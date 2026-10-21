using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;

namespace Retroscope.Server.Endpoints;

/// <summary><c>GET /blob/{id}</c> and <c>POST /api/retro/{id}/images</c> (§d).</summary>
public static class BlobEndpoints {
    public const string BlobContentSecurityPolicy = "default-src 'none'; sandbox";

    public static readonly RateLimit Read = RateLimit.Define(
        "blob.read",
        RateRule.PerIp(600, TimeSpan.FromMinutes(1)),
        RateRule.PerPrincipal(600, TimeSpan.FromMinutes(1)));

    /// <summary>S7: per principal and per IP, so fresh guest principals do not lift the limit.</summary>
    public static readonly RateLimit Upload = RateLimit.Define(
        "retro.images",
        RateRule.PerPrincipal(30, TimeSpan.FromHours(1)),
        RateRule.PerIp(60, TimeSpan.FromHours(1)));

    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton(_ => BlobOptions.FromConfiguration(configuration));
        services.AddSingleton<BlobsRepo>();
    }

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet("/blob/{id:guid}", GetAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(Read);
        app.MapPost("/api/retro/{id:guid}/images", UploadAsync)
            .RequireRoutePolicy(RoutePolicy.BoardAccess)
            .RequireRateLimit(Upload);
    }

    private static async Task<IResult> GetAsync(HttpContext context, Guid id, BlobsRepo blobs) {
        var caller = await context.GetCallerAsync();
        var blob = await blobs.GetAsync(new BlobId(id), caller?.Principal.Id, context.RequestAborted);
        // A board image the caller may not see answers exactly like a missing blob.
        if (blob is null
            || (blob.Purpose == BlobPurpose.BoardImage
                && (blob.Board is null || !Policy.Can(caller?.Principal, Capability.Subscribe, blob.Board.Facts, blob.Board.Access)))) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = BlobContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        // Blobs never change under an id, so both kinds are cached for good; board images only privately.
        headers.CacheControl = blob.Purpose == BlobPurpose.Avatar
            ? "public, max-age=31536000, immutable"
            : "private, max-age=31536000, immutable";
        return Results.Bytes(blob.Bytes, blob.ContentType);
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        Guid id,
        BlobsRepo blobs,
        BlobOptions options,
        ILoggerFactory loggers) {
        var who = (await context.GetCallerAsync())!.Principal;
        var board = await blobs.GetBoardAsync(new BoardId(id), who.Id, context.RequestAborted);
        if (board is null || !Policy.Can(who, Capability.Subscribe, board.Facts, board.Access)) {
            return Problem.Result(StatusCodes.Status404NotFound, "not_found");
        }
        if (board.Facts.Archived) {
            return Problem.Result(StatusCodes.Status403Forbidden, "board_archived");
        }
        if (!Policy.Can(who, Capability.RetroAddCard, board.Facts, board.Access)) {
            return Problem.Result(StatusCodes.Status403Forbidden, "forbidden");
        }
        if (!StageAllowsAdding(board)) {
            return Problem.Result(StatusCodes.Status403Forbidden, "stage_forbids");
        }

        var body = await ReadBodyAsync(context.Request, BlobsRepo.BoardImageMaxBytes, context.RequestAborted);
        if (body is null) {
            return Problem.Result(StatusCodes.Status413PayloadTooLarge, "too_large");
        }
        if (!SniffedImage.TryCreate(body, out var image)) {
            return Problem.Result(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");
        }

        var blobId = new BlobId(Guid.NewGuid());
        var outcome = await blobs.InsertBoardImageAsync(
            blobId,
            new BoardId(id),
            who.Id,
            image,
            options.BoardImageBytesMax,
            context.RequestAborted);
        switch (outcome) {
            case BoardImageInsert.BoardFull:
                return Problem.Result(StatusCodes.Status409Conflict, "board_quota");
            case BoardImageInsert.StoreFull:
                BlobLog.StoreFull(loggers.CreateLogger(typeof(BlobEndpoints)), options.BoardImageBytesMax);
                return Problem.Result(StatusCodes.Status507InsufficientStorage, "storage_full");
        }
        return Results.Json(
            new BlobCreated(blobId),
            BlobJsonContext.Default.BlobCreated,
            statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// Whether some column of the board accepts new content now: the stage gate for its most permissive column, which
    /// is the action-items column when the board has one. Only <c>StagesEnabled</c> of the config matters to the gate.
    /// </summary>
    private static bool StageAllowsAdding(BoardView board) {
        var config = RetroDefaults.Config with { StagesEnabled = board.StagesEnabled };
        return StageRules.AllowsContent(config, board.Stage, actionItemsColumn: board.HasActionItemsColumn);
    }

    /// <returns>Null when the body is longer than <paramref name="max"/>.</returns>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int max, CancellationToken cancellationToken) {
        if (request.ContentLength > max) {
            return null;
        }
        var buffer = new byte[request.ContentLength is { } length ? length + 1 : max + 1];
        var total = 0;
        int read;
        while (total < buffer.Length
            && (read = await request.Body.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0) {
            total += read;
        }
        return total > max ? null : buffer[..total];
    }
}

/// <param name="BoardImageBytesMax">The global ceiling on all board images together (<c>BLOB_BYTES_MAX</c>, S7).</param>
public sealed record BlobOptions(long BoardImageBytesMax) {
    public const long DefaultBoardImageBytesMax = 2L * 1024 * 1024 * 1024;

    public static BlobOptions FromConfiguration(IConfiguration configuration) {
        var value = configuration["BLOB_BYTES_MAX"];
        if (string.IsNullOrWhiteSpace(value)) {
            return new BlobOptions(DefaultBoardImageBytesMax);
        }
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var max) && max > 0
            ? new BlobOptions(max)
            : throw new InvalidOperationException("BLOB_BYTES_MAX must be a positive number of bytes.");
    }
}

public sealed record BlobCreated(BlobId BlobId);

/// <summary>Bytes that <see cref="ImageSniffer"/> accepted; the only thing <see cref="BlobsRepo"/> stores.</summary>
public sealed class SniffedImage {
    private SniffedImage(string contentType, byte[] bytes) {
        ContentType = contentType;
        Bytes = bytes;
        Sha256 = SHA256.HashData(bytes);
    }

    public string ContentType { get; }

    public byte[] Bytes { get; }

    public byte[] Sha256 { get; }

    public static bool TryCreate(byte[] bytes, [NotNullWhen(true)] out SniffedImage? image) {
        var contentType = ImageSniffer.Detect(bytes);
        image = contentType is null ? null : new SniffedImage(contentType, bytes);
        return image is not null;
    }
}

/// <summary>
/// The content type by magic bytes and container structure, never by the declared type. The whole container is
/// walked and must end exactly at its end marker, so a polyglot (an image with HTML, a script or an archive
/// appended, or a bare signature in front of something else) is refused.
/// </summary>
public static class ImageSniffer {
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static string? Detect(ReadOnlySpan<byte> bytes) {
        if (IsPng(bytes)) {
            return "image/png";
        }
        if (IsJpeg(bytes)) {
            return "image/jpeg";
        }
        if (IsGif(bytes)) {
            return "image/gif";
        }
        return IsWebp(bytes) ? "image/webp" : null;
    }

    // Chunks: length, type, data, CRC. IHDR first, at least one IDAT, IEND last.
    private static bool IsPng(ReadOnlySpan<byte> b) {
        if (!b.StartsWith(PngSignature)) {
            return false;
        }
        var pos = 8;
        var sawData = false;
        while (pos + 12 <= b.Length) {
            long length = BinaryPrimitives.ReadUInt32BigEndian(b[pos..]);
            var type = b.Slice(pos + 4, 4);
            if (pos + 12 + length > b.Length || pos == 8 && (!type.SequenceEqual("IHDR"u8) || length != 13)) {
                return false;
            }
            pos += 12 + (int)length;
            sawData |= type.SequenceEqual("IDAT"u8);
            if (type.SequenceEqual("IEND"u8)) {
                return length == 0 && sawData && pos == b.Length;
            }
        }
        return false;
    }

    // Marker segments up to each scan, the entropy-coded data after it, and EOI as the last two bytes.
    private static bool IsJpeg(ReadOnlySpan<byte> b) {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8 || b[2] != 0xFF) {
            return false;
        }
        var pos = 2;
        var sawFrame = false;
        var sawScan = false;
        while (pos < b.Length) {
            if (b[pos] != 0xFF) {
                return false;
            }
            while (pos < b.Length && b[pos] == 0xFF) {
                pos++;
            }
            if (pos >= b.Length) {
                return false;
            }
            var marker = b[pos++];
            if (marker == 0xD9) {
                return sawScan && pos == b.Length;
            }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) {
                continue;
            }
            if (marker is 0x00 or 0xD8 || pos + 2 > b.Length) {
                return false;
            }
            int length = BinaryPrimitives.ReadUInt16BigEndian(b[pos..]);
            if (length < 2 || pos + length > b.Length) {
                return false;
            }
            pos += length;
            sawFrame |= marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (marker == 0xDA) {
                if (!sawFrame) {
                    return false;
                }
                sawScan = true;
                pos = SkipEntropyCoded(b, pos);
            }
        }
        return false;
    }

    /// <returns>The index of the next marker's 0xFF, or the length when the data runs to the end.</returns>
    private static int SkipEntropyCoded(ReadOnlySpan<byte> b, int pos) {
        while (pos + 1 < b.Length) {
            var next = b[pos..].IndexOf((byte)0xFF);
            if (next < 0) {
                return b.Length;
            }
            pos += next;
            if (pos + 1 >= b.Length) {
                return b.Length;
            }
            var following = b[pos + 1];
            if (following is 0x00 or >= 0xD0 and <= 0xD7) {
                pos += 2;
            } else if (following == 0xFF) {
                pos++;
            } else {
                return pos;
            }
        }
        return b.Length;
    }

    // Header, logical screen descriptor and global colour table, then extension and image blocks up to the trailer.
    private static bool IsGif(ReadOnlySpan<byte> b) {
        if (b.Length < 14 || !b.StartsWith("GIF87a"u8) && !b.StartsWith("GIF89a"u8)) {
            return false;
        }
        var pos = 13 + ColorTableSize(b[10]);
        var sawImage = false;
        while (pos >= 0 && pos < b.Length) {
            switch (b[pos]) {
                case 0x3B:
                    return sawImage && pos + 1 == b.Length;
                case 0x21:
                    pos = SkipSubBlocks(b, pos + 2);
                    break;
                case 0x2C when pos + 10 < b.Length:
                    pos = SkipSubBlocks(b, pos + 10 + ColorTableSize(b[pos + 9]) + 1);
                    sawImage = true;
                    break;
                default:
                    return false;
            }
        }
        return false;
    }

    private static int ColorTableSize(byte packed) => (packed & 0x80) == 0 ? 0 : 3 << ((packed & 0x07) + 1);

    /// <returns>The position after the terminating empty sub-block, or -1 when the data ends first.</returns>
    private static int SkipSubBlocks(ReadOnlySpan<byte> b, int pos) {
        while (pos < b.Length) {
            var size = b[pos];
            pos += 1 + size;
            if (size == 0) {
                return pos;
            }
        }
        return -1;
    }

    // RIFF whose declared size is the file, then even-padded chunks; the first is VP8, VP8L or VP8X.
    private static bool IsWebp(ReadOnlySpan<byte> b) {
        if (b.Length < 20 || !b.StartsWith("RIFF"u8) || !b.Slice(8, 4).SequenceEqual("WEBP"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) != (uint)(b.Length - 8)) {
            return false;
        }
        var pos = 12;
        var sawImage = false;
        while (pos + 8 <= b.Length) {
            var type = b.Slice(pos, 4);
            var image = type.SequenceEqual("VP8 "u8) || type.SequenceEqual("VP8L"u8);
            if (pos == 12 && !image && !type.SequenceEqual("VP8X"u8)) {
                return false;
            }
            sawImage |= image || type.SequenceEqual("ANMF"u8);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(b[(pos + 4)..]);
            var end = pos + 8 + size + (size & 1);
            if (end > b.Length) {
                return false;
            }
            pos = (int)end;
        }
        return sawImage && pos == b.Length;
    }
}

internal static partial class BlobLog {
    [LoggerMessage(1, LogLevel.Error, "Board image storage is full: BLOB_BYTES_MAX is {BytesMax} bytes; uploads are refused with 507")]
    public static partial void StoreFull(ILogger logger, long bytesMax);
}
