using Npgsql;
using NpgsqlTypes;

namespace Retroscope.Importer.Html;

/// <summary>
/// The board blobs extracted from converted bodies. One image pasted twice on a board is one blob. Its
/// <c>created_by</c> is the first author's, and NULL as soon as any anonymous body uses it (S6), so a shared blob
/// can never point back at an anonymous author.
/// </summary>
public sealed class BoardImages {
    private readonly Dictionary<Guid, (ExtractedImage Image, Guid? CreatedBy, DateTime CreatedAt)> _blobs = [];

    public int Count => _blobs.Count;

    public void Add(HtmlConversion conversion, Guid? createdBy, bool anonymous, DateTime createdAt) {
        foreach (var image in conversion.Images) {
            if (_blobs.TryGetValue(image.BlobId, out var existing)) {
                _blobs[image.BlobId] = existing with { CreatedBy = anonymous ? null : existing.CreatedBy };
            } else {
                _blobs[image.BlobId] = (image, anonymous ? null : createdBy, createdAt);
            }
        }
    }

    /// <summary>
    /// Inserts every blob as attached (bodies reference them), skipping ids an earlier stage already wrote. A blob
    /// already written keeps its row; when this set saw it in an anonymous body its creator is cleared.
    /// </summary>
    public async Task WriteAsync(NpgsqlConnection target, DateTime attachedAt, CancellationToken cancellationToken) {
        foreach (var (id, (image, createdBy, createdAt)) in _blobs.OrderBy(x => x.Key)) {
            await using var command = new NpgsqlCommand("""
                insert into blobs (id, purpose, board_id, content_type, byte_size, sha256, bytes, created_by, attached_at, created_at)
                values ($1, 'board_image', $2, $3, $4, $5, $6, $7, $8, $9)
                on conflict (id) do update set created_by = case when excluded.created_by is null then null else blobs.created_by end
                """, target) {
                Parameters = {
                    new() { Value = id },
                    new() { Value = image.BoardId },
                    new() { Value = image.ContentType },
                    new() { Value = image.Bytes.Length },
                    new() { Value = image.Sha256, NpgsqlDbType = NpgsqlDbType.Bytea },
                    new() { Value = image.Bytes, NpgsqlDbType = NpgsqlDbType.Bytea },
                    new() { Value = (object?)createdBy ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid },
                    new() { Value = attachedAt, NpgsqlDbType = NpgsqlDbType.TimestampTz },
                    new() { Value = createdAt, NpgsqlDbType = NpgsqlDbType.TimestampTz },
                },
            };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
