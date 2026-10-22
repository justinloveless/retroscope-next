using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Retroscope.Soak;

/// <summary>
/// The cgroup-limited deploy/compose.test.yml stack (1 vCPU, 1 GB for app + db + backup) with the soak overlay, under
/// its own compose project, ports and subnets. Every chaos action is a plain docker command against it.
/// </summary>
internal sealed class Stack(string root, int httpsPort) {
    public const string Project = "retroscope-soak";
    public const string AppContainer = Project + "-app-1";

    public int HttpsPort { get; } = httpsPort;

    public int ToxiproxyPort { get; } = httpsPort + 1;

    public int MailpitPort { get; } = httpsPort + 2;

    public string BaseUrl => $"https://localhost:{HttpsPort}";

    /// <summary>
    /// Builds and starts the stack. One retry: Rancher Desktop sometimes still holds a published port of the previous
    /// run's containers for a few seconds after they are gone.
    /// </summary>
    public async Task UpAsync() {
        try {
            await ComposeAsync(TimeSpan.FromMinutes(30), "up", "-d", "--build", "--wait");
        } catch (InvalidOperationException e) when (e.Message.Contains("address already in use", StringComparison.Ordinal)) {
            await DownAsync();
            await Task.Delay(TimeSpan.FromSeconds(15));
            await ComposeAsync(TimeSpan.FromMinutes(30), "up", "-d", "--build", "--wait");
        }
    }

    public Task DownAsync() => ComposeAsync(TimeSpan.FromMinutes(5), "down", "-v", "--remove-orphans");

    /// <summary><c>docker kill -s KILL app</c>, then back up the way the restart policy would bring it.</summary>
    public async Task KillAppAsync() {
        await DockerAsync(TimeSpan.FromMinutes(1), "kill", "-s", "KILL", AppContainer);
        await WaitHealthyAsync();
    }

    public async Task RestartDbAsync() {
        await ComposeAsync(TimeSpan.FromMinutes(3), "restart", "db");
        await WaitHealthyAsync();
    }

    /// <summary>Stop and recreate, as Coolify redeploys a compose app (§0.1 Deploys).</summary>
    public Task RedeployAppAsync() =>
        ComposeAsync(TimeSpan.FromMinutes(3), "up", "-d", "--no-deps", "--no-build", "--force-recreate", "--wait", "app");

    public Task WaitHealthyAsync() => ComposeAsync(TimeSpan.FromMinutes(3), "up", "-d", "--no-build", "--wait");

    /// <summary>
    /// One statement through psql inside the db container, as the owner; returns the unaligned output. (The database
    /// port is not published: Rancher Desktop's port forwarder did not always come back after the DB restart.)
    /// </summary>
    public async Task<string> PsqlAsync(string sql) =>
        (await ComposeAsync(TimeSpan.FromMinutes(2), "exec", "-T", "db", "psql", "-U", "retroscope_owner", "-d", "retroscope",
            "-At", "-v", "ON_ERROR_STOP=1", "-c", sql)).Trim();

    /// <summary>The app container's memory as the cgroup counts it (recorded next to the process RSS).</summary>
    public async Task<long?> AppContainerBytesAsync() {
        var output = await DockerAsync(TimeSpan.FromSeconds(20), "stats", "--no-stream", "--format", "{{.MemUsage}}", AppContainer);
        var used = output.Split('/')[0].Trim();
        var units = new (string Suffix, double Factor)[] { ("GiB", 1 << 30), ("MiB", 1 << 20), ("KiB", 1 << 10), ("B", 1) };
        foreach (var (suffix, factor) in units) {
            if (used.EndsWith(suffix, StringComparison.Ordinal)
                && double.TryParse(used[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                return (long)(value * factor);
            }
        }
        return null;
    }

    private Task<string> ComposeAsync(TimeSpan timeout, params string[] args) =>
        DockerAsync(timeout, [
            "compose", "-p", Project, "-f", Path.Combine(root, "deploy/compose.test.yml"),
            "-f", Path.Combine(root, "tests/Retroscope.Soak/compose.soak.yml"), .. args,
        ]);

    private async Task<string> DockerAsync(TimeSpan timeout, params string[] args) {
        var start = new ProcessStartInfo("docker") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        foreach (var arg in args) {
            start.ArgumentList.Add(arg);
        }
        foreach (var (name, value) in Environment()) {
            start.Environment[name] = value;
        }
        using var process = Process.Start(start)!;
        var output = new StringBuilder();
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, e) => output.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => errors.AppendLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cancel = new CancellationTokenSource(timeout);
        try {
            await process.WaitForExitAsync(cancel.Token);
        } catch (OperationCanceledException) {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"docker {string.Join(' ', args)} did not finish within {timeout}");
        }
        process.WaitForExit();
        if (process.ExitCode != 0) {
            throw new InvalidOperationException($"docker {string.Join(' ', args)} exited {process.ExitCode}: {errors}");
        }
        return output.ToString();
    }

    private IEnumerable<(string, string)> Environment() {
        yield return ("RS_TEST_HTTPS_PORT", HttpsPort.ToString(CultureInfo.InvariantCulture));
        yield return ("RS_TEST_MAILPIT_PORT", MailpitPort.ToString(CultureInfo.InvariantCulture));
        yield return ("RS_SOAK_TOXIPROXY_PORT", ToxiproxyPort.ToString(CultureInfo.InvariantCulture));
        yield return ("RS_SOAK_EDGE_SUBNET", "172.31.144.0/24");
        yield return ("RS_SOAK_JIRA_SUBNET", "198.20.144.0/24");
        if (System.Environment.GetEnvironmentVariable("DOCKER_HOST") is null) {
            var rancher = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".rd/docker.sock");
            if (File.Exists(rancher)) {
                yield return ("DOCKER_HOST", $"unix://{rancher}");
            }
        }
    }
}
