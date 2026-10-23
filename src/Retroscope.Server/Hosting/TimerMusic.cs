using System.Text.Json;

namespace Retroscope.Server.Hosting;

/// <summary>
/// The optional background track for the retro timer (UX13). The deployer mounts an audio file into the container
/// and names it in <c>MUSIC_FILE</c>; it is never part of the repository or the image. The file is checked once at
/// startup, and anything wrong with it disables the route with a warning rather than stopping the server, because
/// the timer falls back to its generated music. The bytes stay on disk and are streamed with range support, so a
/// browser can seek to the shared position without fetching the whole file.
/// </summary>
public sealed class TimerMusic {
    public const string Path = "/media/timer-music";
    public const string CacheControl = "public, max-age=86400";
    public const long MaxBytes = 32 * 1024 * 1024;

    public static readonly RateLimit Limit = RateLimit.Define("media.timer-music", RateRule.PerIp(120, TimeSpan.FromMinutes(1)));

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase) {
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".oga"] = "audio/ogg",
        [".m4a"] = "audio/mp4",
    };

    private TimerMusic(string? file, string? contentType) {
        File = file;
        ContentType = contentType;
    }

    /// <summary>The absolute path of a file that passed the startup checks, or null when there is none.</summary>
    public string? File { get; }

    public string? ContentType { get; }

    public bool Enabled => File is not null;

    public static TimerMusic FromConfiguration(IConfiguration configuration, ILogger logger) {
        var file = configuration["MUSIC_FILE"];
        if (string.IsNullOrWhiteSpace(file)) {
            return new TimerMusic(null, null);
        }
        var refusal = Check(file, out var contentType);
        if (refusal is not null) {
            TimerMusicLog.Disabled(logger, refusal);
            return new TimerMusic(null, null);
        }
        TimerMusicLog.Enabled(logger, contentType!);
        return new TimerMusic(file, contentType);
    }

    // The message names the problem but not the path: logs may be shared, and the path is the deployer's.
    private static string? Check(string file, out string? contentType) {
        contentType = null;
        if (!System.IO.Path.IsPathFullyQualified(file)) {
            return "MUSIC_FILE must be an absolute path inside the container";
        }
        if (!ContentTypes.TryGetValue(System.IO.Path.GetExtension(file), out var type)) {
            return "MUSIC_FILE must end in .mp3, .ogg, .oga or .m4a";
        }
        var info = new FileInfo(file);
        if (!info.Exists) {
            return "MUSIC_FILE does not exist or is not a regular file";
        }
        if (info.Length == 0 || info.Length > MaxBytes) {
            return $"MUSIC_FILE must hold between 1 byte and {MaxBytes / (1024 * 1024)} MiB";
        }
        Span<byte> head = stackalloc byte[12];
        int read;
        try {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            return "MUSIC_FILE cannot be read by the server";
        }
        if (!LooksLike(type, head[..read])) {
            return $"MUSIC_FILE does not start like {type}";
        }
        contentType = type;
        return null;
    }

    private static bool LooksLike(string contentType, ReadOnlySpan<byte> head) => contentType switch {
        "audio/mpeg" => head.StartsWith("ID3"u8) || (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0),
        "audio/ogg" => head.StartsWith("OggS"u8),
        "audio/mp4" => head.Length >= 8 && head[4..8].SequenceEqual("ftyp"u8),
        _ => false,
    };

    public static void Map(IEndpointRouteBuilder app) {
        // Resolved here so a bad MUSIC_FILE is reported when the server starts, not on the first request.
        var music = app.ServiceProvider.GetRequiredService<TimerMusic>();
        app.MapGet(Path, music.ServeAsync)
            .RequireRoutePolicy(RoutePolicy.Public)
            .RequireRateLimit(Limit)
            .ExcludeFromDescription();
    }

    public Task ServeAsync(HttpContext context) {
        var info = File is null ? null : new FileInfo(File);
        if (info is not { Exists: true }) {
            return Problem.WriteAsync(context, StatusCodes.Status404NotFound, "not_found");
        }
        var lastModified = info.LastWriteTimeUtc;
        var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
            $"\"{info.Length:x}-{lastModified.Ticks:x}\"");
        context.Response.Headers.CacheControl = CacheControl;
        return TypedResults.PhysicalFile(info.FullName, ContentType, lastModified: lastModified, entityTag: etag, enableRangeProcessing: true)
            .ExecuteAsync(context);
    }
}

/// <summary>Tells the retro page whether a track exists, so it needs no extra request to find out.</summary>
public sealed class TimerMusicBootContributor(TimerMusic music) : IBootContributor {
    private static readonly JsonElement True = JsonDocument.Parse("true").RootElement.Clone();

    public ValueTask ContributeAsync(BootContext boot, CancellationToken cancellationToken) {
        if (music.Enabled && boot.Route.Pattern == "/retro/:code") {
            boot.Set("timerMusic", True);
        }
        return ValueTask.CompletedTask;
    }
}

internal static partial class TimerMusicLog {
    [LoggerMessage(20, LogLevel.Warning, "Timer music is off: {Reason}. The timer plays its generated music instead.")]
    public static partial void Disabled(ILogger logger, string reason);

    [LoggerMessage(21, LogLevel.Information, "Timer music is served from MUSIC_FILE as {ContentType}")]
    public static partial void Enabled(ILogger logger, string contentType);
}
