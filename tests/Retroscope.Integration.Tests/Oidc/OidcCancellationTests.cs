using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Retroscope.Core.Security;
using Retroscope.Server.Auth;
using Retroscope.Server.Hosting;
using OidcService = Retroscope.Server.Auth.Oidc;

namespace Retroscope.Integration.Tests.Oidc;

// A provider that fails or times out is a refused sign-in (null), but a cancelled request (the browser went away, or
// shutdown) must stay a cancellation and never be reported as a provider failure.
[Trait("Category", "Oidc")]
public sealed class OidcCancellationTests {
    private static readonly Uri Picture = new("https://lh3.googleusercontent.com/a/x");

    [Fact]
    public async Task A_cancelled_caller_cancels_the_code_exchange() {
        using var oidc = Create(new HangingHandler());
        using var aborted = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var error = await Record.ExceptionAsync(() => oidc.ExchangeAsync(OidcProvider.Google, "code", "verifier", aborted.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
    }

    [Fact]
    public async Task A_cancelled_caller_cancels_the_picture_fetch() {
        using var oidc = Create(new HangingHandler());
        using var aborted = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var error = await Record.ExceptionAsync(() => oidc.FetchPictureAsync(Picture, aborted.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
    }

    [Fact]
    public async Task A_provider_timeout_is_a_failed_exchange() {
        using var oidc = Create(new TimingOutHandler());

        Assert.Null(await oidc.ExchangeAsync(OidcProvider.Google, "code", "verifier", TestContext.Current.CancellationToken));
        Assert.Null(await oidc.FetchPictureAsync(Picture, TestContext.Current.CancellationToken));
    }

    private static OidcService Create(HttpMessageHandler handler) {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["PUBLIC_URL"] = OidcHost.PublicUrl,
            ["WEB_ROOT"] = Path.Combine(Path.GetTempPath(), "rs-no-web-build"),
            ["GOOGLE_CLIENT_ID"] = OidcHost.GoogleClient,
            ["GOOGLE_CLIENT_SECRET"] = "google-secret",
        }).Build();
        var options = OidcOptions.FromConfiguration(configuration) with { CreateHandler = () => handler };
        var keyring = Keyring.Parse("k1:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        return new OidcService(options, HostingOptions.FromConfiguration(configuration), keyring, TimeProvider.System);
    }

    private sealed class HangingHandler : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    // What HttpClient raises when its own Timeout elapses: a cancellation the caller did not ask for.
    private sealed class TimingOutHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out", new TimeoutException()));
    }
}
