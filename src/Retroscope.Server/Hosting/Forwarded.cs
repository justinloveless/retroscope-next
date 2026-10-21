using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Retroscope.Server.Hosting;

/// <summary>
/// Client IP from the proxy (S13). Only the rightmost hop is read (<c>ForwardLimit=1</c>) and only when the peer is
/// inside <c>TRUSTED_PROXY_CIDRS</c>; with no CIDRs configured nothing is trusted, loopback included.
/// With <c>CLIENT_IP_HEADER</c> set, that header (Cloudflare's single-address <c>CF-Connecting-IP</c>) replaces
/// <c>X-Forwarded-For</c> under the same peer rule.
/// </summary>
public static class Forwarded {
    public static void AddServices(IServiceCollection services) {
        services.AddSingleton<IConfigureOptions<ForwardedHeadersOptions>>(sp => new ConfigureNamedOptions<ForwardedHeadersOptions>(
            Options.DefaultName,
            options => Configure(options, sp.GetRequiredService<HostingOptions>())));
    }

    public static void Configure(ForwardedHeadersOptions options, HostingOptions hosting) {
        // With both lists empty the middleware trusts every peer, so no trusted proxy means no forwarding at all.
        options.ForwardedHeaders = hosting.TrustedProxies.Count == 0
            ? ForwardedHeaders.None
            : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.RequireHeaderSymmetry = false;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var network in hosting.TrustedProxies) {
            options.KnownIPNetworks.Add(network);
        }
        if (hosting.ClientIpHeader is { } header) {
            options.ForwardedForHeaderName = header;
        }
    }
}
