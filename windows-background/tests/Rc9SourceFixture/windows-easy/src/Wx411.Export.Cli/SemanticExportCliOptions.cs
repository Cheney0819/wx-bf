namespace Wx411.Export;

public sealed record SemanticExportCliOptions(
    string InputDirectory,
    string OutputPath,
    string? SummaryPath,
    bool Overwrite)
{
    public static SemanticExportCliOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? input = null;
        string? output = null;
        string? summary = null;
        var overwrite = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--input":
                    input = ReadValue(arguments, ref index, argument);
                    break;
                case "--output":
                    output = ReadValue(arguments, ref index, argument);
                    break;
                case "--summary":
                    summary = ReadValue(arguments, ref index, argument);
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {argument}");
            }
        }

        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Missing required argument: --input");
        if (string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("Missing required argument: --output");
        return new SemanticExportCliOptions(input, output, summary, overwrite);
    }

    private static string ReadValue(IReadOnlyList<string> arguments, ref int index, string name)
    {
        if (++index >= arguments.Count || arguments[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value for argument: {name}");
        return arguments[index];
    }
}
