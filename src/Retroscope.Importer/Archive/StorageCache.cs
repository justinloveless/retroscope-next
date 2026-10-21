using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Importer.Map;

namespace Retroscope.Importer.Archive;

/// <summary>Fetches one public object; returns null when the body exceeds <paramref name="maxBytes"/>.</summary>
public interface IObjectFetcher {
    Task<byte[]?> FetchAsync(Uri url, int maxBytes, CancellationToken cancellationToken);
}

/// <summary>Plain HTTPS GET without redirects (the avatars bucket is public, so no service key is involved, S26).</summary>
public sealed class HttpObjectFetcher(HttpClient client) : IObjectFetcher {
    public static HttpClient CreateClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) {
            Timeout = TimeSpan.FromSeconds(30),
        };

    public async Task<byte[]?> FetchAsync(Uri url, int maxBytes, CancellationToken cancellationToken) {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes) {
            return null;
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0) {
            total += read;
        }
        return total > maxBytes ? null : buffer[..total];
    }
}

public sealed record StagingItem(string Bucket, string Path, Uri Url);

public sealed record StagingResult(int Fetched, int AlreadyCached, IReadOnlyList<(StagingItem Item, string Reason)> Skipped);

/// <summary>
/// Phase 5a (DESIGN §h.1, S2): stage avatar bytes in <c>archive.storage_cache</c>, outside the main transaction. Each
/// object is committed on its own, and objects already cached under the same (bucket, path) are not fetched again,
/// so a killed run resumes where it stopped. Only <c>avatars/&lt;user_id&gt;.*</c> of an imported user and allow-listed
/// Google avatar URLs are fetched; only PNG, JPEG or WebP up to 256 KiB (by magic bytes) is kept, without re-encoding.
/// Google avatars are cached under the bucket name <see cref="GoogleBucket"/> with the URL as the path.
/// </summary>
public sealed partial class StorageCache(NpgsqlDataSource target, IObjectFetcher fetcher) {
    public const string AvatarBucket = "avatars";
    public const string GoogleBucket = "google";
    public const int MaxAvatarBytes = 256 * 1024;
    public const int DefaultConcurrency = 4;

    public static IReadOnlyList<StagingItem> SelectAvatars(
        IEnumerable<(string Bucket, string Name, long Size)> objects,
        IEnumerable<(Guid UserId, string? AvatarUrl)> profiles,
        PrincipalDirectory principals,
        Uri storageBaseUrl) {
        var items = new List<StagingItem>();
        foreach (var (bucket, name, _) in objects) {
            if (bucket == AvatarBucket && OwnAvatar().Match(name) is { Success: true } match
                && Guid.TryParse(match.Groups["id"].Value, out var userId) && principals.IsKeptUser(userId)) {
                items.Add(new StagingItem(bucket, name, new Uri(storageBaseUrl, $"storage/v1/object/public/{bucket}/{Uri.EscapeDataString(name)}")));
            }
        }
        foreach (var (userId, avatarUrl) in profiles) {
            if (principals.IsKeptUser(userId) && IsGoogleAvatar(avatarUrl, out var url)) {
                items.Add(new StagingItem(GoogleBucket, url.AbsoluteUri, url));
            }
        }
        return [.. items.DistinctBy(x => (x.Bucket, x.Path)).OrderBy(x => x.Bucket, StringComparer.Ordinal).ThenBy(x => x.Path, StringComparer.Ordinal)];
    }

    public static bool IsGoogleAvatar(string? value, out Uri url) {
        url = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps
            || !parsed.IsDefaultPort || parsed.UserInfo.Length > 0 || !GoogleHost().IsMatch(parsed.Host)) {
            return false;
        }
        url = parsed;
        return true;
    }

    public async Task<StagingResult> StageAsync(IReadOnlyList<StagingItem> items, int concurrency, CancellationToken cancellationToken) {
        var cached = await CachedAsync(cancellationToken);
        var pending = items.Where(x => !cached.Contains((x.Bucket, x.Path))).ToList();
        var skipped = new ConcurrentBag<(StagingItem, string)>();
        var fetched = 0;
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken }, async (item, token) => {
            var bytes = await fetcher.FetchAsync(item.Url, MaxAvatarBytes, token);
            if (bytes is null) {
                skipped.Add((item, "larger than 256 KiB"));
            } else if (ImageSniffer.Detect(bytes) is null) {
                skipped.Add((item, "not PNG, JPEG or WebP"));
            } else {
                await StoreAsync(item, bytes, token);
                Interlocked.Increment(ref fetched);
            }
        });
        return new StagingResult(fetched, items.Count - pending.Count, [.. skipped.OrderBy(x => x.Item1.Path, StringComparer.Ordinal)]);
    }

    private async Task<HashSet<(string, string)>> CachedAsync(CancellationToken cancellationToken) {
        var cached = new HashSet<(string, string)>();
        await using var command = target.CreateCommand("select bucket, path from archive.storage_cache");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            cached.Add((reader.GetString(0), reader.GetString(1)));
        }
        return cached;
    }

    private async Task StoreAsync(StagingItem item, byte[] bytes, CancellationToken cancellationToken) {
        await using var command = target.CreateCommand(
            "insert into archive.storage_cache (bucket, path, sha256, bytes) values ($1, $2, $3, $4) on conflict do nothing");
        command.Parameters.Add(new() { Value = item.Bucket });
        command.Parameters.Add(new() { Value = item.Path });
        command.Parameters.Add(new() { Value = SHA256.HashData(bytes), NpgsqlDbType = NpgsqlDbType.Bytea });
        command.Parameters.Add(new() { Value = bytes, NpgsqlDbType = NpgsqlDbType.Bytea });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [GeneratedRegex(@"^(?<id>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\.[A-Za-z0-9]{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnAvatar();

    [GeneratedRegex(@"^lh[0-9]\.googleusercontent\.com$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GoogleHost();
}
