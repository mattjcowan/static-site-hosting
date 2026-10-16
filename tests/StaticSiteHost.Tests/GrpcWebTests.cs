using System.Text;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;

namespace StaticSiteHost.Tests;

public class GrpcWebTests
{
    [Theory]
    [InlineData("application/grpc-web", "Binary", "application/grpc")]
    [InlineData("application/grpc-web+proto", "Binary", "application/grpc+proto")]
    [InlineData("Application/GRPC-Web+Proto; charset=utf-8", "Binary", "application/grpc+proto")]
    [InlineData("application/grpc-web-text", "Text", "application/grpc")]
    [InlineData("application/grpc-web-text+proto", "Text", "application/grpc+proto")]
    [InlineData("application/grpc-web+json", "Binary", "application/grpc+json")]
    public void Grpc_web_requests_are_recognised_with_the_grpc_content_type_they_become(string contentType, string mode, string grpc)
    {
        Assert.Equal(Enum.Parse<GrpcWebMode>(mode), GrpcWeb.ModeOf(contentType, out var grpcContentType));
        Assert.Equal(grpc, grpcContentType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("application/grpc")]
    [InlineData("application/grpc+proto")]
    [InlineData("application/grpc-webby")]
    [InlineData("application/json")]
    public void Other_requests_are_not_grpc_web(string? contentType) =>
        Assert.Equal(GrpcWebMode.None, GrpcWeb.ModeOf(contentType, out _));

    [Theory]
    [InlineData("application/grpc", true)]
    [InlineData("application/grpc+proto", true)]
    [InlineData("application/grpc; charset=utf-8", true)]
    [InlineData("application/grpc-web", false)]
    [InlineData("application/grpcx", false)]
    [InlineData(null, false)]
    public void Grpc_itself_is_recognised(string? contentType, bool expected) =>
        Assert.Equal(expected, GrpcWeb.IsGrpc(contentType));

    [Theory]
    [InlineData("application/grpc", "Binary", "application/grpc-web")]
    [InlineData("application/grpc+proto", "Binary", "application/grpc-web+proto")]
    [InlineData("application/grpc+proto", "Text", "application/grpc-web-text+proto")]
    [InlineData("text/html", "Binary", null)]
    [InlineData("application/grpcx", "Binary", null)]
    [InlineData(null, "Text", null)]
    public void Only_a_grpc_answer_gets_a_grpc_web_content_type(string? response, string mode, string? expected) =>
        Assert.Equal(expected, GrpcWeb.WebContentTypeFor(response, Enum.Parse<GrpcWebMode>(mode)));

    [Fact]
    public void The_trailers_frame_is_flagged_sized_and_lower_case()
    {
        var frame = GrpcWeb.TrailersFrame(
        [
            KeyValuePair.Create("Grpc-Status", (IEnumerable<string>)["0"]),
            KeyValuePair.Create("x-multi", (IEnumerable<string>)["a", "b"])
        ]);

        const string block = "grpc-status:0\r\nx-multi:a\r\nx-multi:b\r\n";
        Assert.Equal(new byte[] { 0x80, 0, 0, 0, (byte)block.Length }, frame[..5]);
        Assert.Equal(block, Encoding.ASCII.GetString(frame, 5, frame.Length - 5));
    }

    [Fact]
    public void The_trailers_frame_keeps_the_bytes_it_was_given_and_leaves_out_values_with_line_breaks()
    {
        // "é" in UTF-8, as HttpClient hands it over: two Latin-1 characters.
        var utf8 = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("café"));
        var frame = GrpcWeb.TrailersFrame(
        [
            KeyValuePair.Create("grpc-message", (IEnumerable<string>)[utf8]),
            KeyValuePair.Create("x-forged", (IEnumerable<string>)["a\r\ngrpc-status:0"])
        ]);

        Assert.Equal("grpc-message:café\r\n", Encoding.UTF8.GetString(frame, 5, frame.Length - 5));
    }

    /// <summary>Hands out what it holds a few bytes at a time, as a slow connection would.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }

    private static async Task<byte[]> DecodeAsync(string text, int chunk = 4096)
    {
        using var stream = new GrpcWebTextRequestStream(new TrickleStream(Encoding.ASCII.GetBytes(text), chunk));
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(3)]
    [InlineData(1)]
    public async Task A_text_body_decodes_whole_in_pieces_and_with_line_breaks_however_it_arrives(int chunk)
    {
        var data = Enumerable.Range(0, 20_000).Select(i => (byte)(i * 7)).ToArray();

        Assert.Equal(data, await DecodeAsync(Convert.ToBase64String(data), chunk));
        Assert.Equal(data, await DecodeAsync(Convert.ToBase64String(data, Base64FormattingOptions.InsertLineBreaks), chunk));

        // Three pieces, each padded on its own.
        var pieces = Convert.ToBase64String(data[..1]) + Convert.ToBase64String(data[1..3]) + Convert.ToBase64String(data[3..]);
        Assert.Equal(data, await DecodeAsync(pieces, chunk));
    }

    [Theory]
    [InlineData("AAA!")]
    [InlineData("AAAAAA")]
    [InlineData("A=AA")]
    public async Task A_text_body_that_is_not_base64_is_refused(string text) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => DecodeAsync(text));

    [Fact]
    public async Task The_text_response_passes_writes_through_until_active_and_then_sends_base64()
    {
        using var inner = new MemoryStream();
        var stream = new GrpcWebTextResponseStream(inner);

        await stream.WriteAsync("plain"u8.ToArray());
        stream.Active = true;
        await stream.WriteAsync(new byte[] { 1 });
        await stream.WriteAsync(new byte[] { 2, 3, 4 });

        Assert.Equal("plainAQ==AgME", Encoding.ASCII.GetString(inner.ToArray()));
    }

    [Fact]
    public void Translating_needs_http2_to_the_target()
    {
        var route = new ProxyRoute { Path = "/", Target = "http://grpc:50051", GrpcWeb = true };
        Assert.Empty(ProxyRouteRules.Validate("app.example.com", [route]));

        route.HttpVersion = ProxyHttpVersions.Http11;
        Assert.Contains(ProxyRouteRules.Validate("app.example.com", [route]), error => error.Contains("needs HTTP/2"));
    }
}
