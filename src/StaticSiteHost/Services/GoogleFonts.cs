using System.Net;
using System.Text.RegularExpressions;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>
/// Fonts from Google Fonts, only once an administrator has allowed them (BrandingSettings.AllowGoogleFonts).
/// Either the server downloads every file once and serves them itself, so visitors' browsers never
/// contact Google, or pages load Google's stylesheet directly. Downloading keeps Google's split of a
/// font into ranges of characters (<c>unicode-range</c>), so a page still fetches only what it shows,
/// which is what makes a Chinese font practical.
///
/// Only two addresses are ever fetched: fonts.googleapis.com for the stylesheet and fonts.gstatic.com
/// for the files. A family name is letters, digits, spaces and hyphens, so it cannot steer a request
/// anywhere else.
/// </summary>
public sealed partial class GoogleFonts
{
    public const string HttpClientName = "google-fonts";

    private const string CssHost = "https://fonts.googleapis.com/css2";
    private const string FileHost = "https://fonts.gstatic.com/";

    /// <summary>The weights asked for, most first: a family that lacks some of them is asked again with fewer.</summary>
    private static readonly string[] WeightSets = ["wght@400;500;600;700", "wght@400;700", ""];

    private readonly IHttpClientFactory _http;
    private readonly ILogger<GoogleFonts> _logger;

    public GoogleFonts(IHttpClientFactory http, ILogger<GoogleFonts> logger)
    {
        _http = http;
        _logger = logger;
    }

    [GeneratedRegex(@"@font-face\s*\{(?<body>[^}]*)\}")]
    private static partial Regex FontFace();

    [GeneratedRegex(@"font-style:\s*(?<v>normal|italic)")]
    private static partial Regex Style();

    [GeneratedRegex(@"font-weight:\s*(?<v>[0-9]{3}(?:\s+[0-9]{3})?)")]
    private static partial Regex Weight();

    [GeneratedRegex(@"src:\s*url\((?<v>[^)]+)\)")]
    private static partial Regex Source();

    [GeneratedRegex(@"unicode-range:\s*(?<v>[Uu0-9A-Fa-f+,\s\-?]+)")]
    private static partial Regex Range();

    /// <summary>
    /// Google's stylesheet for a family: its address and text, or why it could not be had. A browser's
    /// user agent asks for WOFF2 files, which every current browser reads.
    /// </summary>
    public async Task<(string? Url, string? Css, string? Error)> StylesheetAsync(string family, CancellationToken ct)
    {
        if (!BrandingStore.IsFamilyName(family)) return (null, null, ServerText.Get("branding.google.error.name"));

        var client = _http.CreateClient(HttpClientName);
        foreach (var weights in WeightSets)
        {
            var url = $"{CssHost}?family={Uri.EscapeDataString(family.Trim()).Replace("%20", "+")}{(weights.Length > 0 ? ":" + weights : "")}&display=swap";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
                using var response = await client.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.BadRequest) continue;
                if (!response.IsSuccessStatusCode)
                    return (null, null, ServerText.Get("branding.google.error.status", new { status = (int)response.StatusCode }));
                return (url, await response.Content.ReadAsStringAsync(ct), null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not reach Google Fonts for {Family}", family);
                return (null, null, ServerText.Get("branding.google.error.unreachable"));
            }
        }

        return (null, null, ServerText.Get("branding.google.error.noFont", new { family = family.Trim() }));
    }

    /// <summary>Downloads every file a Google stylesheet names, with what each covers.</summary>
    public async Task<(List<(byte[] Data, string Weight, string Style, string? UnicodeRange)>? Files, string? Error)> DownloadAsync(
        string css, CancellationToken ct)
    {
        var faces = FontFace().Matches(css)
            .Select(m => m.Groups["body"].Value)
            .Select(body => (
                Url: Source().Match(body) is { Success: true } s ? s.Groups["v"].Value.Trim('\'', '"', ' ') : "",
                Weight: Weight().Match(body) is { Success: true } w ? w.Groups["v"].Value : "400",
                Style: Style().Match(body) is { Success: true } st ? st.Groups["v"].Value : "normal",
                Range: Range().Match(body) is { Success: true } r ? r.Groups["v"].Value.Trim() : null))
            .ToList();

        if (faces.Count == 0) return (null, ServerText.Get("branding.google.error.noFaces"));
        if (faces.Count > BrandingStore.MaxFontFiles) return (null, ServerText.Get("branding.google.error.tooManyFiles", new { count = faces.Count, max = BrandingStore.MaxFontFiles }));
        if (faces.Any(f => !f.Url.StartsWith(FileHost, StringComparison.Ordinal)))
            return (null, ServerText.Get("branding.google.error.elsewhere"));

        // A variable font is one file Google names once per weight: each address is fetched once.
        var client = _http.CreateClient(HttpClientName);
        var downloaded = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        using var tooMuch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(8);

        try
        {
            await Task.WhenAll(faces.Select(f => f.Url).Distinct(StringComparer.Ordinal).Select(async url =>
            {
                await gate.WaitAsync(tooMuch.Token);
                try
                {
                    var data = await client.GetByteArrayAsync(url, tooMuch.Token);
                    // Stops the rest as soon as the font is larger than the library keeps, before it all sits in memory.
                    if (Interlocked.Add(ref total, data.Length) > BrandingStore.MaxFontBytes) await tooMuch.CancelAsync();
                    downloaded[url] = data;
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && Interlocked.Read(ref total) > BrandingStore.MaxFontBytes)
        {
            return (null, ServerText.Get("branding.google.error.large", new { size = BrandingStore.MaxFontBytes / (1024 * 1024) }));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not download a Google Fonts file");
            return (null, ServerText.Get("branding.google.error.download"));
        }

        return (faces.Select(f => (downloaded[f.Url], f.Weight, f.Style, f.Range)).ToList(), null);
    }
}
