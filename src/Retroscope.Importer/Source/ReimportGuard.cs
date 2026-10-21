namespace Retroscope.Importer.Source;

public sealed class ReimportRefusedException(string message) : Exception(message);

/// <summary>
/// After <c>cutover</c> the app owns the data (S24). <c>run</c> then refuses unless the operator passes
/// <c>--force-reimport --i-have-a-dump=&lt;path&gt;</c> naming an existing, non-empty dump file and types the target
/// database name at the prompt.
/// </summary>
public static class ReimportGuard {
    public static void Check(DateTime? cutoverAt, bool force, string? dumpPath, string databaseName, TextReader input, TextWriter output) {
        if (cutoverAt is null) {
            return;
        }
        if (!force || string.IsNullOrEmpty(dumpPath)) {
            throw new ReimportRefusedException(
                $"Cutover happened at {cutoverAt:O}; the app owns this data. Re-importing needs --force-reimport --i-have-a-dump=<path>.");
        }
        var dump = new FileInfo(dumpPath);
        if (!dump.Exists || dump.Length == 0) {
            throw new ReimportRefusedException($"--i-have-a-dump: {dumpPath} does not exist or is empty.");
        }

        output.Write($"This replaces every row in database '{databaseName}' (cutover was {cutoverAt:O}). Type the database name to continue: ");
        output.Flush();
        if (!string.Equals(input.ReadLine()?.Trim(), databaseName, StringComparison.Ordinal)) {
            throw new ReimportRefusedException("The typed name does not match; nothing was changed.");
        }
    }
}
