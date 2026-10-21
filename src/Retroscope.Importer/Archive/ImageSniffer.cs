namespace Retroscope.Importer.Archive;

/// <summary>Image type by magic bytes only (never by name or declared content type). PNG, JPEG and WebP are accepted.</summary>
public static class ImageSniffer {
    public static string? Detect(ReadOnlySpan<byte> bytes) {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) {
            return "image/png";
        }
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) {
            return "image/jpeg";
        }
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) {
            return "image/webp";
        }
        return null;
    }
}
