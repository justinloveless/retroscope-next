using System.Text;
using Retroscope.Server.Endpoints;
using static Retroscope.Integration.Tests.Blobs.ImageFixtures;

namespace Retroscope.Integration.Tests.Blobs;

[Trait("Category", "Blobs")]
public sealed class ImageSnifferTests {
    [Theory]
    [MemberData(nameof(ImageFixtures.All), MemberType = typeof(ImageFixtures))]
    public void Encoder_output_is_recognised_by_its_bytes(string name, byte[] image, string contentType) {
        Assert.True(contentType == ImageSniffer.Detect(image), name);
    }

    [Theory]
    [MemberData(nameof(ImageFixtures.All), MemberType = typeof(ImageFixtures))]
    public void Every_truncation_is_refused(string name, byte[] image, string contentType) {
        for (var length = 0; length < image.Length; length++) {
            Assert.True(ImageSniffer.Detect(image.AsSpan(0, length)) is null, $"{name} ({contentType}) cut to {length} bytes");
        }
    }

    [Theory]
    [MemberData(nameof(ImageFixtures.All), MemberType = typeof(ImageFixtures))]
    public void Anything_after_the_end_of_the_image_is_refused(string name, byte[] image, string contentType) {
        Assert.True(ImageSniffer.Detect([.. image, 0]) is null, $"{name} ({contentType}) + NUL");
        Assert.True(ImageSniffer.Detect(Append(image, "<script>alert(1)</script>")) is null, $"{name} ({contentType}) + script");
    }

    [Fact]
    public void Polyglots_are_refused() {
        // An image followed by markup, a GIF whose tail is JavaScript, a JPEG with a zip archive behind it.
        Assert.Null(ImageSniffer.Detect(Append(Png, "<html><body onload=alert(1)>")));
        Assert.Null(ImageSniffer.Detect(Append(Gif, "*/=alert(document.domain);//")));
        Assert.Null(ImageSniffer.Detect([.. Jpeg, .. "PK\u0003\u0004"u8, .. new byte[26]]));
        // A bare signature in front of something else.
        Assert.Null(ImageSniffer.Detect([.. Png.AsSpan(0, 8), .. "<svg onload=alert(1)>"u8]));
        Assert.Null(ImageSniffer.Detect("GIF89a<html><script>alert(1)</script></html>"u8));
        Assert.Null(ImageSniffer.Detect([0xFF, 0xD8, 0xFF, .. "<html>"u8]));
    }

    [Fact]
    public void A_webp_whose_riff_size_is_not_the_file_size_is_refused() {
        var grown = Append(WebpLossless, "xx");

        Assert.Null(ImageSniffer.Detect(grown));
    }

    [Fact]
    public void Markup_and_other_formats_are_refused() {
        Assert.Null(ImageSniffer.Detect([]));
        Assert.Null(ImageSniffer.Detect("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));
        Assert.Null(ImageSniffer.Detect("<!DOCTYPE html><html></html>"u8));
        Assert.Null(ImageSniffer.Detect("%PDF-1.7\n"u8));
        Assert.Null(ImageSniffer.Detect(Encoding.ASCII.GetBytes("BM" + new string('\0', 60))));
    }

    [Fact]
    public void Ancillary_chunks_inside_a_png_are_allowed() {
        var padded = PngOfSize(4096);

        Assert.Equal(4096, padded.Length);
        Assert.Equal("image/png", ImageSniffer.Detect(padded));
    }
}
