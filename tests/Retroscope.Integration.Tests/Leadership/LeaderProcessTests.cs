using System.Diagnostics;
using System.Text;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using static Retroscope.Integration.Tests.Leadership.LeaderHarness;

namespace Retroscope.Integration.Tests.Leadership;

// Runs the real server binary: "exits non-zero" is a property of the process, which the restart policy relies on.
[Trait("Category", "Leader")]
[Collection(SharedPostgres.Name)]
public sealed class LeaderProcessTests(PostgresFixture postgres) {
    [Fact]
    public async Task Second_host_exits_non_zero_when_the_acquire_window_closes() {
        var database = await postgres.CreateDatabaseAsync();
        await using var first = Create(database);
        await first.StartAsync(Token);

        using var server = ServerProcess.Start(database, acquireSeconds: 2);
        var exitCode = await server.WaitForExitAsync(TimeSpan.FromSeconds(60));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Could not acquire the leader lease within 2 s", server.Output, StringComparison.Ordinal);
        Assert.Equal(first.Current, await EpochAsync(database));
    }

    [Fact]
    public async Task Host_exits_non_zero_when_its_lease_connection_dies() {
        var database = await postgres.CreateDatabaseAsync();
        var epoch = await EpochAsync(database);

        using var server = ServerProcess.Start(database, acquireSeconds: 30);
        await WaitUntilAsync(async () => server.HasExited || await EpochAsync(database) > epoch, TimeSpan.FromSeconds(60));
        Assert.False(server.HasExited, server.Output);
        await TerminateLeaseHolderAsync(database);
        var exitCode = await server.WaitForExitAsync(TimeSpan.FromSeconds(30));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Leadership lost: the lease connection failed", server.Output, StringComparison.Ordinal);
    }

    private sealed class ServerProcess : IDisposable {
        private readonly Process _process;
        private readonly StringBuilder _output = new();

        private ServerProcess(Process process) {
            _process = process;
            _process.OutputDataReceived += (_, e) => Append(e.Data);
            _process.ErrorDataReceived += (_, e) => Append(e.Data);
        }

        public bool HasExited => _process.HasExited;

        public string Output {
            get {
                lock (_output) {
                    return _output.ToString();
                }
            }
        }

        public static ServerProcess Start(TestDatabase database, int acquireSeconds) {
            var binary = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "retroscope.exe" : "retroscope");
            var info = new ProcessStartInfo(binary) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            info.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            info.Environment["DOTNET_hostBuilder__reloadConfigOnChange"] = "false";
            info.Environment["PUBLIC_URL"] = "http://127.0.0.1";
            info.Environment["DATABASE_URL"] = database.AppConnectionString;
            info.Environment["DATABASE_OWNER_URL"] = database.OwnerConnectionString;
            info.Environment[LeaderOptions.AcquireSecondsKey] = acquireSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var server = new ServerProcess(new Process { StartInfo = info });
            server._process.Start();
            server._process.BeginOutputReadLine();
            server._process.BeginErrorReadLine();
            return server;
        }

        public async Task<int> WaitForExitAsync(TimeSpan timeout) {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
            cts.CancelAfter(timeout);
            try {
                await _process.WaitForExitAsync(cts.Token);
            } catch (OperationCanceledException) {
                Assert.Fail($"The server did not exit within {timeout.TotalSeconds} s.\n{Output}");
            }
            return _process.ExitCode;
        }

        public void Dispose() {
            if (!_process.HasExited) {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
            _process.Dispose();
        }

        private void Append(string? line) {
            if (line is null) {
                return;
            }
            lock (_output) {
                _output.AppendLine(line);
            }
        }
    }
}
