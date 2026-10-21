using System.Text.Json;
using System.Text.Json.Nodes;

namespace Retroscope.Soak;

/// <summary>The verdict of a run: every §j gate with its findings, plus the measured (not gated) numbers.</summary>
internal sealed record Report(bool Passed, IReadOnlyList<Finding> Findings, IReadOnlyList<Finding> Warnings, JsonObject Document) {
    public static Report Build(
        Profile profile,
        TimeSpan duration,
        ClientResults results,
        Rows rows,
        IReadOnlyList<Finding> oracle,
        IReadOnlyList<Converged> convergence,
        IReadOnlyList<Outage> outages,
        Outage? redeploy,
        IReadOnlyList<JsonElement> reconnects,
        IReadOnlyList<MemorySample> memory) {
        var findings = oracle.ToList();
        foreach (var check in convergence.Where(c => !c.Ok)) {
            findings.Add(new("convergence", $"after {check.After}: {check.Result}"));
        }

        var peak = memory.Count == 0 ? 0 : memory.Max(s => s.RssBytes);
        if (memory.Count == 0) {
            findings.Add(new("rss", "no RSS sample was taken"));
        } else if (peak >= Run.RssBudgetBytes) {
            var at = memory.First(s => s.RssBytes == peak).At;
            findings.Add(new("rss", $"app RSS peaked at {Mib(peak)} MiB at {at:O} (budget {Mib(Run.RssBudgetBytes)} MiB)"));
        }
        foreach (var outage in outages.Where(o => o.Name == "kill")) {
            if (!memory.Any(s => s.At >= outage.To && s.At <= outage.To + TimeSpan.FromSeconds(60))) {
                findings.Add(new("rss", $"no RSS sample in the minute after the kill at {outage.From:O}"));
            }
        }

        var redeployReconnects = redeploy is null
            ? []
            : reconnects.Where(r => DateTimeOffset.FromUnixTimeMilliseconds(r.GetProperty("closedAt").GetInt64()) is var closed
                && closed >= redeploy.From - TimeSpan.FromSeconds(1) && closed <= redeploy.To).Select(r => r.GetProperty("ms").GetInt64()).ToList();
        if (redeploy is null || redeployReconnects.Count == 0) {
            findings.Add(new("redeploy_reconnect", "no reconnect after the redeploy was measured"));
        } else if (redeployReconnects.Max() >= Run.RedeployReconnectBudget.TotalMilliseconds) {
            findings.Add(new("redeploy_reconnect", $"slowest reconnect after the redeploy took {redeployReconnects.Max()} ms"));
        }

        var intents = results.Tabs.SelectMany(t => t.Intents).ToList();
        // §c.6: `internal` marks a bug, and a transient database failure should never surface as one (it is answered with
        // bye + 1012 so the intent is resent). Reported, not gated: §j does not list it.
        var warnings = results.Tabs.SelectMany(t => t.Intents.Where(i => i.Code == "internal")
            .Select(i => new Finding("internal_nack", $"{t.Label} {i.K} #{i.Intent} was refused internal"))).ToList();
        var document = new JsonObject {
            ["profile"] = profile.Name,
            ["minutes"] = duration.TotalMinutes,
            ["passed"] = findings.Count == 0,
            ["gates"] = Gates(findings),
            ["measured"] = new JsonObject {
                ["sockets"] = results.Tabs.Count,
                ["acks"] = results.Latency.Acks,
                ["acksPerSecond"] = Math.Round(results.Latency.Acks / duration.TotalSeconds, 1),
                ["ackLatencyMs"] = new JsonObject {
                    ["p50"] = results.Latency.P50, ["p95"] = results.Latency.P95, ["p99"] = results.Latency.P99, ["max"] = results.Latency.Max,
                },
                ["intents"] = JsonSerializer.SerializeToNode(intents.GroupBy(i => i.Via).ToDictionary(g => g.Key, g => g.Count())),
                ["finalNacks"] = JsonSerializer.SerializeToNode(results.NackCodes),
                ["peakRssMiB"] = Math.Round(Mib(peak), 1),
                ["rssSamples"] = memory.Count,
                ["reconnects"] = reconnects.Count,
                ["redeployReconnectMaxMs"] = redeployReconnects.Count == 0 ? null : redeployReconnects.Max(),
                ["convergence"] = new JsonArray([.. convergence.Select(c => (JsonNode)new JsonObject {
                    ["after"] = c.After, ["ok"] = c.Ok, ["reconnectMs"] = c.ReconnectMs, ["convergedMs"] = c.ConvergedMs,
                })]),
                ["outages"] = new JsonArray([.. outages.Select(o => (JsonNode)new JsonObject {
                    ["name"] = o.Name, ["seconds"] = Math.Round((o.To - o.From).TotalSeconds, 1),
                })]),
                ["rows"] = new JsonObject {
                    ["rounds"] = rows.Rounds.Count, ["votes"] = rows.Votes.Count, ["cards"] = rows.Cards.Count,
                    ["comments"] = rows.Comments.Count, ["cardVotes"] = rows.CardVotes.Count,
                },
            },
            ["warnings"] = new JsonArray([.. warnings.Take(50).Select(f => (JsonNode)new JsonObject { ["gate"] = f.Gate, ["detail"] = f.Detail })]),
            ["findings"] = new JsonArray([.. findings.GroupBy(f => f.Gate).SelectMany(g => g.Take(50))
                .Select(f => (JsonNode)new JsonObject { ["gate"] = f.Gate, ["detail"] = f.Detail })]),
        };
        return new Report(findings.Count == 0, findings, warnings, document);
    }

    public static readonly string[] GateNames = [
        "lost_acked", "duplicate", "stale_overwrite", "wrong_state", "unacked", "vote_leak", "seq_gap", "replay_epoch",
        "reveal_twice", "reveal_missing", "timer_expired_twice", "timer_missing", "convergence", "rss", "redeploy_reconnect",
    ];

    private static JsonObject Gates(IReadOnlyList<Finding> findings) {
        var gates = new JsonObject();
        foreach (var name in GateNames.Concat(findings.Select(f => f.Gate)).Distinct()) {
            gates[name] = findings.Count(f => f.Gate == name);
        }
        return gates;
    }

    private static double Mib(long bytes) => bytes / 1048576.0;
}
