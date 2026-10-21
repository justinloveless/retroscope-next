using System.Net;
using Retroscope.Fakes.FakeJira;
using Retroscope.Fakes.Host;

// FakeJira as a container for deploy/compose.test.yml. The fake itself serves plain HTTP on loopback, so this
// process fronts it with TLS on :443 under a certificate from a throwaway CA. The CA (plus the system roots) is
// written to CERT_BUNDLE for the app container to trust through SSL_CERT_FILE; the file appearing is the health signal.
var bundlePath = Environment.GetEnvironmentVariable("CERT_BUNDLE") ?? "/certs/bundle.pem";
var hosts = (Environment.GetEnvironmentVariable("FAKEJIRA_HOSTS") ?? $"*.atlassian.net,{FakeJiraServer.MediaHost}")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (File.Exists(bundlePath)) {
    File.Delete(bundlePath);
}

await using var jira = await FakeJiraServer.StartAsync();
jira.Email = Environment.GetEnvironmentVariable("FAKEJIRA_EMAIL") ?? jira.Email;
jira.Token = Environment.GetEnvironmentVariable("FAKEJIRA_TOKEN") ?? jira.Token;
SeedData.Apply(jira);

var certificates = TestCertificates.Create(hosts);
await using var front = await TlsFront.StartAsync(IPAddress.Any, 443, certificates.Server, new Uri($"http://127.0.0.1:{jira.Port}"));
await certificates.WriteBundleAsync(bundlePath);
Console.WriteLine($"FakeJira on :443 for {string.Join(", ", hosts)}; trust bundle at {bundlePath}");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => {
    e.Cancel = true;
    stop.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
try {
    await Task.Delay(Timeout.Infinite, stop.Token);
} catch (OperationCanceledException) {
}
