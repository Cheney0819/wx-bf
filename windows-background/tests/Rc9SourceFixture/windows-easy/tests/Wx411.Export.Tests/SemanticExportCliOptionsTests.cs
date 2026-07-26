namespace Wx411.Export.Tests;

public sealed class SemanticExportCliOptionsTests
{
    [Fact]
    public void ParseAcceptsRequiredAndOptionalArguments()
    {
        var options = SemanticExportCliOptions.Parse([
            "--input", "input-dir",
            "--output", "output.sqlite",
            "--summary", "summary.json",
            "--overwrite",
        ]);

        Assert.Equal("input-dir", options.InputDirectory);
        Assert.Equal("output.sqlite", options.OutputPath);
        Assert.Equal("summary.json", options.SummaryPath);
        Assert.True(options.Overwrite);
    }

    [Fact]
    public void ParseRejectsMissingRequiredArguments()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            SemanticExportCliOptions.Parse(["--input", "input-dir"]));

        Assert.Contains("--output", error.Message, StringComparison.Ordinal);
    }
}
