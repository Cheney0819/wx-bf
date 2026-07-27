using System.Text.Json;

namespace DesktopPet.DataSync.Tests;

public sealed class ParserResultValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-parser-result-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ValidDocumentIsAccepted()
    {
        var resultPath = await ParserResultTestData.WriteAsync(_root);

        var result = await new ParserResultValidator()
            .ValidateAsync(resultPath, "job-1", "source-1", default);

        Assert.Equal(2, result.Messages.Count);
        Assert.Single(result.Contacts);
        Assert.Single(result.Favorites);
    }

    [Fact]
    public async Task OptionalNextCursorIsAcceptedAndPreserved()
    {
        var valid = JsonSerializer.SerializeToElement(ParserResultTestData.Document());
        var document = valid.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value);
        document["nextCursor"] = "opaque-cursor";
        var resultPath = await ParserResultTestData.WriteAsync(_root, document);

        var result = await new ParserResultValidator()
            .ValidateAsync(resultPath, "job-1", "source-1", default);

        Assert.Equal("opaque-cursor", result.NextCursor);
    }

    [Theory]
    [InlineData("wrong-job", "source-1")]
    [InlineData("job-1", "wrong-source")]
    public async Task MismatchedResultIdentityIsRejected(string jobId, string sourceSetId)
    {
        var resultPath = await ParserResultTestData.WriteAsync(
            _root,
            ParserResultTestData.Document(jobId, sourceSetId));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ParserResultValidator().ValidateAsync(
                resultPath, "job-1", "source-1", default));
    }

    [Fact]
    public async Task UnknownMemberCarryingAbsolutePathIsRejected()
    {
        var valid = JsonSerializer.SerializeToElement(ParserResultTestData.Document());
        var document = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["jobId"] = "job-1",
            ["sourceSetId"] = "source-1",
            ["messages"] = valid.GetProperty("messages"),
            ["contacts"] = valid.GetProperty("contacts"),
            ["favorites"] = valid.GetProperty("favorites"),
            ["notices"] = valid.GetProperty("notices"),
            ["sourcePath"] = @"C:\Users\secret\database.db",
        };
        var resultPath = await ParserResultTestData.WriteAsync(_root, document);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ParserResultValidator().ValidateAsync(
                resultPath, "job-1", "source-1", default));
    }

    [Fact]
    public async Task MoreThanFiveThousandMessagesIsRejected()
    {
        var valid = JsonSerializer.SerializeToElement(ParserResultTestData.Document());
        var messages = Enumerable.Range(0, 5001)
            .Select(index => ParserResultTestData.Message(index, "message"))
            .ToArray();
        var document = Replace(valid, "messages", messages);
        var resultPath = await ParserResultTestData.WriteAsync(_root, document);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ParserResultValidator().ValidateAsync(
                resultPath, "job-1", "source-1", default));
    }

    [Theory]
    [InlineData("not-base64", "")]
    [InlineData("dm9pY2U=", "0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task InvalidMediaEncodingOrHashIsRejected(string mediaData, string mediaSha256)
    {
        var message = JsonSerializer.SerializeToElement(ParserResultTestData.Message(1, "voice"));
        var changedMessage = message.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value);
        changedMessage["media_type"] = "voice";
        changedMessage["media_data"] = mediaData;
        changedMessage["media_sha256"] = mediaSha256;
        var valid = JsonSerializer.SerializeToElement(ParserResultTestData.Document());
        var resultPath = await ParserResultTestData.WriteAsync(
            _root,
            Replace(valid, "messages", new[] { changedMessage }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ParserResultValidator().ValidateAsync(
                resultPath, "job-1", "source-1", default));
    }

    [Fact]
    public async Task MediaAboveFiveMebibytesIsRejected()
    {
        var media = new byte[5 * 1024 * 1024 + 1];
        var message = JsonSerializer.SerializeToElement(ParserResultTestData.Message(1, "voice"));
        var changedMessage = message.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value);
        changedMessage["media_type"] = "voice";
        changedMessage["media_data"] = Convert.ToBase64String(media);
        changedMessage["media_sha256"] = ParserResultTestData.Sha256(media);
        var valid = JsonSerializer.SerializeToElement(ParserResultTestData.Document());
        var resultPath = await ParserResultTestData.WriteAsync(
            _root,
            Replace(valid, "messages", new[] { changedMessage }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ParserResultValidator().ValidateAsync(
                resultPath, "job-1", "source-1", default));
    }

    [Fact]
    public async Task DuplicateMessageIdentityIsNormalized()
    {
        var valid = JsonSerializer.SerializeToElement(ParserResultTestData.Document());
        var duplicate = ParserResultTestData.Message(1, "hello");
        var resultPath = await ParserResultTestData.WriteAsync(
            _root,
            Replace(valid, "messages", new[] { duplicate, duplicate }));

        var result = await new ParserResultValidator().ValidateAsync(
            resultPath, "job-1", "source-1", default);

        Assert.Single(result.Messages);
    }

    [Fact]
    public async Task DuplicateMessagesAreAllValidatedBeforeNormalization()
    {
        var valid = JsonSerializer.SerializeToElement(ParserResultTestData.Document());
        var first = ParserResultTestData.Message(1, "hello");
        var second = JsonSerializer.SerializeToElement(first)
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value);
        second["media_data"] = "not-base64";
        var resultPath = await ParserResultTestData.WriteAsync(
            _root,
            Replace(valid, "messages", new object[] { first, second }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ParserResultValidator().ValidateAsync(
                resultPath, "job-1", "source-1", default));
    }

    [Fact]
    public async Task DelimiterContainingDistinctMessagesAndFavoritesRemainDistinct()
    {
        var valid = JsonSerializer.SerializeToElement(ParserResultTestData.Document());
        var firstMessage = JsonSerializer.SerializeToElement(ParserResultTestData.Message(1, "right"))
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value);
        firstMessage["sender"] = "left|middle";
        var secondMessage = JsonSerializer.SerializeToElement(ParserResultTestData.Message(1, "middle|right"))
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value);
        secondMessage["sender"] = "left";

        var firstFavorite = Favorite("table|item", "7");
        var secondFavorite = Favorite("table", "item|7");
        var document = valid.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Name switch
            {
                "messages" => (object?)new[] { firstMessage, secondMessage },
                "favorites" => new[] { firstFavorite, secondFavorite },
                _ => property.Value,
            });
        var resultPath = await ParserResultTestData.WriteAsync(_root, document);

        var result = await new ParserResultValidator().ValidateAsync(
            resultPath, "job-1", "source-1", default);

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal(2, result.Favorites.Count);
    }

    [Fact]
    public async Task StringAbove64KiBIsRejected()
    {
        var valid = JsonSerializer.SerializeToElement(ParserResultTestData.Document());
        var resultPath = await ParserResultTestData.WriteAsync(
            _root,
            Replace(valid, "messages", new[]
            {
                ParserResultTestData.Message(1, new string('x', 64 * 1024 + 1)),
            }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ParserResultValidator().ValidateAsync(
                resultPath, "job-1", "source-1", default));
    }

    [Fact]
    public async Task IntegerOutsideSigned64BitRangeIsRejected()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "overflow.json");
        var json = JsonSerializer.Serialize(ParserResultTestData.Document())
            .Replace("\"create_time\":101", "\"create_time\":9223372036854775808", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ParserResultValidator().ValidateAsync(path, "job-1", "source-1", default));
    }

    [Fact]
    public async Task OutputAbove32MebibytesIsRejectedBeforeJsonParsing()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "oversized.json");
        await File.WriteAllBytesAsync(path, new byte[32 * 1024 * 1024 + 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ParserResultValidator().ValidateAsync(path, "job-1", "source-1", default));
    }

    private static Dictionary<string, object?> Replace(
        JsonElement valid,
        string propertyName,
        object replacement) =>
        valid.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Name == propertyName ? replacement : (object?)property.Value);

    private static object Favorite(string sourceTable, string sourceId) => new
    {
        source_table = sourceTable,
        source_id = sourceId,
        title = "Saved title",
        summary = "Saved summary",
        item_type = "link",
        item_sub_type = "",
        source_updated_at = 99L,
        data_json = new Dictionary<string, object?> { ["id"] = 7 },
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
