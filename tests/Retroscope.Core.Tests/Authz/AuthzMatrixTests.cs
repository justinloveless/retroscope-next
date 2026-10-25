using System.Text;
using Retroscope.Core.Authz;
using Retroscope.Core.Tests.Scaffold;

namespace Retroscope.Core.Tests.Authz;

// tests/authz-matrix.csv is the reviewed table of who may do what: one row per capability and resource, one column
// per viewer, Y = allowed. Regenerate it with RS_UPDATE_AUTHZ_MATRIX=1 after an intended rule change and review the
// diff cell by cell.
[Trait("Category", "Authz")]
public sealed class AuthzMatrixTests {
    private const char Allow = 'Y';
    private const char Deny = '-';

    private static readonly string MatrixPath = Path.Combine(RepoRoot.Path, "tests", "authz-matrix.csv");

    [Fact]
    public void Policy_matches_every_cell_of_the_matrix() {
        var expected = Compute();
        if (Environment.GetEnvironmentVariable("RS_UPDATE_AUTHZ_MATRIX") == "1") {
            File.WriteAllText(MatrixPath, Render(expected));
        }

        var rows = Load();
        var mismatches = new List<string>();
        foreach (var row in expected) {
            Assert.True(rows.TryGetValue((row.Capability, row.Resource), out var cells), $"missing row {row.Capability},{row.Resource}");
            for (var i = 0; i < AuthzMatrix.Viewers.Count; i++) {
                if (cells[i] != row.Cells[i]) {
                    mismatches.Add($"{row.Capability},{row.Resource},{AuthzMatrix.Viewers[i].Name}: csv {cells[i]}, policy {row.Cells[i]}");
                }
            }
        }
        Assert.Empty(mismatches);
        Assert.Equal(expected.Count, rows.Count);
    }

    [Fact]
    public void Matrix_covers_every_capability_with_an_allow_and_a_deny() {
        var rows = Load();
        foreach (var capability in AuthzMatrix.Capabilities) {
            var cells = rows.Where(x => x.Key.Capability == capability.ToString()).SelectMany(x => x.Value).ToList();
            Assert.True(cells.Contains(Allow), $"{capability} has no allow cell");
            Assert.True(cells.Contains(Deny), $"{capability} has no deny cell");
        }
    }

    [Fact]
    public void Matrix_has_a_row_for_every_resource_of_each_capabilitys_room_kind() {
        var rows = Load();
        foreach (var capability in AuthzMatrix.Capabilities) {
            var kinds = AuthzMatrix.KindsOf(capability);
            foreach (var resource in AuthzMatrix.Resources.Where(x => kinds.Contains(x.Facts.Room.Kind))) {
                Assert.True(rows.ContainsKey((capability.ToString(), resource.Name)), $"missing row {capability},{resource.Name}");
            }
        }
    }

    [Fact]
    public void Mid_session_changes_take_effect_on_the_next_check() {
        foreach (var capability in AuthzMatrix.Capabilities) {
            foreach (var resource in AuthzMatrix.Resources) {
                foreach (var viewer in AuthzMatrix.Viewers.Where(x => x.Before is not null)) {
                    var (before, _) = AuthzMatrix.Evaluate(capability, resource, viewer);
                    var reference = AuthzMatrix.Viewers.Single(x => x.Name == viewer.Before);
                    Assert.True(
                        before == AuthzMatrix.Evaluate(capability, resource, reference).After,
                        $"{capability},{resource.Name},{viewer.Name}: before the change it should act as {viewer.Before}");
                }
            }
        }
    }

    [Fact]
    public void A_capability_checked_against_another_room_kind_is_denied() {
        foreach (var capability in AuthzMatrix.Capabilities) {
            var kinds = AuthzMatrix.KindsOf(capability);
            foreach (var resource in AuthzMatrix.Resources.Where(x => !kinds.Contains(x.Facts.Room.Kind))) {
                foreach (var viewer in AuthzMatrix.Viewers) {
                    Assert.False(
                        AuthzMatrix.Evaluate(capability, resource, viewer).After,
                        $"{capability} on {resource.Name} for {viewer.Name}");
                }
            }
        }
    }

    /// <summary>
    /// Every capability needs a signed-in member of the room's team (UX18): signed-out callers, guest and external
    /// principals, signed-in non-members and app admins as such are denied everything, as is everyone on a deleted room.
    /// </summary>
    [Fact]
    public void Every_capability_is_denied_to_non_members_and_on_deleted_rooms() {
        var rows = Load();
        var outsiders = new[] { "anonymous", "external", "guest", "user", "app_admin" };
        foreach (var ((capability, resource), cells) in rows) {
            foreach (var outsider in outsiders) {
                Assert.True(cells[Column(outsider)] == Deny, $"{capability},{resource} allows {outsider}");
            }
            if (resource.EndsWith("_deleted", StringComparison.Ordinal)) {
                Assert.True(cells.All(x => x == Deny), $"{capability},{resource} allows someone on a deleted room");
            }
        }
    }

    private static int Column(string viewer) {
        return AuthzMatrix.Viewers.Select((x, i) => (x.Name, i)).Single(x => x.Name == viewer).i;
    }

    private static List<Row> Compute() {
        var rows = new List<Row>();
        foreach (var capability in AuthzMatrix.Capabilities) {
            var kinds = AuthzMatrix.KindsOf(capability);
            foreach (var resource in AuthzMatrix.Resources.Where(x => kinds.Contains(x.Facts.Room.Kind))) {
                var cells = AuthzMatrix.Viewers
                    .Select(viewer => AuthzMatrix.Evaluate(capability, resource, viewer).After ? Allow : Deny)
                    .ToArray();
                rows.Add(new Row(capability.ToString(), resource.Name, cells));
            }
        }
        return rows;
    }

    private static string Render(List<Row> rows) {
        var text = new StringBuilder();
        text.Append("capability,resource,").AppendJoin(',', AuthzMatrix.Viewers.Select(x => x.Name)).Append('\n');
        foreach (var row in rows) {
            text.Append(row.Capability).Append(',').Append(row.Resource).Append(',').AppendJoin(',', row.Cells).Append('\n');
        }
        return text.ToString();
    }

    private static Dictionary<(string Capability, string Resource), char[]> Load() {
        var lines = File.ReadAllLines(MatrixPath);
        Assert.Equal("capability,resource," + string.Join(',', AuthzMatrix.Viewers.Select(x => x.Name)), lines[0]);
        var rows = new Dictionary<(string, string), char[]>();
        foreach (var line in lines.Skip(1)) {
            var parts = line.Split(',');
            Assert.Equal(AuthzMatrix.Viewers.Count + 2, parts.Length);
            Assert.True(Enum.TryParse<Capability>(parts[0], out _), $"unknown capability {parts[0]}");
            Assert.Contains(AuthzMatrix.Resources, x => x.Name == parts[1]);
            var cells = parts.Skip(2).Select(x => Assert.Single(x)).ToArray();
            Assert.All(cells, x => Assert.True(x is Allow or Deny, $"cell {x} in {line}"));
            Assert.True(rows.TryAdd((parts[0], parts[1]), cells), $"duplicate row {parts[0]},{parts[1]}");
        }
        return rows;
    }

    private sealed record Row(string Capability, string Resource, char[] Cells);
}
