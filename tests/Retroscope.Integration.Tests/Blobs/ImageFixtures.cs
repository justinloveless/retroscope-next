using System.Buffers.Binary;
using System.Text;

namespace Retroscope.Integration.Tests.Blobs;

// 3x2 images written by Pillow 12.1 (one translucent pixel), so each container is what a real encoder emits.
internal static class ImageFixtures {
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAYAAACddGYaAAAAGElEQVR4nGNgYPjfcEJO4z8IM4IIBigAAH5ZB6hkbG/dAAAAAElFTkSuQmCC");

    public static readonly byte[] Jpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAACAAMDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDgVUIoVQFUDAAGABRRRXk4j+LL1f5n65S+CPoj/9k=");

    public static readonly byte[] ProgressiveJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wgARCAACAAMDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAVAQEBAAAAAAAAAAAAAAAAAAAEBv/aAAwDAQACEAMQAAABgAld/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFxEAAwEAAAAAAAAAAAAAAAAAAAECMf/aAAgBAwEBPwGcR//EABcRAAMBAAAAAAAAAAAAAAAAAAABAzL/2gAIAQIBAT8Bppn/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAY/An//xAAXEAEBAQEAAAAAAAAAAAAAAAABEQAh/9oACAEBAAE/IQhDhv/aAAwDAQACAAMAAAAQA//EABYRAAMAAAAAAAAAAAAAAAAAAAABof/aAAgBAwEBPxCZH//EABcRAAMBAAAAAAAAAAAAAAAAAAABobH/2gAIAQIBAT8Qqen/xAAWEAEBAQAAAAAAAAAAAAAAAAABEQD/2gAIAQEAAT8QAgAIAQDf/9k=");

    public static readonly byte[] Gif = Convert.FromBase64String(
        "R0lGODdhAwACAIEAAMgeKAAA/wAAAAAAACwAAAAAAwACAAAIBwADABg4MCAAOw==");

    public static readonly byte[] WebpLossy = Convert.FromBase64String(
        "UklGRmoAAABXRUJQVlA4WAoAAAAQAAAAAgAAAQAAQUxQSAcAAAAAgP//////AFZQOCA8AAAA0AEAnQEqAwACAAFAJiWoAnS6AJ4ABiAA/vFP/6MdAfK/k+pIT5EDK29Dhf/drcPf/vGf/2fnwn/WgAAA");

    public static readonly byte[] WebpLossless = Convert.FromBase64String(
        "UklGRiIAAABXRUJQVlA4TBYAAAAvAkAAEA8wHoP8lP//AQYPbnAQ0f8Q");

    public static readonly byte[] WebpAnimated = Convert.FromBase64String(
        "UklGRuYAAABXRUJQVlA4WAoAAAASAAAAAgAAAQAAQU5JTQYAAAAAAAAAAABBTk1GZgAAAAAAAAAAAAIAAAEAAGQAAAJBTFBIBwAAAACA//////8AVlA4ID4AAABQAQCdASoDAAIAAUAmJagABHQAAP7xGrfy23qn8QV/9SQ2aXvb72a7RGZ///drcQ8X/94z//7Pz4u7/MgAAEFOTUZMAAAAAAAAAAAAAgAAAQAAZAAAAFZQOCA0AAAANAEAnQEqAwACAAAAJiWgAANwAP7pIh//958//7nz//ufP+jP//8p+//yOP//I4/+UCAAAA==");

    public static readonly byte[] GifAnimated = Convert.FromBase64String(
        "R0lGODlhAwACAIEAAMgeKAAA/wAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAAAwACAAAIBwADABg4MCAAIfkEAQoAAQAsAAAAAAMAAgCBAP8AAAAAAAAAAAAACAYAAQgcGBAAOw==");

    public static TheoryData<string, byte[], string> All => new() {
        { nameof(Png), Png, "image/png" },
        { nameof(Jpeg), Jpeg, "image/jpeg" },
        { nameof(ProgressiveJpeg), ProgressiveJpeg, "image/jpeg" },
        { nameof(Gif), Gif, "image/gif" },
        { nameof(GifAnimated), GifAnimated, "image/gif" },
        { nameof(WebpLossy), WebpLossy, "image/webp" },
        { nameof(WebpLossless), WebpLossless, "image/webp" },
        { nameof(WebpAnimated), WebpAnimated, "image/webp" },
    };

    /// <summary><see cref="Png"/> grown to exactly <paramref name="size"/> bytes by a private ancillary chunk after IHDR.</summary>
    public static byte[] PngOfSize(int size) {
        const int AfterHeader = 8 + 25;
        var padding = size - Png.Length - 12;
        var chunk = new byte[12 + padding];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)padding);
        "prIv"u8.CopyTo(chunk.AsSpan(4));
        return [.. Png.AsSpan(0, AfterHeader), .. chunk, .. Png.AsSpan(AfterHeader)];
    }

    public static byte[] Append(byte[] image, string text) => [.. image, .. Encoding.UTF8.GetBytes(text)];
}
