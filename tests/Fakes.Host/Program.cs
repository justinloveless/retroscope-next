using System.Net;
using Retroscope.Fakes.FakeJira;
using Retroscope.Fakes.FakeOidc;
using Retroscope.Fakes.Host;

// The fakes as containers for deploy/compose.test.yml; FAKES_ROLE picks one.
//   jira (default): FakeJira serves plain HTTP on loopback, so this process fronts it with TLS on :443 under a
//     certificate from a throwaway CA. The CA (plus the system roots) is written to CERT_BUNDLE for the app container
//     to trust through SSL_CERT_FILE; the file appearing is the health signal.
//   oidc: FakeOidc (Google) on plain HTTP :8081, its clients from FAKEOIDC_CLIENTS (id:secret,…); READY_FILE
//     appears once it listens.
var role = Environment.GetEnvironmentVariable("FAKES_ROLE") ?? "jira";
await using IAsyncDisposable running = role switch {
    "jira" => await StartJiraAsync(),
    "oidc" => await StartOidcAsync(),
    _ => throw new InvalidOperationException($"FAKES_ROLE must be 'jira' or 'oidc', not '{role}'."),
};

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

static async Task<IAsyncDisposable> StartJiraAsync() {
    var bundlePath = Environment.GetEnvironmentVariable("CERT_BUNDLE") ?? "/certs/bundle.pem";
    var hosts = (Environment.GetEnvironmentVariable("FAKEJIRA_HOSTS") ?? $"*.atlassian.net,{FakeJiraServer.MediaHost}")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (File.Exists(bundlePath)) {
        File.Delete(bundlePath);
    }

    var jira = await FakeJiraServer.StartAsync();
    jira.Email = Environment.GetEnvironmentVariable("FAKEJIRA_EMAIL") ?? jira.Email;
    jira.Token = Environment.GetEnvironmentVariable("FAKEJIRA_TOKEN") ?? jira.Token;
    SeedData.Apply(jira);

    var certificates = TestCertificates.Create(hosts);
    var front = await TlsFront.StartAsync(IPAddress.Any, 443, certificates.Server, new Uri($"http://127.0.0.1:{jira.Port}"));
    await certificates.WriteBundleAsync(bundlePath);
    Console.WriteLine($"FakeJira on :443 for {string.Join(", ", hosts)}; trust bundle at {bundlePath}");
    return new Running(front, jira);
}

static async Task<IAsyncDisposable> StartOidcAsync() {
    var readyPath = Environment.GetEnvironmentVariable("READY_FILE") ?? "/tmp/ready";
    if (File.Exists(readyPath)) {
        File.Delete(readyPath);
    }

    var oidc = await FakeOidcServer.StartAsync();
    foreach (var client in (Environment.GetEnvironmentVariable("FAKEOIDC_CLIENTS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) {
        var (id, secret) = client.Split(':', 2) is [var i, var s] ? (i.Trim(), s.Trim())
            : throw new InvalidOperationException("FAKEOIDC_CLIENTS entries must look like 'id:secret'.");
        oidc.Clients[id] = secret;
    }

    var front = await OidcFront.StartAsync(IPAddress.Any, 8081, oidc);
    await File.WriteAllTextAsync(readyPath, "ready");
    Console.WriteLine($"FakeOidc on :8081 for clients {string.Join(", ", oidc.Clients.Keys)}");
    return new Running(front, oidc);
}

/// <summary>Disposes the front before the fake behind it.</summary>
internal sealed class Running(IAsyncDisposable front, IAsyncDisposable fake) : IAsyncDisposable {
    public async ValueTask DisposeAsync() {
        await front.DisposeAsync();
        await fake.DisposeAsync();
    }
}
