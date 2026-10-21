using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Map;

namespace Retroscope.Importer.Storage;

/// <summary>
/// Phase 5, inside the main transaction (DESIGN §h.1, §h.2 <c>profiles.avatar_url</c>): attaches avatars staged by
/// <c>fetch-storage</c> and writes the manifest of every Supabase storage object to <c>archive.storage_objects</c>.
/// A user gets an avatar only when <c>avatar_url</c> names the user's own <c>avatars/&lt;user_id&gt;.*</c> object or an
/// allow-listed Google URL, and the bytes are in <c>archive.storage_cache</c> (S26). Bytes of other objects are never
/// copied: they are recorded <c>manifest_only</c>.
/// </summary>
public sealed partial class StorageStage : IImportStage {
    public const string ObjectsTable = "storage.objects";

    /// <summary>Avatar blob ids are <c>v5(avatar:&lt;user id&gt;)</c> under this namespace (frozen), so re-runs agree.</summary>
    public static readonly Guid AvatarNamespace = new("5d0c7a3e-91b4-4f2a-8e6d-3c1b9a7f2e40");

    public int Order => 500;

    public string Name => "storage";

    public static Guid AvatarBlobId(Guid user) => UuidV5.Create(AvatarNamespace, $"avatar:{user:D}");

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var objects = await context.Source.StorageObjectsAsync(cancellationToken);
        var cache = await ReadCacheAsync(context.Target, cancellationToken);
        var profiles = await context.Source.ReadAsync("public.profiles", cancellationToken);

        var avatars = new List<(Guid User, CachedObject Object, string ContentType)>();
        // Own avatar objects that a profile points at: null when imported, else why not.
        var referenced = new Dictionary<string, (Guid User, string? Reason)>(StringComparer.Ordinal);
        foreach (var profile in profiles.OrderBy(x => x.Uuid("id")!.Value)) {
            var user = profile.Uuid("id")!.Value;
            if (!context.Principals.IsKeptUser(user) || profile.Text("avatar_url") is not { Length: > 0 } url) {
                continue;
            }
            var key = AvatarKey(user, url);
            var reason = key is null ? "not_allowed" : Unusable(cache.GetValueOrDefault(key.Value));
            if (reason is null) {
                var cached = cache[key!.Value];
                avatars.Add((user, cached, ImageSniffer.Detect(cached.Bytes)!));
                context.Report.Count(key.Value.Bucket == StorageCache.GoogleBucket ? "storage.avatars.google" : "storage.avatars.own");
            } else {
                context.Report.Count($"storage.avatars.skipped.{reason}");
                var host = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Host : "";
                context.Report.Note("skippedAvatars", new JsonObject { ["user"] = user.ToString(), ["host"] = host, ["reason"] = reason });
            }
            if (key is { Bucket: StorageCache.AvatarBucket, Path: var path }) {
                referenced[path] = (user, reason);
            }
        }

        await TargetRows.CopyAsync(context.Target, "blobs", BlobColumns, avatars.Select(x => new object?[] {
            AvatarBlobId(x.User), "avatar", x.ContentType, x.Object.Bytes.Length, x.Object.Sha256, x.Object.Bytes, x.User, context.ImportedAt,
        }), cancellationToken);
        await SetAvatarsAsync(context.Target, avatars.Select(x => x.User).ToArray(), cancellationToken);

        await WriteManifestAsync(context, objects, cache, referenced, cancellationToken);
    }

    /// <summary>
    /// The cache entry an <c>avatar_url</c> may use, or null when it is neither the user's own avatar object nor an
    /// allow-listed Google URL. Only the object path matters: the bytes were fetched from <c>IMPORT_STORAGE_URL</c>.
    /// </summary>
    public static (string Bucket, string Path)? AvatarKey(Guid user, string avatarUrl) {
        if (StorageCache.IsGoogleAvatar(avatarUrl, out var google)) {
            return (StorageCache.GoogleBucket, google.AbsoluteUri);
        }
        if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var url) || OwnAvatarPath().Match(url.AbsolutePath) is not { Success: true } match) {
            return null;
        }
        var name = Uri.UnescapeDataString(match.Groups["name"].Value);
        return OwnAvatarName().Match(name) is { Success: true } own && Guid.TryParse(own.Groups["id"].Value, out var owner) && owner == user
            ? (StorageCache.AvatarBucket, name)
            : null;
    }

    private static readonly IReadOnlyList<(string, NpgsqlDbType)> BlobColumns = [
        ("id", NpgsqlDbType.Uuid), ("purpose", NpgsqlDbType.Text), ("content_type", NpgsqlDbType.Text), ("byte_size", NpgsqlDbType.Integer),
        ("sha256", NpgsqlDbType.Bytea), ("bytes", NpgsqlDbType.Bytea), ("created_by", NpgsqlDbType.Uuid), ("created_at", NpgsqlDbType.TimestampTz),
    ];

    // The checks fetch-storage made, repeated: the cache lives across runs and is read back unverified.
    private static string? Unusable(CachedObject? cached) => cached switch {
        null => "not_staged",
        _ when !SHA256.HashData(cached.Bytes).AsSpan().SequenceEqual(cached.Sha256) => "cache_corrupt",
        _ when cached.Bytes.Length > StorageCache.MaxAvatarBytes || ImageSniffer.Detect(cached.Bytes) is null => "not_image",
        _ => null,
    };

    private static async Task WriteManifestAsync(
        ImportContext context,
        IReadOnlyList<(string Bucket, string Name, long Size)> objects,
        Dictionary<(string Bucket, string Path), CachedObject> cache,
        Dictionary<string, (Guid User, string? Reason)> referenced,
        CancellationToken cancellationToken) {
        var tally = context.Report.Table(ObjectsTable);
        var rows = new List<object?[]>();
        foreach (var (bucket, name, size) in objects) {
            var sha256 = cache.GetValueOrDefault((bucket, name))?.Sha256;
            if (bucket == StorageCache.AvatarBucket && referenced.TryGetValue(name, out var avatar)) {
                if (avatar.Reason is null) {
                    rows.Add([bucket, name, size, sha256, "imported", AvatarBlobId(avatar.User), context.RunId]);
                    tally.Imported++;
                } else {
                    rows.Add([bucket, name, size, sha256, "skipped", null, context.RunId]);
                    tally.Skip(avatar.Reason);
                }
            } else if (bucket == StorageCache.AvatarBucket && IsKeptUsersOwnAvatar(context, name)) {
                // Staged or not, the profile does not point at it (an upload whose profile save was cancelled).
                rows.Add([bucket, name, size, sha256, "skipped", null, context.RunId]);
                tally.Skip("not_profile_avatar");
            } else {
                rows.Add([bucket, name, size, null, "manifest_only", null, context.RunId]);
                tally.Archived++;
            }
        }
        // storage.objects rows without a bucket or a name cannot be addressed and are not listed.
        if (tally.Source > objects.Count) {
            tally.Skip("unnamed", tally.Source - objects.Count);
        }

        await TargetRows.CopyAsync(context.Target, "archive.storage_objects", [
            ("bucket", NpgsqlDbType.Text), ("path", NpgsqlDbType.Text), ("byte_size", NpgsqlDbType.Bigint), ("sha256", NpgsqlDbType.Bytea),
            ("disposition", NpgsqlDbType.Text), ("blob_id", NpgsqlDbType.Uuid), ("import_run", NpgsqlDbType.Uuid),
        ], rows, cancellationToken);
    }

    private static bool IsKeptUsersOwnAvatar(ImportContext context, string name) =>
        OwnAvatarName().Match(name) is { Success: true } match && Guid.TryParse(match.Groups["id"].Value, out var user)
            && context.Principals.IsKeptUser(user);

    private static async Task SetAvatarsAsync(NpgsqlConnection target, Guid[] users, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand("""
            update users u set avatar_blob_id = x.blob
            from unnest($1::uuid[], $2::uuid[]) x(id, blob)
            where u.id = x.id
            """, target) {
            Parameters = {
                new() { Value = users, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid },
                new() { Value = users.Select(AvatarBlobId).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid },
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<(string Bucket, string Path), CachedObject>> ReadCacheAsync(NpgsqlConnection target, CancellationToken cancellationToken) {
        var cache = new Dictionary<(string, string), CachedObject>();
        await using var command = new NpgsqlCommand("select bucket, path, sha256, bytes from archive.storage_cache", target);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            cache[(reader.GetString(0), reader.GetString(1))] = new CachedObject(reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3));
        }
        return cache;
    }

    private sealed record CachedObject(byte[] Sha256, byte[] Bytes);

    [GeneratedRegex(@"/storage/v1/object/public/avatars/(?<name>[^/]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnAvatarPath();

    [GeneratedRegex(@"^(?<id>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\.[A-Za-z0-9]{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnAvatarName();
}
