using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StaticSiteHost.Tls;

/// <summary>A refusal from the ACME server, with its problem type and detail (RFC 8555 §6.7).</summary>
public sealed class AcmeException(string message, string? type = null) : Exception(message)
{
    public string? Type { get; } = type;
}

/// <summary>
/// How an ACME order proves control of its names: one kind of challenge (<see cref="Type"/>),
/// prepared for every name before any is answered, then removed.
/// </summary>
public interface IAcmeChallengeSolver
{
    /// <summary><c>http-01</c> or <c>dns-01</c>.</summary>
    string Type { get; }

    /// <summary>Puts the proof for one name in place. <paramref name="identifier"/> is the name without any <c>*.</c>.</summary>
    Task PrepareAsync(string identifier, string token, string keyAuthorization, CancellationToken ct);

    /// <summary>Waits, once every name is prepared, until the ACME server can see the proofs.</summary>
    Task ReadyAsync(CancellationToken ct);

    /// <summary>Takes every proof away again, whether the order worked or not.</summary>
    Task CleanupAsync(CancellationToken ct);
}

/// <summary>HTTP-01: the server answers /.well-known/acme-challenge/{token} on port 80 (<see cref="AcmeChallenges"/>).</summary>
public sealed class HttpChallengeSolver(AcmeChallenges challenges) : IAcmeChallengeSolver
{
    private readonly List<string> _published = [];

    public string Type => "http-01";

    public Task PrepareAsync(string identifier, string token, string keyAuthorization, CancellationToken ct)
    {
        challenges.Publish(token, keyAuthorization);
        _published.Add(token);
        return Task.CompletedTask;
    }

    public Task ReadyAsync(CancellationToken ct) => Task.CompletedTask;

    public Task CleanupAsync(CancellationToken ct)
    {
        foreach (var token in _published) challenges.Withdraw(token);
        _published.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>
/// A small ACME client (RFC 8555): an account, an order for some host names, their HTTP-01 or
/// DNS-01 challenges, and the certificate. Enough for Let's Encrypt and any server that follows the RFC
/// (step-ca, Pebble), with nothing but the framework's own cryptography: every request is a JWS
/// signed with the account's ECDSA P-256 key (ES256).
///
/// Not thread-safe: one order at a time, which is how <see cref="CertificateManager"/> uses it.
/// </summary>
public sealed class AcmeClient
{
    private const string BadNonce = "urn:ietf:params:acme:error:badNonce";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _directoryUrl;
    private readonly ECDsa _accountKey;
    private string? _accountUrl;
    private Directory? _directory;
    private string? _nonce;

    /// <param name="accountUrl">The account's URL from an earlier <see cref="EnsureAccountAsync"/>, if known.</param>
    public AcmeClient(HttpClient http, string directoryUrl, ECDsa accountKey, string? accountUrl = null)
    {
        _http = http;
        _directoryUrl = directoryUrl;
        _accountKey = accountKey;
        _accountUrl = accountUrl;
    }

    /// <summary>How long to wait between looks at a pending authorization or order.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long an order may stay pending or processing before it is given up.</summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromMinutes(3);

    // ---------------------------------------------------------------- the whole order

    /// <summary>
    /// Orders a certificate for <paramref name="hosts"/> (a wildcard is <c>*.</c> and the name) and
    /// returns its chain, as PEM. Every name's challenge is put in place through
    /// <paramref name="solver"/> first, then answered, and taken away again at the end.
    /// </summary>
    /// <param name="csr">The certificate request, DER, for exactly these host names.</param>
    public async Task<string> OrderCertificateAsync(IReadOnlyList<string> hosts, byte[] csr, IAcmeChallengeSolver solver, CancellationToken ct)
    {
        var (orderUrl, order) = await NewOrderAsync(hosts, ct);
        try
        {
            var pending = new List<(string Url, Challenge Challenge)>();
            foreach (var authorizationUrl in order.Authorizations)
            {
                var authorization = await PostAsGetAsync<Authorization>(authorizationUrl, ct);
                if (authorization.Status == "valid") continue;

                var challenge = authorization.Challenges.FirstOrDefault(c => c.Type == solver.Type)
                                ?? throw new AcmeException($"The ACME server offers no {solver.Type} check for {authorization.Identifier?.Value}.");

                await solver.PrepareAsync(authorization.Identifier?.Value ?? "", challenge.Token, KeyAuthorization(challenge.Token), ct);
                pending.Add((authorizationUrl, challenge));
            }

            if (pending.Count > 0) await solver.ReadyAsync(ct);

            foreach (var (_, challenge) in pending) await PostAsync<Challenge>(challenge.Url, new { }, ct);
            foreach (var (authorizationUrl, _) in pending)
            {
                await PollAsync(authorizationUrl, (Authorization a) => a.Status, "pending", ct, done: a =>
                {
                    if (a.Status == "valid") return;
                    var failed = a.Challenges.FirstOrDefault(c => c.Type == solver.Type)?.Error;
                    throw new AcmeException(
                        $"{a.Identifier?.Value}: {failed?.Detail ?? "the check did not pass"}", failed?.Type);
                });
            }

            order = await PostAsync<Order>(order.Finalize, new { csr = Base64Url(csr) }, ct);
            order = await PollAsync(orderUrl, (Order o) => o.Status, ["pending", "ready", "processing"], ct, done: o =>
            {
                if (o.Status != "valid") throw new AcmeException($"The order ended as '{o.Status}': {o.Error?.Detail}", o.Error?.Type);
            }, first: order);

            if (order.Certificate is null) throw new AcmeException("The order is valid but names no certificate.");
            return await DownloadAsync(order.Certificate, ct);
        }
        finally
        {
            await solver.CleanupAsync(CancellationToken.None);
        }
    }

    // ---------------------------------------------------------------- the steps

    /// <summary>
    /// The account for this key: found or created, agreeing to the server's terms. Returns its URL,
    /// which identifies it from then on.
    /// </summary>
    public async Task<string> EnsureAccountAsync(string? email, CancellationToken ct)
    {
        if (_accountUrl is not null) return _accountUrl;

        var directory = await DirectoryAsync(ct);
        var payload = new
        {
            termsOfServiceAgreed = true,
            contact = string.IsNullOrWhiteSpace(email) ? null : new[] { "mailto:" + email.Trim() }
        };

        using var response = await SendAsync(directory.NewAccount, payload, useJwk: true, ct);
        _accountUrl = response.Headers.Location?.ToString()
                      ?? throw new AcmeException("The ACME server created an account but did not say where it is.");
        return _accountUrl;
    }

    private async Task<(string Url, Order Order)> NewOrderAsync(IReadOnlyList<string> hosts, CancellationToken ct)
    {
        var directory = await DirectoryAsync(ct);
        using var response = await SendAsync(directory.NewOrder,
            new { identifiers = hosts.Select(h => new { type = "dns", value = h }) }, useJwk: false, ct);

        var url = response.Headers.Location?.ToString() ?? throw new AcmeException("The ACME server made an order but did not say where it is.");
        return (url, await ReadAsync<Order>(response, ct));
    }

    private async Task<string> DownloadAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, payload: null, useJwk: false, ct, accept: "application/pem-certificate-chain");
        var pem = await response.Content.ReadAsStringAsync(ct);
        if (!pem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            throw new AcmeException("The ACME server sent something other than a certificate chain.");
        return pem;
    }

    /// <summary>Looks at an authorization or order until it leaves its waiting states, then hands it to <paramref name="done"/>.</summary>
    private async Task<T> PollAsync<T>(
        string url, Func<T, string> status, IReadOnlyCollection<string> waiting, CancellationToken ct, Action<T> done, T? first = default)
    {
        var deadline = DateTimeOffset.UtcNow + PollTimeout;
        var current = first;

        while (current is null || waiting.Contains(status(current)))
        {
            if (DateTimeOffset.UtcNow > deadline) throw new AcmeException($"The ACME server took longer than {PollTimeout.TotalMinutes:0} minutes.");
            if (current is not null) await Task.Delay(PollInterval, ct);
            current = await PostAsGetAsync<T>(url, ct);
        }

        done(current);
        return current;
    }

    private Task<T> PollAsync<T>(string url, Func<T, string> status, string waiting, CancellationToken ct, Action<T> done) =>
        PollAsync(url, status, [waiting], ct, done);

    // ---------------------------------------------------------------- JWS

    /// <summary>What the server must find at /.well-known/acme-challenge/{token}: the token and the account key's thumbprint.</summary>
    public string KeyAuthorization(string token) => token + "." + Thumbprint(_accountKey);

    /// <summary>What a DNS-01 TXT record at <c>_acme-challenge.{name}</c> holds: the key authorization's SHA-256, base64url.</summary>
    public static string DnsTxtValue(string keyAuthorization) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(keyAuthorization)));

    /// <summary>The account key's JWK thumbprint (RFC 7638): SHA-256 of its members in a fixed order.</summary>
    public static string Thumbprint(ECDsa key)
    {
        var (x, y) = Coordinates(key);
        var canonical = $$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{y}}"}""";
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static (string X, string Y) Coordinates(ECDsa key)
    {
        var point = key.ExportParameters(includePrivateParameters: false).Q;
        return (Base64Url(point.X!), Base64Url(point.Y!));
    }

    private Task<T> PostAsGetAsync<T>(string url, CancellationToken ct) => PostAsync<T>(url, payload: null, ct);

    private async Task<T> PostAsync<T>(string url, object? payload, CancellationToken ct)
    {
        using var response = await SendAsync(url, payload ?? PostAsGet, useJwk: false, ct);
        return await ReadAsync<T>(response, ct);
    }

    /// <summary>Marks a POST-as-GET: an empty payload, not an empty object.</summary>
    private static readonly object PostAsGet = new();

    /// <summary>
    /// Signs and sends one request. The first is signed with the key itself (a new account); the
    /// rest name the account. A rejected nonce is retried once with the fresh one that came back.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        string url, object? payload, bool useJwk, CancellationToken ct, string accept = "application/json")
    {
        for (var attempt = 0; ; attempt++)
        {
            var nonce = _nonce ?? await NewNonceAsync(ct);
            _nonce = null;

            object header = useJwk
                ? new { alg = "ES256", jwk = Jwk(), nonce, url }
                : new { alg = "ES256", kid = _accountUrl ?? throw new InvalidOperationException("No ACME account yet."), nonce, url };

            var protectedPart = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header, Json));
            var payloadPart = payload is null || ReferenceEquals(payload, PostAsGet) ? "" : Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
            // .NET signs ECDSA as r||s (IEEE P1363), which is what JWS's ES256 is.
            var signature = _accountKey.SignData(Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart), HashAlgorithmName.SHA256);

            var body = JsonSerializer.Serialize(new { @protected = protectedPart, payload = payloadPart, signature = Base64Url(signature) });
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
            request.Headers.Accept.ParseAdd(accept);

            var response = await _http.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Replay-Nonce", out var nonces)) _nonce = nonces.FirstOrDefault();
            if (response.IsSuccessStatusCode) return response;

            var problem = await ProblemAsync(response, ct);
            response.Dispose();
            if (problem.Type == BadNonce && attempt == 0) continue;
            throw new AcmeException(problem.Detail ?? $"The ACME server answered {(int)response.StatusCode}.", problem.Type);
        }
    }

    private object Jwk()
    {
        var (x, y) = Coordinates(_accountKey);
        return new { crv = "P-256", kty = "EC", x, y };
    }

    private async Task<string> NewNonceAsync(CancellationToken ct)
    {
        var directory = await DirectoryAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Head, directory.NewNonce);
        using var response = await _http.SendAsync(request, ct);
        return response.Headers.TryGetValues("Replay-Nonce", out var nonces) && nonces.FirstOrDefault() is { } nonce
            ? nonce
            : throw new AcmeException("The ACME server gave no nonce.");
    }

    private async Task<Directory> DirectoryAsync(CancellationToken ct)
    {
        if (_directory is not null) return _directory;

        using var response = await _http.GetAsync(_directoryUrl, ct);
        if (!response.IsSuccessStatusCode)
            throw new AcmeException($"The ACME directory at {_directoryUrl} answered {(int)response.StatusCode}.");
        return _directory = await ReadAsync<Directory>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new AcmeException("The ACME server sent an empty answer.");

    private static async Task<Problem> ProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<Problem>(Json, ct) ?? new Problem(null, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new Problem(null, $"The ACME server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---------------------------------------------------------------- the server's objects

    private sealed record Directory(string NewNonce, string NewAccount, string NewOrder);

    private sealed record Problem(string? Type, string? Detail);

    private sealed record Identifier(string Type, string Value);

    private sealed record Challenge(string Type, string Url, string Token, string? Status, Problem? Error);

    private sealed record Authorization(string Status, Identifier? Identifier, List<Challenge> Challenges);

    private sealed record Order(string Status, List<string> Authorizations, string Finalize, string? Certificate, Problem? Error);
}

/// <summary>The certificate request an order is finalized with: a new key, and the host names as SANs.</summary>
public static class CertificateRequests
{
    /// <summary>A new ECDSA P-256 key and a CSR (DER) for <paramref name="hosts"/>.</summary>
    public static (ECDsa Key, byte[] Csr) Create(IReadOnlyList<string> hosts)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName($"CN={hosts[0]}"), key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        foreach (var host in hosts) names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        return (key, request.CreateSigningRequest());
    }
}
