using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StaticSiteHost.Services.Dns;

/// <summary>A refusal from Cloudflare's API, with its first error.</summary>
public sealed class CloudflareException(string message, HttpStatusCode status = 0) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>A zone the token can see: its id and domain.</summary>
public sealed record CloudflareZone(string Id, string Name);

/// <summary>A DNS record as Cloudflare has it.</summary>
public sealed record CloudflareRecord(string Id, string Type, string Name, string Content, bool Proxied, string? Comment);

/// <summary>A Cloudflare Origin CA certificate: its id (to revoke it), the PEM, and when it expires.</summary>
public sealed record CloudflareOriginCertificate(string Id, string CertificatePem, DateTimeOffset? ExpiresOn);

/// <summary>
/// What this server asks of Cloudflare's API: zones, DNS records and Origin CA certificates. An
/// interface so the parts that use it can be tested without Cloudflare.
/// </summary>
public interface ICloudflareApi
{
    /// <summary>Whether the token is a working one; null when it is, else why not.</summary>
    Task<string?> VerifyAsync(string token, CancellationToken ct);

    Task<IReadOnlyList<CloudflareZone>> ZonesAsync(string token, CancellationToken ct);

    Task<IReadOnlyList<CloudflareRecord>> RecordsAsync(string token, string zoneId, string name, CancellationToken ct);

    Task<CloudflareRecord> CreateRecordAsync(string token, string zoneId, string type, string name, string content, bool proxied, string? comment, CancellationToken ct);

    Task<CloudflareRecord> UpdateRecordAsync(string token, string zoneId, string recordId, string content, bool proxied, CancellationToken ct);

    Task DeleteRecordAsync(string token, string zoneId, string recordId, CancellationToken ct);

    /// <summary>Whether the token may make Origin CA certificates in the zone: Zone, SSL and Certificates, Edit.</summary>
    Task<bool> CanMakeOriginCertificatesAsync(string token, string zoneId, CancellationToken ct);

    Task<CloudflareOriginCertificate> CreateOriginCertificateAsync(string token, IReadOnlyList<string> hostnames, string csrPem, CancellationToken ct);

    Task RevokeOriginCertificateAsync(string token, string certificateId, CancellationToken ct);
}

/// <summary>
/// Cloudflare's API v4 over HTTPS, with an API token (<c>Authorization: Bearer</c>). Each call
/// takes the token, so nothing here holds it.
/// </summary>
public sealed class CloudflareApi : ICloudflareApi
{
    /// <summary>The name of the HttpClient that talks to Cloudflare.</summary>
    public const string HttpClientName = "cloudflare";

    public const string BaseUrl = "https://api.cloudflare.com/client/v4/";

    /// <summary>Origin CA certificates are made for 15 years, the longest Cloudflare offers: they never need renewing in practice.</summary>
    public const int OriginValidityDays = 5475;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _http;

    public CloudflareApi(IHttpClientFactory http) => _http = http;

    public async Task<string?> VerifyAsync(string token, CancellationToken ct)
    {
        try
        {
            var result = await SendAsync<TokenStatus>(token, HttpMethod.Get, "user/tokens/verify", null, ct);
            return result.Status == "active" ? null : $"The token is {result.Status}.";
        }
        catch (CloudflareException ex)
        {
            return ex.Message;
        }
    }

    public async Task<IReadOnlyList<CloudflareZone>> ZonesAsync(string token, CancellationToken ct)
    {
        var zones = new List<CloudflareZone>();
        for (var page = 1; ; page++)
        {
            var (items, info) = await SendPageAsync<Zone>(token, $"zones?per_page=50&page={page}", ct);
            zones.AddRange(items.Select(z => new CloudflareZone(z.Id, z.Name.ToLowerInvariant())));
            if (info is null || page >= info.TotalPages) return zones;
        }
    }

    public async Task<IReadOnlyList<CloudflareRecord>> RecordsAsync(string token, string zoneId, string name, CancellationToken ct)
    {
        var (items, _) = await SendPageAsync<Record>(token,
            $"zones/{Uri.EscapeDataString(zoneId)}/dns_records?per_page=100&name.exact={Uri.EscapeDataString(name)}", ct);
        return items.Select(ToRecord).ToList();
    }

    public async Task<CloudflareRecord> CreateRecordAsync(
        string token, string zoneId, string type, string name, string content, bool proxied, string? comment, CancellationToken ct) =>
        ToRecord(await SendAsync<Record>(token, HttpMethod.Post, $"zones/{Uri.EscapeDataString(zoneId)}/dns_records",
            new { type, name, content, ttl = 1, proxied = type is "A" or "AAAA" or "CNAME" ? proxied : (bool?)null, comment }, ct));

    public async Task<CloudflareRecord> UpdateRecordAsync(string token, string zoneId, string recordId, string content, bool proxied, CancellationToken ct) =>
        ToRecord(await SendAsync<Record>(token, HttpMethod.Patch,
            $"zones/{Uri.EscapeDataString(zoneId)}/dns_records/{Uri.EscapeDataString(recordId)}", new { content, proxied }, ct));

    public async Task DeleteRecordAsync(string token, string zoneId, string recordId, CancellationToken ct)
    {
        try
        {
            await SendAsync<JsonElement>(token, HttpMethod.Delete, $"zones/{Uri.EscapeDataString(zoneId)}/dns_records/{Uri.EscapeDataString(recordId)}", null, ct);
        }
        catch (CloudflareException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            // Already gone, which is what was wanted.
        }
    }

    public async Task<bool> CanMakeOriginCertificatesAsync(string token, string zoneId, CancellationToken ct)
    {
        try
        {
            await SendPageAsync<JsonElement>(token, $"certificates?zone_id={Uri.EscapeDataString(zoneId)}", ct);
            return true;
        }
        catch (CloudflareException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            return false;
        }
    }

    public async Task<CloudflareOriginCertificate> CreateOriginCertificateAsync(
        string token, IReadOnlyList<string> hostnames, string csrPem, CancellationToken ct)
    {
        var result = await SendAsync<OriginCertificate>(token, HttpMethod.Post, "certificates", new
        {
            hostnames,
            csr = csrPem,
            request_type = "origin-ecc",
            requested_validity = OriginValidityDays
        }, ct);

        if (string.IsNullOrWhiteSpace(result.Certificate)) throw new CloudflareException("Cloudflare made the certificate but did not send it.");
        return new CloudflareOriginCertificate(result.Id, result.Certificate,
            DateTimeOffset.TryParse(result.ExpiresOn, out var expires) ? expires : null);
    }

    public async Task RevokeOriginCertificateAsync(string token, string certificateId, CancellationToken ct)
    {
        try
        {
            await SendAsync<JsonElement>(token, HttpMethod.Delete, $"certificates/{Uri.EscapeDataString(certificateId)}", null, ct);
        }
        catch (CloudflareException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            // Already revoked.
        }
    }

    // ---------------------------------------------------------------- the envelope

    private async Task<T> SendAsync<T>(string token, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var envelope = await SendEnvelopeAsync<T>(token, method, path, body, ct);
        return envelope.Result ?? throw new CloudflareException("Cloudflare sent an empty answer.");
    }

    private async Task<(List<T> Items, ResultInfo? Info)> SendPageAsync<T>(string token, string path, CancellationToken ct)
    {
        var envelope = await SendEnvelopeAsync<List<T>>(token, HttpMethod.Get, path, null, ct);
        return (envelope.Result ?? [], envelope.ResultInfo);
    }

    private async Task<Envelope<T>> SendEnvelopeAsync<T>(string token, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);

        using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);
        Envelope<T>? envelope;
        try
        {
            envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(Json, ct);
        }
        catch (JsonException)
        {
            throw new CloudflareException($"Cloudflare answered {(int)response.StatusCode} {response.ReasonPhrase}.", response.StatusCode);
        }

        if (envelope is null || !envelope.Success || !response.IsSuccessStatusCode)
        {
            var error = envelope?.Errors?.FirstOrDefault();
            throw new CloudflareException(
                error is null ? $"Cloudflare answered {(int)response.StatusCode} {response.ReasonPhrase}." : $"{error.Message} (Cloudflare error {error.Code})",
                response.StatusCode);
        }

        return envelope;
    }

    private static CloudflareRecord ToRecord(Record r) => new(r.Id, r.Type, r.Name.ToLowerInvariant(), r.Content, r.Proxied, r.Comment);

    private sealed record Envelope<T>(bool Success, List<ApiError>? Errors, T? Result, [property: JsonPropertyName("result_info")] ResultInfo? ResultInfo);

    private sealed record ApiError(int Code, string Message);

    private sealed record ResultInfo([property: JsonPropertyName("total_pages")] int TotalPages);

    private sealed record TokenStatus(string Status);

    private sealed record Zone(string Id, string Name);

    private sealed record Record(string Id, string Type, string Name, string Content, bool Proxied, string? Comment);

    private sealed record OriginCertificate(string Id, string? Certificate, [property: JsonPropertyName("expires_on")] string? ExpiresOn);
}
