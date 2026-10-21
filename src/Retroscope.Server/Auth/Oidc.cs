using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Core.Security;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;

namespace Retroscope.Server.Auth;

public enum OidcProvider {
    Google,
    Entra,
}

public static class OidcProviders {
    public static string Name(OidcProvider provider) => provider switch {
        OidcProvider.Google => "google",
        OidcProvider.Entra => "entra",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static bool TryParse(string? value, out OidcProvider provider) {
        switch (value) {
            case "google":
                provider = OidcProvider.Google;
                return true;
            case "entra":
                provider = OidcProvider.Entra;
                return true;
            default:
                provider = default;
                return false;
        }
    }

    public static AuthMethod Method(OidcProvider provider) =>
        provider == OidcProvider.Google ? AuthMethod.Google : AuthMethod.Entra;

    public static OwnershipProof Proof(OidcProvider provider) =>
        provider == OidcProvider.Google ? OwnershipProof.Google : OwnershipProof.Entra;
}

/// <summary>One registered OIDC client: its credentials and the provider's two endpoints.</summary>
public sealed record OidcClient(string ClientId, string ClientSecret, Uri AuthorizeEndpoint, Uri TokenEndpoint);

/// <summary>
/// OIDC settings from the environment (§e, §i.1). Endpoints are static, with no discovery fetch. A provider is
/// enabled when its client id and secret are set; Entra also needs a non-empty <c>ENTRA_ALLOWED_TENANTS</c> (S5).
/// </summary>
public sealed record OidcOptions {
    public const string GoogleAuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string GoogleTokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string EntraAuthority = "https://login.microsoftonline.com";
    public const string GooglePictureHost = "lh3.googleusercontent.com";

    public OidcClient? Google { get; init; }

    public OidcClient? Entra { get; init; }

    /// <summary>Lower-case tenant ids whose users may sign in with Entra.</summary>
    public IReadOnlySet<string> EntraAllowedTenants { get; init; } = new HashSet<string>();

    /// <summary>Per tenant id, the lower-case email domains whose Entra <c>email</c> claim is trusted.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> EntraTenantDomains { get; init; } =
        new Dictionary<string, IReadOnlySet<string>>();

    /// <summary>The handler behind token exchange and the picture fetch; tests route it to the fake provider.</summary>
    public Func<HttpMessageHandler> CreateHandler { get; init; } = DefaultHandler;

    public OidcClient? Client(OidcProvider provider) => provider == OidcProvider.Google ? Google : Entra;

    public static OidcOptions FromConfiguration(IConfiguration configuration) {
        var google = ReadClient(configuration, "GOOGLE", () => (
            new Uri(Setting(configuration, "GOOGLE_AUTHORIZE_URL") ?? GoogleAuthorizeEndpoint),
            new Uri(Setting(configuration, "GOOGLE_TOKEN_URL") ?? GoogleTokenEndpoint)));

        var tenants = Split(configuration["ENTRA_ALLOWED_TENANTS"], ',').Select(x => x.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var tenant = Setting(configuration, "ENTRA_TENANT") ?? "organizations";
        if (!tenant.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')) {
            throw new InvalidOperationException("ENTRA_TENANT must be 'organizations' or a tenant id.");
        }
        var authority = (Setting(configuration, "ENTRA_AUTHORITY_URL") ?? EntraAuthority).TrimEnd('/');
        var entra = ReadClient(configuration, "ENTRA", () => (
            new Uri($"{authority}/{tenant}/oauth2/v2.0/authorize"),
            new Uri($"{authority}/{tenant}/oauth2/v2.0/token")));

        return new OidcOptions {
            Google = google,
            Entra = tenants.Count == 0 ? null : entra,
            EntraAllowedTenants = tenants,
            EntraTenantDomains = ParseTenantDomains(configuration["ENTRA_TENANT_DOMAINS"]),
        };
    }

    /// <summary>Parses <c>tid=acme.com|acme.io,tid2=example.org</c>.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ParseTenantDomains(string? value) {
        var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var entry in Split(value, ',')) {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0) {
                throw new InvalidOperationException("ENTRA_TENANT_DOMAINS entries must look like 'tid=domain1|domain2'.");
            }
            var domains = Split(parts[1], '|').Select(x => x.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            if (domains.Count == 0) {
                throw new InvalidOperationException($"ENTRA_TENANT_DOMAINS entry for '{parts[0]}' lists no domain.");
            }
            map[parts[0].ToLowerInvariant()] = domains;
        }
        return map;
    }

    private static OidcClient? ReadClient(IConfiguration configuration, string prefix, Func<(Uri Authorize, Uri Token)> endpoints) {
        var id = Setting(configuration, $"{prefix}_CLIENT_ID");
        var secret = Setting(configuration, $"{prefix}_CLIENT_SECRET");
        if (id is null && secret is null) {
            return null;
        }
        if (id is null || secret is null) {
            throw new InvalidOperationException($"{prefix}_CLIENT_ID and {prefix}_CLIENT_SECRET must be set together.");
        }
        var (authorize, token) = endpoints();
        return new OidcClient(id, secret, authorize, token);
    }

    private static string? Setting(IConfiguration configuration, string name) =>
        string.IsNullOrWhiteSpace(configuration[name]) ? null : configuration[name]!.Trim();

    private static string[] Split(string? value, char separator) =>
        (value ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static SocketsHttpHandler DefaultHandler() => new() {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    };
}

/// <summary>What the browser carries between start and callback, sealed in <c>__Host-rs_oidc</c>.</summary>
/// <param name="LinkUser">Set by the explicit link flow: the signed-in user the identity is linked to.</param>
public sealed record OidcFlow(
    string Provider,
    string State,
    string Nonce,
    string Verifier,
    string Return,
    Guid? LinkUser,
    long ExpiresAt);

public sealed record OidcTokenResponse([property: JsonPropertyName("id_token")] string? IdToken);

/// <summary>The ID token claims we read. Claims whose JSON type varies between providers stay as elements.</summary>
public sealed record IdTokenClaims(
    [property: JsonPropertyName("iss")] string? Iss,
    [property: JsonPropertyName("aud")] JsonElement? Aud,
    [property: JsonPropertyName("azp")] string? Azp,
    [property: JsonPropertyName("exp")] long? Exp,
    [property: JsonPropertyName("iat")] long? Iat,
    [property: JsonPropertyName("nonce")] string? Nonce,
    [property: JsonPropertyName("sub")] string? Sub,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("email_verified")] JsonElement? EmailVerified,
    [property: JsonPropertyName("tid")] string? Tid,
    [property: JsonPropertyName("oid")] string? Oid,
    [property: JsonPropertyName("xms_edov")] JsonElement? XmsEdov,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("picture")] string? Picture);

/// <summary>A validated sign-in: the stable subject, and the email only as far as the provider vouches for it.</summary>
/// <param name="Email">Normalized; null when the token carried no usable address.</param>
/// <param name="EmailTrusted">Google <c>email_verified</c>, or Entra <c>xms_edov</c> / a listed tenant domain (§e).</param>
public sealed record OidcIdentity(OidcProvider Provider, string Subject, string? Email, bool EmailTrusted, string FullName, Uri? Picture);

/// <summary>ID token validation (§e; OIDC Core §3.1.3.7). Returns an error code instead of throwing.</summary>
public static class IdTokenValidator {
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);
    public const int MaxSubjectLength = 200;

    private static readonly string[] GoogleIssuers = ["https://accounts.google.com", "accounts.google.com"];

    public static bool TryValidate(
        OidcProvider provider,
        OidcOptions options,
        IdTokenClaims claims,
        string expectedNonce,
        DateTimeOffset now,
        [NotNullWhen(true)] out OidcIdentity? identity,
        [NotNullWhen(false)] out string? error) {
        identity = null;
        var client = options.Client(provider)!;
        if (!HasAudience(claims, client.ClientId)) {
            error = "oidc_audience";
            return false;
        }
        if (claims.Exp is not { } exp || now >= DateTimeOffset.FromUnixTimeSeconds(exp) + Skew) {
            error = "oidc_expired";
            return false;
        }
        if (claims.Iat is not { } iat || (now - DateTimeOffset.FromUnixTimeSeconds(iat)).Duration() > Skew) {
            error = "oidc_expired";
            return false;
        }
        if (claims.Nonce is null || !FixedTimeEquals(claims.Nonce, expectedNonce)) {
            error = "oidc_nonce";
            return false;
        }

        var email = AuthEndpoints.NormalizeEmail(claims.Email);
        var name = FullName(claims.Name);
        Uri? picture = null;
        string subject;
        bool trusted;
        if (provider == OidcProvider.Google) {
            if (!GoogleIssuers.Contains(claims.Iss, StringComparer.Ordinal)) {
                error = "oidc_issuer";
                return false;
            }
            subject = claims.Sub ?? "";
            trusted = IsTrue(claims.EmailVerified);
            picture = GooglePicture(claims.Picture);
        } else {
            var tid = claims.Tid?.ToLowerInvariant();
            if (tid is null || !options.EntraAllowedTenants.Contains(tid)) {
                error = "oidc_tenant";
                return false;
            }
            if (claims.Iss != $"https://login.microsoftonline.com/{claims.Tid}/v2.0") {
                error = "oidc_issuer";
                return false;
            }
            subject = string.IsNullOrEmpty(claims.Oid) ? "" : $"{tid}:{claims.Oid.ToLowerInvariant()}";
            trusted = email is not null
                && (IsTrue(claims.XmsEdov)
                    || options.EntraTenantDomains.TryGetValue(tid, out var domains) && domains.Contains(email[(email.IndexOf('@') + 1)..]));
        }
        if (subject.Length is 0 or > MaxSubjectLength) {
            error = "oidc_subject";
            return false;
        }

        identity = new OidcIdentity(provider, subject, email, trusted && email is not null, name, picture);
        error = null;
        return true;
    }

    /// <summary>The payload of a compact JWS. The signature is not checked: the token came from the token endpoint over TLS.</summary>
    public static IdTokenClaims? DecodePayload(string? idToken) {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 } || parts[1].Length == 0) {
            return null;
        }
        try {
            var payload = Base64Url.DecodeFromChars(parts[1]);
            return JsonSerializer.Deserialize(payload, OidcJsonContext.Default.IdTokenClaims);
        } catch (Exception e) when (e is FormatException or JsonException) {
            return null;
        }
    }

    // OIDC Core §3.1.3.7 items 3–5: aud must list us; with several audiences, or whenever azp is present, azp must be us.
    private static bool HasAudience(IdTokenClaims claims, string clientId) {
        var audiences = claims.Aud switch {
            { ValueKind: JsonValueKind.String } aud => [aud.GetString()!],
            { ValueKind: JsonValueKind.Array } aud => aud.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToList(),
            _ => new List<string>(),
        };
        if (!audiences.Contains(clientId, StringComparer.Ordinal)) {
            return false;
        }
        if (audiences.Count > 1 || claims.Azp is not null) {
            return claims.Azp == clientId;
        }
        return true;
    }

    // Google sends a JSON boolean; the string forms are accepted because Entra documents xms_edov only as "Boolean".
    private static bool IsTrue(JsonElement? value) => value switch {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.String } s => s.GetString() is "true" or "1",
        { ValueKind: JsonValueKind.Number } n => n.TryGetInt32(out var i) && i == 1,
        _ => false,
    };

    private static string FullName(string? name) {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length <= MeEndpoints.MaxFullNameLength && GuestNames.IsPlainText(trimmed) ? trimmed : "";
    }

    private static Uri? GooglePicture(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Host == OidcOptions.GooglePictureHost
            ? uri
            : null;

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>
/// The hand-rolled authorization code flow (§e): PKCE (S256), <c>state</c> and <c>nonce</c> in a sealed, single-use,
/// <c>SameSite=Lax</c> cookie; <c>response_mode=query</c> (S20); the code is exchanged with the client secret.
/// </summary>
public sealed class Oidc : IDisposable {
    public const string CookieName = "__Host-rs_oidc";
    public const int MaxResponseBytes = 256 * 1024;
    public static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    private static readonly byte[] CookieAssociatedData = "oidc-flow:v1"u8.ToArray();

    private readonly HostingOptions _hosting;
    private readonly Keyring _keyring;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);

    public Oidc(OidcOptions options, HostingOptions hosting, Keyring keyring, TimeProvider time) {
        Options = options;
        _hosting = hosting;
        _keyring = keyring;
        _time = time;
        _http = new HttpClient(options.CreateHandler(), disposeHandler: true) { Timeout = HttpTimeout };
    }

    public OidcOptions Options { get; }

    public IEnumerable<OidcProvider> EnabledProviders =>
        Enum.GetValues<OidcProvider>().Where(x => Options.Client(x) is not null);

    public bool IsEnabled(OidcProvider provider) => Options.Client(provider) is not null;

    public Uri RedirectUri(OidcProvider provider) =>
        new(_hosting.PublicUrl, $"/auth/oidc/{OidcProviders.Name(provider)}/callback");

    /// <summary>Sets the flow cookie and returns the provider's authorization URL.</summary>
    public Uri Start(HttpContext context, OidcProvider provider, string returnPath, PrincipalId? linkUser) {
        var client = Options.Client(provider) ?? throw new InvalidOperationException("The provider is not enabled.");
        var verifier = OpaqueToken.New();
        var flow = new OidcFlow(
            OidcProviders.Name(provider),
            OpaqueToken.New(),
            OpaqueToken.New(),
            verifier,
            returnPath,
            linkUser?.Value,
            (_time.GetUtcNow() + FlowLifetime).ToUnixTimeSeconds());
        var payload = JsonSerializer.SerializeToUtf8Bytes(flow, OidcJsonContext.Default.OidcFlow);
        var box = _keyring.Seal(KeyPurpose.OidcCookie, payload, CookieAssociatedData);
        context.Response.Cookies.Append(CookieName, Base64Url.EncodeToString(box), Sessions.CookieOptions(FlowLifetime));

        var query = new Dictionary<string, string> {
            ["response_type"] = "code",
            ["client_id"] = client.ClientId,
            ["redirect_uri"] = RedirectUri(provider).AbsoluteUri,
            ["scope"] = "openid email profile",
            ["response_mode"] = "query",
            ["state"] = flow.State,
            ["nonce"] = flow.Nonce,
            ["code_challenge"] = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
        };
        var separator = string.IsNullOrEmpty(client.AuthorizeEndpoint.Query) ? "?" : "&";
        return new Uri(client.AuthorizeEndpoint.AbsoluteUri + separator
            + string.Join('&', query.Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value)}")));
    }

    /// <summary>
    /// Reads the flow cookie and deletes it whatever it holds (single use). Null when it is missing, does not open,
    /// has expired or belongs to another provider.
    /// </summary>
    public OidcFlow? TakeFlow(HttpContext context, OidcProvider provider) {
        var value = context.Request.Cookies[CookieName];
        if (value is null) {
            return null;
        }
        context.Response.Cookies.Append(CookieName, "", Sessions.CookieOptions(TimeSpan.Zero));
        OidcFlow? flow;
        try {
            if (!_keyring.TryOpen(KeyPurpose.OidcCookie, Base64Url.DecodeFromChars(value), CookieAssociatedData, out var payload)) {
                return null;
            }
            flow = JsonSerializer.Deserialize(payload, OidcJsonContext.Default.OidcFlow);
        } catch (Exception e) when (e is FormatException or JsonException) {
            return null;
        }
        var live = flow is not null
            && flow.Provider == OidcProviders.Name(provider)
            && _time.GetUtcNow() < DateTimeOffset.FromUnixTimeSeconds(flow.ExpiresAt);
        return live ? flow : null;
    }

    /// <summary>
    /// True once per flow, when <paramref name="state"/> matches. Deleting the cookie makes the flow single use in an
    /// honest browser; remembering consumed states until they expire makes it single use for a replayed cookie too.
    /// </summary>
    public bool TryConsume(OidcFlow flow, string? state) {
        if (state is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(flow.State))) {
            return false;
        }
        var now = _time.GetUtcNow();
        foreach (var (key, expires) in _consumed) {
            if (expires <= now) {
                _consumed.TryRemove(key, out _);
            }
        }
        return _consumed.TryAdd(flow.State, DateTimeOffset.FromUnixTimeSeconds(flow.ExpiresAt));
    }

    /// <summary>Exchanges the code at the token endpoint; null on any failure.</summary>
    public async Task<IdTokenClaims?> ExchangeAsync(OidcProvider provider, string code, string verifier, CancellationToken cancellationToken) {
        var client = Options.Client(provider)!;
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri(provider).AbsoluteUri,
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
            ["code_verifier"] = verifier,
        });
        try {
            using var response = await _http.PostAsync(client.TokenEndpoint, content, cancellationToken);
            if (!response.IsSuccessStatusCode) {
                return null;
            }
            var body = await ReadCappedAsync(response, MaxResponseBytes, cancellationToken);
            var token = body is null ? null : JsonSerializer.Deserialize(body, OidcJsonContext.Default.OidcTokenResponse);
            return IdTokenValidator.DecodePayload(token?.IdToken);
        } catch (Exception e) when (e is HttpRequestException or JsonException || e is TaskCanceledException && !cancellationToken.IsCancellationRequested) {
            return null;
        }
    }

    /// <summary>Downloads a Google profile picture from the allow-listed host; null on any failure.</summary>
    public async Task<byte[]?> FetchPictureAsync(Uri picture, CancellationToken cancellationToken) {
        if (picture.Host != OidcOptions.GooglePictureHost || picture.Scheme != Uri.UriSchemeHttps) {
            return null;
        }
        try {
            using var response = await _http.GetAsync(picture, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode ? await ReadCappedAsync(response, BlobsRepo.AvatarMaxBytes, cancellationToken) : null;
        } catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !cancellationToken.IsCancellationRequested) {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, int cap, CancellationToken cancellationToken) {
        if (response.Content.Headers.ContentLength > cap) {
            return null;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0) {
            if (buffer.Length + read > cap) {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

/// <summary>How an OIDC sign-in resolved to a local account.</summary>
public abstract record OidcResolution {
    private OidcResolution() {
    }

    /// <param name="NewLink">The identity was linked or the account created just now.</param>
    public sealed record SignIn(PrincipalId User, bool NewLink) : OidcResolution;

    public sealed record Refused(string Code) : OidcResolution;
}

public enum OidcLinkOutcome {
    Linked,
    AlreadyLinked,
    LinkedElsewhere,
}

/// <summary>
/// The account-linking rules (§e, pre-hijack defence): an existing <c>(provider, subject)</c> signs in; a trusted email
/// links to a verified account, claims an unverified one, or creates a verified account; an untrusted email never
/// links by email.
/// </summary>
public sealed class OidcAccounts(NpgsqlDataSource dataSource, AccountClaims claims, BlobsRepo blobs, TimeProvider time) {
    private const int Attempts = 3;

    public async Task<OidcResolution> ResolveAsync(OidcIdentity identity, CancellationToken cancellationToken) {
        for (var attempt = 0; attempt < Attempts; attempt++) {
            if (await FindLinkedAsync(identity, cancellationToken) is { } linked) {
                return linked.Disabled ? new OidcResolution.Refused("account_disabled") : new OidcResolution.SignIn(linked.Id, false);
            }
            if (identity.Email is not { } email) {
                return new OidcResolution.Refused("email_required");
            }

            if (await FindByEmailAsync(email, cancellationToken) is { } account) {
                if (!identity.EmailTrusted) {
                    return new OidcResolution.Refused("account_exists");
                }
                if (account.Disabled) {
                    return new OidcResolution.Refused("account_disabled");
                }
                if (!account.Verified) {
                    await claims.ClaimAsync(account.Id, OidcProviders.Proof(identity.Provider), cancellationToken);
                }
                if (await LinkAsync(account.Id, identity, cancellationToken)) {
                    return new OidcResolution.SignIn(account.Id, true);
                }
                continue;
            }

            var created = new PrincipalId(Guid.CreateVersion7());
            if (await CreateAsync(created, email, identity, cancellationToken)) {
                return new OidcResolution.SignIn(created, true);
            }
        }
        return new OidcResolution.Refused("oidc_conflict");
    }

    /// <summary>The explicit link flow: refuses a subject that another user already linked.</summary>
    public async Task<OidcLinkOutcome> LinkExplicitAsync(PrincipalId user, OidcIdentity identity, CancellationToken cancellationToken) {
        for (var attempt = 0; attempt < Attempts; attempt++) {
            if (await FindLinkedAsync(identity, cancellationToken) is { } linked) {
                return linked.Id == user ? OidcLinkOutcome.AlreadyLinked : OidcLinkOutcome.LinkedElsewhere;
            }
            if (await LinkAsync(user, identity, cancellationToken)) {
                return OidcLinkOutcome.Linked;
            }
        }
        return OidcLinkOutcome.LinkedElsewhere;
    }

    /// <summary>Stores the picture as the user's avatar unless they already have one.</summary>
    /// <returns>True when the avatar changed.</returns>
    public async Task<bool> SetAvatarIfNoneAsync(PrincipalId user, SniffedImage image, CancellationToken cancellationToken) {
        var blob = new BlobId(Guid.NewGuid());
        await blobs.InsertAvatarAsync(blob, user, image, cancellationToken);
        await using var command = dataSource.CreateCommand("""
            with updated as (
              update users set avatar_blob_id = $2, updated_at = $3 where id = $1 and avatar_blob_id is null returning id
            )
            delete from blobs where id = $2 and not exists (select 1 from updated)
            """);
        command.Parameters.Add(new() { Value = user.Value });
        command.Parameters.Add(new() { Value = blob.Value });
        command.Parameters.Add(new() { Value = time.GetUtcNow() });
        // The statement's row count is the delete's: zero means the update took the blob.
        return await command.ExecuteNonQueryAsync(cancellationToken) == 0;
    }

    public async Task<bool> HasAvatarAsync(PrincipalId user, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("select avatar_blob_id is not null from users where id = $1");
        command.Parameters.Add(new() { Value = user.Value });
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task<(PrincipalId Id, bool Disabled)?> FindLinkedAsync(OidcIdentity identity, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            select u.id, u.disabled_at is not null
            from external_identities i join users u on u.id = i.user_id
            where i.provider = $1 and i.subject = $2
            """);
        command.Parameters.Add(new() { Value = OidcProviders.Name(identity.Provider) });
        command.Parameters.Add(new() { Value = identity.Subject });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (new PrincipalId(reader.GetGuid(0)), reader.GetBoolean(1)) : null;
    }

    private async Task<(PrincipalId Id, bool Verified, bool Disabled)?> FindByEmailAsync(string email, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(
            "select id, email_verified_at is not null, disabled_at is not null from users where email = $1");
        command.Parameters.Add(new() { Value = email });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (new PrincipalId(reader.GetGuid(0)), reader.GetBoolean(1), reader.GetBoolean(2))
            : null;
    }

    /// <returns>False when the subject is already linked (to anyone).</returns>
    private async Task<bool> LinkAsync(PrincipalId user, OidcIdentity identity, CancellationToken cancellationToken) {
        var now = time.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction) { BatchCommands = { InsertIdentity(user, identity, now, ifAbsent: true) } };
        if (await batch.ExecuteNonQueryAsync(cancellationToken) == 0) {
            return false;
        }
        await using var log = new NpgsqlBatch(connection, transaction) { BatchCommands = { Audit("auth.link", user, identity, now) } };
        await log.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    /// <returns>False when the email or the subject was taken in the meantime.</returns>
    private async Task<bool> CreateAsync(PrincipalId id, string email, OidcIdentity identity, CancellationToken cancellationToken) {
        var now = time.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction) {
            BatchCommands = {
                new("insert into principals (id, kind, created_at) values ($1, 'user', $2)") {
                    Parameters = { new() { Value = id.Value }, new() { Value = now } },
                },
                new("""
                    insert into users (id, email, email_verified_at, full_name, mentions_seen_at, created_at, updated_at)
                    values ($1, $2, $3, $4, $5, $5, $5)
                    """) {
                    Parameters = {
                        new() { Value = id.Value },
                        new() { Value = email },
                        new() { Value = identity.EmailTrusted ? now : DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz },
                        new() { Value = identity.FullName },
                        new() { Value = now },
                    },
                },
                InsertIdentity(id, identity, now, ifAbsent: false),
                Audit("auth.signup", id, identity, now),
            },
        };
        try {
            await batch.ExecuteNonQueryAsync(cancellationToken);
        } catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) {
            return false;
        }
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    private static NpgsqlBatchCommand InsertIdentity(PrincipalId user, OidcIdentity identity, DateTimeOffset now, bool ifAbsent) =>
        new("""
            insert into external_identities (provider, subject, user_id, linked_at) values ($1, $2, $3, $4)
            """ + (ifAbsent ? " on conflict (provider, subject) do nothing" : "")) {
            Parameters = {
                new() { Value = OidcProviders.Name(identity.Provider) },
                new() { Value = identity.Subject },
                new() { Value = user.Value },
                new() { Value = now },
            },
        };

    private static NpgsqlBatchCommand Audit(string action, PrincipalId user, OidcIdentity identity, DateTimeOffset now) =>
        UsersRepo.Audit(action, user, user.ToString(), new Dictionary<string, string> {
            ["provider"] = OidcProviders.Name(identity.Provider),
            ["email_trusted"] = identity.EmailTrusted ? "true" : "false",
        }, now);
}

internal static partial class OidcLog {
    [LoggerMessage(1, LogLevel.Information, "OIDC {Provider} callback refused: {Code}")]
    public static partial void Refused(ILogger logger, string provider, string code);
}
