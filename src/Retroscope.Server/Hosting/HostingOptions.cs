using System.Net;

namespace Retroscope.Server.Hosting;

/// <summary>Hosting settings read once from the environment (DESIGN §i.1).</summary>
public sealed class HostingOptions {
    private HostingOptions(Uri publicUrl, string webRoot, IReadOnlyList<IPNetwork> trustedProxies, string? clientIpHeader) {
        PublicUrl = publicUrl;
        PublicOrigin = publicUrl.GetLeftPart(UriPartial.Authority);
        WebRoot = webRoot;
        TrustedProxies = trustedProxies;
        ClientIpHeader = clientIpHeader;
    }

    public Uri PublicUrl { get; }

    /// <summary>The exact value a same-origin browser sends in <c>Origin</c>, e.g. <c>https://retro.example.com</c>.</summary>
    public string PublicOrigin { get; }

    /// <summary>Holds <c>manifest.json</c>, <c>csp.json</c> and <c>assets/</c> from <c>web/dist</c>.</summary>
    public string WebRoot { get; }

    public IReadOnlyList<IPNetwork> TrustedProxies { get; }

    /// <summary>Set (e.g. <c>CF-Connecting-IP</c>) only when Cloudflare fronts the proxy.</summary>
    public string? ClientIpHeader { get; }

    public static HostingOptions FromConfiguration(IConfiguration configuration) {
        var publicUrl = configuration["PUBLIC_URL"];
        if (string.IsNullOrWhiteSpace(publicUrl)
            || !Uri.TryCreate(publicUrl, UriKind.Absolute, out var url)
            || url.Scheme is not ("https" or "http")
            || url.AbsolutePath != "/"
            || !string.IsNullOrEmpty(url.Query)
            || !string.IsNullOrEmpty(url.UserInfo)) {
            throw new InvalidOperationException("PUBLIC_URL must be an http(s) origin such as https://retro.example.com.");
        }

        var webRoot = configuration["WEB_ROOT"];
        if (string.IsNullOrWhiteSpace(webRoot)) {
            webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        }

        var header = configuration["CLIENT_IP_HEADER"];
        return new HostingOptions(
            url,
            webRoot,
            ParseNetworks(configuration["TRUSTED_PROXY_CIDRS"]),
            string.IsNullOrWhiteSpace(header) ? null : header.Trim());
    }

    public static IReadOnlyList<IPNetwork> ParseNetworks(string? cidrs) {
        if (string.IsNullOrWhiteSpace(cidrs)) {
            return [];
        }
        var networks = new List<IPNetwork>();
        foreach (var entry in cidrs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (!IPNetwork.TryParse(entry, out var network)) {
                throw new InvalidOperationException($"TRUSTED_PROXY_CIDRS entry '{entry}' is not a CIDR.");
            }
            networks.Add(network);
        }
        return networks;
    }
}
