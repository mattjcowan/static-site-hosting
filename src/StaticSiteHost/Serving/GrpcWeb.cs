using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace StaticSiteHost.Serving;

/// <summary>The two forms of gRPC-Web a browser sends: the gRPC frames as they are, or in base64.</summary>
internal enum GrpcWebMode
{
    None,

    /// <summary><c>application/grpc-web</c>: the request and response bodies are gRPC's own frames.</summary>
    Binary,

    /// <summary><c>application/grpc-web-text</c>: the same frames, in base64 both ways.</summary>
    Text
}

/// <summary>
/// gRPC-Web, translated to gRPC and back, for a proxy route with <see cref="Models.ProxyRoute.GrpcWeb"/>
/// on. A browser cannot speak gRPC: it needs HTTP/2 trailers, which no browser API exposes, and
/// proxies such as Railway's carry HTTP/1.1 only. gRPC-Web is gRPC's answer, over any HTTP version:
/// the request is gRPC with another content type, and the trailers come back at the end of the body,
/// as one last frame. This is the protocol as https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-WEB.md
/// sets it out, the part Envoy's gRPC-Web filter does:
///
///   * request: <c>application/grpc-web[-text][+proto]</c> becomes <c>application/grpc[+proto]</c>,
///     a text body is decoded from base64, and <c>TE: trailers</c> is sent, over HTTP/2;
///   * response: <c>application/grpc…</c> becomes <c>application/grpc-web…</c>, and the trailers
///     (<c>grpc-status</c>, <c>grpc-message</c>…) are written after the body as a frame whose flag
///     byte is 0x80; a text response is base64 throughout.
///
/// A response that is not gRPC (a 404 page from the target, say) passes through as it is.
/// </summary>
internal static class GrpcWeb
{
    private const string GrpcWebText = "application/grpc-web-text";
    private const string GrpcWebBinary = "application/grpc-web";
    private const string Grpc = "application/grpc";

    /// <summary>The flag byte of the frame that carries the trailers.</summary>
    internal const byte TrailersFlag = 0x80;

    /// <summary>
    /// Whether a request is gRPC-Web, and the gRPC content type it becomes: the same message format
    /// after the <c>+</c> (<c>+proto</c>, <c>+json</c>), or none.
    /// </summary>
    public static GrpcWebMode ModeOf(string? contentType, out string grpcContentType)
    {
        grpcContentType = "";
        var value = MediaType(contentType);

        var (mode, rest) =
            value.StartsWith(GrpcWebText, StringComparison.OrdinalIgnoreCase) ? (GrpcWebMode.Text, value[GrpcWebText.Length..]) :
            value.StartsWith(GrpcWebBinary, StringComparison.OrdinalIgnoreCase) ? (GrpcWebMode.Binary, value[GrpcWebBinary.Length..]) :
            (GrpcWebMode.None, "");

        if (mode == GrpcWebMode.None || (rest.Length > 0 && rest[0] != '+')) return GrpcWebMode.None;

        grpcContentType = Grpc + rest.ToLowerInvariant();
        return mode;
    }

    /// <summary>
    /// Whether a request is gRPC itself (<c>application/grpc</c> or <c>application/grpc+…</c>), which
    /// goes to the target over HTTP/2 whatever the route's HTTP version says, short of 1.1 only.
    /// </summary>
    public static bool IsGrpc(string? contentType)
    {
        var value = MediaType(contentType);
        return value.StartsWith(Grpc, StringComparison.OrdinalIgnoreCase) &&
               (value.Length == Grpc.Length || value[Grpc.Length] == '+');
    }

    /// <summary>
    /// The gRPC-Web content type for a gRPC response, or null when the response is not gRPC and is
    /// to pass through untouched.
    /// </summary>
    public static string? WebContentTypeFor(string? responseContentType, GrpcWebMode mode)
    {
        var value = MediaType(responseContentType);
        if (!value.StartsWith(Grpc, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = value[Grpc.Length..];
        if (rest.Length > 0 && rest[0] != '+') return null;

        return (mode == GrpcWebMode.Text ? GrpcWebText : GrpcWebBinary) + rest.ToLowerInvariant();
    }

    /// <summary>The media type alone: no parameters, no spaces.</summary>
    private static string MediaType(string? contentType)
    {
        var value = contentType ?? "";
        var semicolon = value.IndexOf(';');
        return (semicolon < 0 ? value : value[..semicolon]).Trim();
    }

    /// <summary>
    /// The frame that ends a gRPC-Web response: the flag byte, the length as four bytes, big-endian,
    /// then each trailer as <c>name:value\r\n</c>, with the name in lower case as the protocol says.
    /// Bytes go out as they came (Latin-1, as HttpClient read them), so a status message in UTF-8
    /// arrives intact; a value with a line break in it would start a trailer of its own, and is left out.
    /// </summary>
    public static byte[] TrailersFrame(IEnumerable<KeyValuePair<string, IEnumerable<string>>> trailers)
    {
        var text = new StringBuilder();
        foreach (var (name, values) in trailers)
        {
            foreach (var value in values)
            {
                if (value.AsSpan().IndexOfAny('\r', '\n') >= 0) continue;
                text.Append(name.ToLowerInvariant()).Append(':').Append(value).Append("\r\n");
            }
        }

        var block = Encoding.Latin1.GetBytes(text.ToString());
        var frame = new byte[5 + block.Length];
        frame[0] = TrailersFlag;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), block.Length);
        block.CopyTo(frame, 5);
        return frame;
    }
}

/// <summary>
/// A <c>grpc-web-text</c> request body as the gRPC frames it encodes, decoded as it is read, so a
/// client stream is never held whole. Base64 that a client sent in several pieces, each with its own
/// padding, decodes too, as the protocol allows; line breaks and spaces are skipped.
/// </summary>
internal sealed class GrpcWebTextRequestStream : Stream
{
    private readonly Stream _inner;
    private const int EncodedCapacity = 16 * 1024;

    // Every four characters decode to three bytes at most.
    private const int DecodedCapacity = EncodedCapacity / 4 * 3;

    private readonly byte[] _encoded = new byte[EncodedCapacity];
    private int _pending;
    private readonly byte[] _decoded = new byte[DecodedCapacity];
    private int _decodedStart;
    private int _decodedEnd;
    private bool _ended;

    public GrpcWebTextRequestStream(Stream inner) => _inner = inner;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_decodedStart == _decodedEnd)
        {
            if (_ended) return 0;

            var read = await _inner.ReadAsync(_encoded.AsMemory(_pending), cancellationToken);
            if (read == 0)
            {
                _ended = true;
                if (_pending > 0) throw new InvalidDataException("The gRPC-Web request body ends partway through its base64.");
                return 0;
            }

            _pending = SkipWhitespace(_encoded.AsSpan(0, _pending + read));
            Decode();
        }

        var count = Math.Min(buffer.Length, _decodedEnd - _decodedStart);
        _decoded.AsSpan(_decodedStart, count).CopyTo(buffer.Span);
        _decodedStart += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Moves what is not whitespace to the front, and returns how much of it there is.</summary>
    private static int SkipWhitespace(Span<byte> text)
    {
        var kept = 0;
        foreach (var b in text)
        {
            if (b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') continue;
            text[kept++] = b;
        }
        return kept;
    }

    /// <summary>
    /// Decodes every whole group of four characters waiting, a run at a time: a run ends at a group
    /// with padding, since what follows it is a new piece. The rest waits for the next read.
    /// </summary>
    private void Decode()
    {
        var whole = _pending / 4 * 4;
        _decodedStart = _decodedEnd = 0;

        var start = 0;
        for (var group = 0; group < whole; group += 4)
        {
            var padded = _encoded[group + 3] == (byte)'=';
            if (!padded && group + 4 < whole) continue;

            var run = _encoded.AsSpan(start, group + 4 - start);
            var status = Base64.DecodeFromUtf8(run, _decoded.AsSpan(_decodedEnd), out _, out var written, isFinalBlock: true);
            if (status != OperationStatus.Done) throw new InvalidDataException("The gRPC-Web request body is not valid base64.");

            _decodedEnd += written;
            start = group + 4;
        }

        _encoded.AsSpan(whole, _pending - whole).CopyTo(_encoded);
        _pending -= whole;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// The response body of a <c>grpc-web-text</c> request: once <see cref="Active"/> (the target answered
/// with gRPC), everything written is sent in base64, each write padded on its own so a streamed
/// message reaches the browser whole and at once. Until then it passes writes through, so an error
/// page from the target arrives as it was sent.
/// </summary>
internal sealed class GrpcWebTextResponseStream : Stream
{
    private readonly Stream _inner;

    public GrpcWebTextResponseStream(Stream inner) => _inner = inner;

    public Stream Inner => _inner;

    public bool Active { get; set; }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!Active || buffer.Length == 0)
        {
            await _inner.WriteAsync(buffer, cancellationToken);
            return;
        }

        var size = Base64.GetMaxEncodedToUtf8Length(buffer.Length);
        var encoded = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            Base64.EncodeToUtf8(buffer.Span, encoded, out _, out var written);
            await _inner.WriteAsync(encoded.AsMemory(0, written), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encoded);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override void Flush() => _inner.Flush();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
