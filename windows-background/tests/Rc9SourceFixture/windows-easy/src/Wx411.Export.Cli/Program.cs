using Wx411.Export;

const string usage = """
    Wx411 semantic export v2

    Usage:
      Wx411.Export.Cli --input <directory> --output <sqlite> [--summary <json>] [--overwrite]
    """;

if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
{
    Console.WriteLine(usage);
    return args.Length == 0 ? 2 : 0;
}

try
{
    var options = SemanticExportCliOptions.Parse(args);
    var progress = new Progress<SemanticExportProgress>(update =>
        Console.WriteLine($"[{update.Percent,3}%] {update.Stage}{(update.Detail is null ? string.Empty : $": {update.Detail}")}"));
    var result = await new SemanticExportService().ExportAsync(
        new SemanticExportRequest(
            options.InputDirectory,
            options.OutputPath,
            options.SummaryPath,
            options.Overwrite),
        progress);

    Console.WriteLine($"output={result.OutputPath}");
    Console.WriteLine($"summary={result.SummaryPath}");
    Console.WriteLine($"messages={result.MessageCount}");
    Console.WriteLine($"conversations={result.ConversationCount}");
    Console.WriteLine($"identities={result.IdentityCount}");
    Console.WriteLine($"chatrooms={result.ChatRoomCount}");
    Console.WriteLine($"chatroom_members={result.ChatRoomMemberCount}");
    Console.WriteLine($"sha256={result.OutputSha256}");
    foreach (var warning in result.Warnings)
        Console.WriteLine($"warning={warning}");
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or
                                  UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(usage);
    return 2;
}
