using System.Text.RegularExpressions;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Archive;

/// <summary>
/// Which archived values are secret (DESIGN §h.3). The explicit <c>seal</c> lists in <c>expected-columns.json</c> are
/// the primary mechanism; the column-name regex is a backstop for everything else, minus the table's <c>plain</c>
/// list. <c>app_config.value</c> is secret when its key looks like a credential.
/// </summary>
public static partial class SecretPolicy {
    public static bool IsSecret(SourceRow row, string column) {
        var table = row.Plan.Expected;
        if (table.Seal.Contains(column)) {
            return true;
        }
        if (row.Table == "public.app_config" && column == "value") {
            return SecretName().IsMatch(row.Text("key") ?? "");
        }
        return !table.Plain.Contains(column) && SecretName().IsMatch(column);
    }

    [GeneratedRegex("TOKEN|KEY|SECRET|URL|PASSWORD", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();
}
