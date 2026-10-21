using Retroscope.Importer.Source;

namespace Retroscope.Importer.Tests.Infrastructure;

[Trait("Category", "Source")]
public sealed class HtmlTextOnlyTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"html-textonly-{Guid.NewGuid():N}.json");

    private static IReadOnlySet<string> TextOnly => HtmlTagPolicy.TextOnly;

    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task The_cli_option_extends_the_text_only_tags_and_the_histogram_accepts_them() {
        File.WriteAllText(_path, """{"comment": "operator additions", "tags": ["rs-probe-cli"]}""");

        Assert.Equal(2, await RunCliAsync("no-such-verb", "--html-textonly", _path));

        Assert.Contains("rs-probe-cli", TextOnly);
        Assert.Contains("span", TextOnly);
        var histogram = new HtmlHistogram();
        histogram.Add("<p><rs-probe-cli>kept as text</rs-probe-cli></p>", "card#1");
        histogram.ThrowIfUnknown();
    }

    [Fact]
    public async Task The_cli_option_needs_a_path() {
        Assert.Equal(2, await RunCliAsync("plan", "--html-textonly"));
    }

    [Fact]
    public async Task An_unreadable_file_refuses_the_run() {
        Assert.Equal(1, await RunCliAsync("plan", "--html-textonly", _path));
    }

    [Theory]
    [InlineData("""{"tags": ["script"]}""", "<script> is already converted or dropped")]
    [InlineData("""{"tags": ["img"]}""", "<img> is already converted or dropped")]
    [InlineData("""{"tags": ["Table"]}""", "\"Table\" is not a lower-case tag name")]
    [InlineData("""{"tags": [null]}""", "\"\" is not a lower-case tag name")]
    [InlineData("""{"names": ["table"]}""", "--html-textonly")]
    [InlineData("""not json""", "--html-textonly")]
    public void A_file_that_would_change_converted_or_dropped_tags_or_is_malformed_is_refused(string json, string message) {
        File.WriteAllText(_path, json);

        var failure = Assert.Throws<SettingsException>(() => HtmlTagPolicy.AddTextOnly(_path));

        Assert.Contains(message, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("table", TextOnly);
    }

    private static async Task<int> RunCliAsync(params string[] args) {
        var result = typeof(HtmlTagPolicy).Assembly.EntryPoint!.Invoke(null, [args]);
        return result is Task<int> task ? await task : (int)result!;
    }
}
