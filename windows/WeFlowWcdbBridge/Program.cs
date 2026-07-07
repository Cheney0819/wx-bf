using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;

var cliOptions = CliOptions.Parse(args);
var exporter = new WcdbExporter(cliOptions);
var result = exporter.Run();

Console.WriteLine(
    JsonSerializer.Serialize(
        result,
        JsonDefaults.Options
    )
);

return result.Success ? 0 : 1;

internal sealed record CliOptions(
    string AccountDir,
    string Key,
    string Wxid,
    string DllPath,
    string OutputJsonPath,
    int SessionLimit,
    int MessageLimit,
    int MessageOffset
)
{
    public static CliOptions Parse(string[] args)
    {
        string? accountDir = null;
        string? key = null;
        string? wxid = null;
        string? dllPath = null;
        string? outputJsonPath = null;
        var sessionLimit = 200;
        var messageLimit = 500;
        var messageOffset = 0;

        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            if (string.Equals(current, "--account-dir", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                accountDir = args[++index];
                continue;
            }

            if (string.Equals(current, "--key", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                key = args[++index];
                continue;
            }

            if (string.Equals(current, "--wxid", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                wxid = args[++index];
                continue;
            }

            if (string.Equals(current, "--dll", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                dllPath = args[++index];
                continue;
            }

            if (string.Equals(current, "--output-json", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                outputJsonPath = args[++index];
                continue;
            }

            if (string.Equals(current, "--session-limit", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (int.TryParse(args[++index], out var parsed) && parsed > 0)
                {
                    sessionLimit = parsed;
                }
                continue;
            }

            if (string.Equals(current, "--message-limit", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (int.TryParse(args[++index], out var parsed) && parsed > 0)
                {
                    messageLimit = parsed;
                }
                continue;
            }

            if (string.Equals(current, "--message-offset", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (int.TryParse(args[++index], out var parsed) && parsed >= 0)
                {
                    messageOffset = parsed;
                }
            }
        }

        return new CliOptions(
            string.IsNullOrWhiteSpace(accountDir) ? string.Empty : accountDir.Trim(),
            string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim(),
            string.IsNullOrWhiteSpace(wxid) ? string.Empty : wxid.Trim(),
            string.IsNullOrWhiteSpace(dllPath) ? string.Empty : dllPath.Trim(),
            string.IsNullOrWhiteSpace(outputJsonPath) ? string.Empty : outputJsonPath.Trim(),
            sessionLimit,
            messageLimit,
            messageOffset
        );
    }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };
}

internal sealed class WcdbExporter
{
    private static readonly Regex WxidPrefixPattern = new(@"^(wxid_[^_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WxidSuffixPattern = new(@"^(.+)_([a-zA-Z0-9]{4})$", RegexOptions.Compiled);

    private readonly CliOptions _options;

    public WcdbExporter(CliOptions options)
    {
        _options = options;
    }

    public ExportResult Run()
    {
        var startedAt = Stopwatch.StartNew();
        nint dllHandle = 0;
        nint wcdbHandle = 0;
        var loadedNativeHandles = new List<nint>();
        var warnings = new List<string>();
        var initProtectionTriedPaths = new List<string>();
        string dllPath = string.Empty;
        string sessionDbPath = string.Empty;
        string normalizedWxid = NormalizeWxid(_options.Wxid);
        WcdbShutdownDelegate? wcdbShutdown = null;
        WcdbCloseAccountDelegate? wcdbCloseAccount = null;

        try
        {
            if (string.IsNullOrWhiteSpace(_options.AccountDir))
            {
                return Failure("缺少 --account-dir", startedAt.ElapsedMilliseconds, warnings);
            }

            if (string.IsNullOrWhiteSpace(_options.Key))
            {
                return Failure("缺少 --key", startedAt.ElapsedMilliseconds, warnings);
            }

            if (string.IsNullOrWhiteSpace(_options.OutputJsonPath))
            {
                return Failure("缺少 --output-json", startedAt.ElapsedMilliseconds, warnings);
            }

            var accountDir = ResolveDirectory(_options.AccountDir);
            if (!Directory.Exists(accountDir))
            {
                return Failure($"账号目录不存在: {accountDir}", startedAt.ElapsedMilliseconds, warnings);
            }

            if (string.IsNullOrWhiteSpace(normalizedWxid))
            {
                normalizedWxid = NormalizeWxid(Path.GetFileName(accountDir));
            }

            dllPath = ResolveDllPath();
            if (!File.Exists(dllPath))
            {
                return Failure($"缺少 wcdb_api.dll: {dllPath}", startedAt.ElapsedMilliseconds, warnings);
            }

            var dllDirectory = Path.GetDirectoryName(dllPath) ?? AppContext.BaseDirectory;
            foreach (var dependency in new[] { "WCDB.dll", "SDL2.dll" })
            {
                var dependencyPath = Path.Combine(dllDirectory, dependency);
                if (!File.Exists(dependencyPath))
                {
                    warnings.Add($"缺少依赖库 {dependency}");
                    continue;
                }

                try
                {
                    loadedNativeHandles.Add(NativeLibrary.Load(dependencyPath));
                }
                catch (Exception ex)
                {
                    warnings.Add($"预加载 {dependency} 失败: {ex.Message}");
                }
            }

            dllHandle = NativeLibrary.Load(dllPath);
            var initProtection = GetExport<InitProtectionDelegate>(dllHandle, "InitProtection");
            var wcdbInit = GetExport<WcdbInitDelegate>(dllHandle, "wcdb_init");
            wcdbShutdown = GetExportOptional<WcdbShutdownDelegate>(dllHandle, "wcdb_shutdown");
            var wcdbOpenAccount = GetExport<WcdbOpenAccountDelegate>(dllHandle, "wcdb_open_account");
            wcdbCloseAccount = GetExportOptional<WcdbCloseAccountDelegate>(dllHandle, "wcdb_close_account");
            var wcdbSetMyWxid = GetExportOptional<WcdbSetMyWxidDelegate>(dllHandle, "wcdb_set_my_wxid");
            var wcdbGetSessions = GetExport<WcdbGetSessionsDelegate>(dllHandle, "wcdb_get_sessions");
            var wcdbGetMessages = GetExport<WcdbGetMessagesDelegate>(dllHandle, "wcdb_get_messages");
            var wcdbGetDisplayNames = GetExportOptional<WcdbGetDisplayNamesDelegate>(dllHandle, "wcdb_get_display_names");
            var wcdbFreeString = GetExport<WcdbFreeStringDelegate>(dllHandle, "wcdb_free_string");

            var initProtectionResult = InitializeProtection(initProtection, dllDirectory, initProtectionTriedPaths);
            if (initProtectionResult != 0)
            {
                return Failure(
                    $"InitProtection 失败，错误码: {initProtectionResult}",
                    startedAt.ElapsedMilliseconds,
                    warnings,
                    dllPath,
                    accountDir,
                    sessionDbPath,
                    normalizedWxid,
                    initProtectionTriedPaths
                );
            }

            var initResult = wcdbInit();
            if (initResult != 0)
            {
                return Failure(
                    $"wcdb_init 失败，错误码: {initResult}",
                    startedAt.ElapsedMilliseconds,
                    warnings,
                    dllPath,
                    accountDir,
                    sessionDbPath,
                    normalizedWxid,
                    initProtectionTriedPaths
                );
            }

            sessionDbPath = FindSessionDbPath(accountDir);
            if (string.IsNullOrWhiteSpace(sessionDbPath) || !File.Exists(sessionDbPath))
            {
                return Failure(
                    "未找到 session.db",
                    startedAt.ElapsedMilliseconds,
                    warnings,
                    dllPath,
                    accountDir,
                    sessionDbPath,
                    normalizedWxid,
                    initProtectionTriedPaths
                );
            }

            var openResult = wcdbOpenAccount(sessionDbPath, _options.Key, out wcdbHandle);
            if (openResult != 0 || wcdbHandle <= 0)
            {
                return Failure(
                    $"wcdb_open_account 失败，错误码: {openResult}",
                    startedAt.ElapsedMilliseconds,
                    warnings,
                    dllPath,
                    accountDir,
                    sessionDbPath,
                    normalizedWxid,
                    initProtectionTriedPaths
                );
            }

            if (wcdbSetMyWxid is not null && !string.IsNullOrWhiteSpace(normalizedWxid))
            {
                try
                {
                    var setMyWxidResult = wcdbSetMyWxid(wcdbHandle, normalizedWxid);
                    if (setMyWxidResult != 0)
                    {
                        warnings.Add($"wcdb_set_my_wxid 返回错误码: {setMyWxidResult}");
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"wcdb_set_my_wxid 调用失败: {ex.Message}");
                }
            }

            var sessionsJson = ReadJson(
                wcdbFreeString,
                out var sessionsRc,
                out var sessionError,
                () =>
                {
                    var nativeRc = wcdbGetSessions(wcdbHandle, out var outPtr);
                    return (nativeRc, outPtr);
                }
            );
            if (string.IsNullOrWhiteSpace(sessionsJson))
            {
                return Failure(
                    string.IsNullOrWhiteSpace(sessionError) ? $"获取会话失败，错误码: {sessionsRc}" : sessionError,
                    startedAt.ElapsedMilliseconds,
                    warnings,
                    dllPath,
                    accountDir,
                    sessionDbPath,
                    normalizedWxid,
                    initProtectionTriedPaths
                );
            }

            var sessions = ParseSessions(sessionsJson)
                .Where(item => !string.IsNullOrWhiteSpace(item.Username))
                .OrderByDescending(item => item.SortTimestamp)
                .ThenBy(item => item.Username, StringComparer.OrdinalIgnoreCase)
                .Take(_options.SessionLimit)
                .ToList();

            var displayNameMap = ResolveDisplayNames(wcdbHandle, wcdbGetDisplayNames, wcdbFreeString, sessions.Select(item => item.Username), warnings);
            foreach (var session in sessions)
            {
                if (displayNameMap.TryGetValue(session.Username, out var displayName)
                    && !string.IsNullOrWhiteSpace(displayName)
                    && (string.IsNullOrWhiteSpace(session.DisplayName)
                        || string.Equals(session.DisplayName, session.Username, StringComparison.OrdinalIgnoreCase)))
                {
                    session.DisplayName = displayName;
                }
            }

            var exportedMessages = new List<ExportMessage>();
            var dedupeKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var session in sessions)
            {
                var sessionId = session.Username;
                var messagesJson = ReadJson(
                    wcdbFreeString,
                    out var messagesRc,
                    out var messageError,
                    () =>
                    {
                        var nativeRc = wcdbGetMessages(wcdbHandle, sessionId, _options.MessageLimit, _options.MessageOffset, out var outPtr);
                        return (nativeRc, outPtr);
                    }
                );

                if (string.IsNullOrWhiteSpace(messagesJson))
                {
                    warnings.Add(string.IsNullOrWhiteSpace(messageError)
                        ? $"读取会话消息失败: {sessionId}: 错误码 {messagesRc}"
                        : $"读取会话消息失败: {sessionId}: {messageError}");
                    continue;
                }

                foreach (var message in ParseMessages(messagesJson, session, normalizedWxid))
                {
                    var dedupeKey = BuildMessageDedupeKey(message);
                    if (!dedupeKeys.Add(dedupeKey))
                    {
                        continue;
                    }

                    exportedMessages.Add(message);
                }
            }

            var senderDisplayNameMap = ResolveDisplayNames(
                wcdbHandle,
                wcdbGetDisplayNames,
                wcdbFreeString,
                exportedMessages
                    .Select(item => item.InternalSenderUsername)
                    .Where(item => !string.IsNullOrWhiteSpace(item)),
                warnings
            );

            foreach (var message in exportedMessages)
            {
                if (message.IsSender)
                {
                    message.Sender = "我";
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(message.InternalSenderUsername)
                    && senderDisplayNameMap.TryGetValue(message.InternalSenderUsername, out var senderDisplay)
                    && !string.IsNullOrWhiteSpace(senderDisplay))
                {
                    message.Sender = senderDisplay;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(message.InternalSenderUsername))
                {
                    message.Sender = message.InternalSenderUsername;
                    continue;
                }

                message.Sender = message.Nickname;
            }

            exportedMessages = exportedMessages
                .OrderBy(item => item.CreateTime)
                .ThenBy(item => item.Wxid, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.MsgType)
                .ToList();

            var outputPath = Path.GetFullPath(_options.OutputJsonPath);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            File.WriteAllText(
                outputPath,
                JsonSerializer.Serialize(exportedMessages, JsonDefaults.Options),
                new UTF8Encoding(false)
            );

            return new ExportResult
            {
                Success = true,
                Source = "weflow_wcdb",
                DllPath = dllPath,
                AccountDir = accountDir,
                SessionDbPath = sessionDbPath,
                NormalizedWxid = normalizedWxid,
                DurationMs = startedAt.ElapsedMilliseconds,
                SessionCount = sessions.Count,
                ExportedMessageCount = exportedMessages.Count,
                OutputJsonPath = outputPath,
                Sessions = sessions,
                Warnings = warnings,
                InitProtectionTriedPaths = initProtectionTriedPaths,
            };
        }
        catch (Exception ex)
        {
            return Failure(
                ex.Message,
                startedAt.ElapsedMilliseconds,
                warnings,
                dllPath,
                ResolveDirectory(_options.AccountDir),
                sessionDbPath,
                normalizedWxid,
                initProtectionTriedPaths
            );
        }
        finally
        {
            try
            {
                if (wcdbHandle > 0 && wcdbCloseAccount is not null)
                {
                    wcdbCloseAccount(wcdbHandle);
                }
            }
            catch
            {
            }

            try
            {
                if (wcdbShutdown is not null)
                {
                    wcdbShutdown();
                }
            }
            catch
            {
            }

            if (dllHandle != 0)
            {
                NativeLibrary.Free(dllHandle);
            }

            foreach (var handle in loadedNativeHandles)
            {
                try
                {
                    NativeLibrary.Free(handle);
                }
                catch
                {
                }
            }
        }
    }

    private ExportResult Failure(
        string error,
        long durationMs,
        List<string> warnings,
        string dllPath = "",
        string accountDir = "",
        string sessionDbPath = "",
        string normalizedWxid = "",
        List<string>? initProtectionTriedPaths = null)
    {
        return new ExportResult
        {
            Success = false,
            Source = "weflow_wcdb",
            Error = error,
            DurationMs = durationMs,
            DllPath = dllPath,
            AccountDir = accountDir,
            SessionDbPath = sessionDbPath,
            NormalizedWxid = normalizedWxid,
            Warnings = warnings,
            InitProtectionTriedPaths = initProtectionTriedPaths ?? new List<string>(),
        };
    }

    private string ResolveDllPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.DllPath))
        {
            return Path.GetFullPath(_options.DllPath);
        }

        var nativeArch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "resources", "wcdb", "win32", nativeArch, "wcdb_api.dll"),
            Path.Combine(AppContext.BaseDirectory, "resources", "win32", nativeArch, "wcdb_api.dll"),
            Path.Combine(AppContext.BaseDirectory, "wcdb_api.dll"),
            Path.Combine(Directory.GetCurrentDirectory(), "resources", "wcdb", "win32", nativeArch, "wcdb_api.dll"),
            Path.Combine(Directory.GetCurrentDirectory(), "resources", "win32", nativeArch, "wcdb_api.dll"),
            Path.Combine(Directory.GetCurrentDirectory(), "wcdb_api.dll"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return Path.GetFullPath(candidates[0]);
    }

    private static string ResolveDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static string NormalizeWxid(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var prefixMatch = WxidPrefixPattern.Match(text);
        if (prefixMatch.Success)
        {
            return prefixMatch.Groups[1].Value;
        }

        var suffixMatch = WxidSuffixPattern.Match(text);
        return suffixMatch.Success ? suffixMatch.Groups[1].Value : text;
    }

    private static string FindSessionDbPath(string accountDir)
    {
        var normalized = ResolveDirectory(accountDir);
        var dbStorageDir = normalized.EndsWith("db_storage", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : Path.Combine(normalized, "db_storage");

        var directCandidates = new[]
        {
            Path.Combine(dbStorageDir, "session", "session.db"),
            Path.Combine(dbStorageDir, "Session", "session.db"),
            Path.Combine(dbStorageDir, "session.db"),
        };

        foreach (var candidate in directCandidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (!Directory.Exists(dbStorageDir))
        {
            return string.Empty;
        }

        return FindSessionDbRecursive(dbStorageDir, 0) ?? string.Empty;
    }

    private static string? FindSessionDbRecursive(string directory, int depth)
    {
        if (depth > 5)
        {
            return null;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "session.db", SearchOption.TopDirectoryOnly))
            {
                return file;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var found = FindSessionDbRecursive(child, depth + 1);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static int InitializeProtection(
        InitProtectionDelegate initProtection,
        string dllDirectory,
        List<string> triedPaths)
    {
        var nativeArch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var candidates = new[]
        {
            dllDirectory,
            Path.GetDirectoryName(dllDirectory) ?? dllDirectory,
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "resources"),
            Path.Combine(AppContext.BaseDirectory, "resources", "win32", nativeArch),
            Path.Combine(AppContext.BaseDirectory, "resources", "wcdb", "win32", nativeArch),
            Directory.GetCurrentDirectory(),
            Path.Combine(Directory.GetCurrentDirectory(), "resources"),
            Path.Combine(Directory.GetCurrentDirectory(), "resources", "win32", nativeArch),
            Path.Combine(Directory.GetCurrentDirectory(), "resources", "wcdb", "win32", nativeArch),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastCode = int.MinValue;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(candidate);
            }
            catch
            {
                normalized = candidate;
            }

            if (!seen.Add(normalized))
            {
                continue;
            }

            triedPaths.Add(normalized);

            try
            {
                lastCode = initProtection(normalized);
                if (lastCode == 0)
                {
                    return 0;
                }
            }
            catch
            {
            }
        }

        return lastCode == int.MinValue ? -2301 : lastCode;
    }

    private static string? ReadJson(
        WcdbFreeStringDelegate freeString,
        out int rc,
        out string errorMessage,
        Func<(int Rc, nint Ptr)> invoke)
    {
        errorMessage = string.Empty;
        nint rawPtr = 0;
        rc = int.MinValue;
        try
        {
            var nativeResult = invoke();
            rc = nativeResult.Rc;
            rawPtr = nativeResult.Ptr;
            if (rawPtr == 0)
            {
                errorMessage = rc == 0 ? "接口未返回 JSON 指针" : $"接口调用失败，错误码: {rc}";
                return null;
            }

            var text = Marshal.PtrToStringUTF8(rawPtr);
            if (string.IsNullOrWhiteSpace(text))
            {
                errorMessage = "接口返回了空 JSON";
                return null;
            }

            return text;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return null;
        }
        finally
        {
            if (rawPtr != 0)
            {
                try
                {
                    freeString(rawPtr);
                }
                catch
                {
                }
            }
        }
    }

    private static List<SessionSummary> ParseSessions(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new List<SessionSummary>();
        }

        var list = new List<SessionSummary>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var username = PickString(item, "username", "user_name", "userName", "usrName", "UsrName", "talker");
            if (string.IsNullOrWhiteSpace(username))
            {
                continue;
            }

            var displayName = PickString(item, "displayName", "display_name", "remark", "nickName", "nickname");
            var sortTimestamp = PickLong(item, "sort_timestamp", "sortTimestamp", "last_timestamp", "lastTimestamp", "create_time", "createTime");
            list.Add(new SessionSummary
            {
                Username = username,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName,
                SortTimestamp = sortTimestamp,
            });
        }

        return list;
    }

    private static Dictionary<string, string> ResolveDisplayNames(
        nint handle,
        WcdbGetDisplayNamesDelegate? getDisplayNames,
        WcdbFreeStringDelegate freeString,
        IEnumerable<string> usernames,
        List<string> warnings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (getDisplayNames is null)
        {
            return result;
        }

        var list = usernames
            .Select(item => (item ?? string.Empty).Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0)
        {
            return result;
        }

        nint ptr = 0;
        try
        {
            var rc = getDisplayNames(handle, JsonSerializer.Serialize(list, JsonDefaults.Options), out ptr);
            if (rc != 0 || ptr == 0)
            {
                warnings.Add($"wcdb_get_display_names 失败，错误码: {rc}");
                return result;
            }

            var text = Marshal.PtrToStringUTF8(ptr);
            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var username in list)
            {
                if (!doc.RootElement.TryGetProperty(username, out var displayElement))
                {
                    continue;
                }

                var displayName = displayElement.ValueKind == JsonValueKind.String
                    ? displayElement.GetString()
                    : displayElement.ToString();

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                result[username] = displayName.Trim();
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"解析 display name 失败: {ex.Message}");
        }
        finally
        {
            if (ptr != 0)
            {
                try
                {
                    freeString(ptr);
                }
                catch
                {
                }
            }
        }

        return result;
    }

    private static List<ExportMessage> ParseMessages(string json, SessionSummary session, string normalizedWxid)
    {
        using var document = JsonDocument.Parse(PrepareJson(json));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new List<ExportMessage>();
        }

        var list = new List<ExportMessage>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var msgType = (int)PickLong(item, "localType", "local_type", "msg_type", "type");
            var msgSubType = (int)PickLong(item, "msgSubType", "msg_sub_type", "subType", "sub_type");
            var createTime = PickLong(item, "createTime", "create_time", "time", "timestamp");
            var isSender = PickIsSender(item, normalizedWxid);
            var senderUsername = PickString(item, "senderUsername", "sender_username", "realSenderId", "real_sender_id");
            var parsedContent = PickString(item, "parsedContent", "parsed_content");
            var rawContent = PickString(item, "content", "rawContent", "raw_content", "strContent", "str_content");
            var content = NormalizeMessageContent(msgType, parsedContent, rawContent);
            var nickname = string.IsNullOrWhiteSpace(session.DisplayName) ? session.Username : session.DisplayName;

            list.Add(new ExportMessage
            {
                Wxid = session.Username,
                Content = content,
                CreateTime = createTime,
                IsSender = isSender,
                Nickname = nickname,
                Sender = isSender ? "我" : (!string.IsNullOrWhiteSpace(senderUsername) ? senderUsername : nickname),
                MsgType = msgType,
                MsgSubType = msgSubType,
                MediaType = string.Empty,
                MediaMime = string.Empty,
                MediaName = string.Empty,
                MediaData = string.Empty,
                InternalLocalId = PickLong(item, "localId", "local_id"),
                InternalServerId = PickString(item, "serverIdRaw", "server_id", "serverId"),
                InternalSenderUsername = senderUsername,
            });
        }

        return list;
    }

    private static string PrepareJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "[]";
        }

        if (!Regex.IsMatch(json, "\"server_id\"\\s*:\\s*-?\\d{16,}", RegexOptions.IgnoreCase))
        {
            return json;
        }

        return Regex.Replace(
            json,
            "(\"server_id\"\\s*:\\s*)(-?\\d{16,})",
            "$1\"$2\"",
            RegexOptions.IgnoreCase
        );
    }

    private static string BuildMessageDedupeKey(ExportMessage message)
    {
        if (message.InternalLocalId > 0)
        {
            return $"{message.Wxid}\u001f{message.InternalLocalId}";
        }

        if (!string.IsNullOrWhiteSpace(message.InternalServerId))
        {
            return $"{message.Wxid}\u001f{message.InternalServerId}";
        }

        return $"{message.Wxid}\u001f{message.CreateTime}\u001f{message.MsgType}\u001f{message.Content}";
    }

    private static string NormalizeMessageContent(int msgType, string parsedContent, string rawContent)
    {
        var content = (parsedContent ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(content))
        {
            return content.Length > 500 ? content[..500] : content;
        }

        var raw = (rawContent ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(raw) && !LooksLikeOpaqueXml(raw))
        {
            return raw.Length > 500 ? raw[..500] : raw;
        }

        return FallbackMessageContent(msgType, raw);
    }

    private static bool LooksLikeOpaqueXml(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains('<') && value.Contains('>');
    }

    private static string FallbackMessageContent(int msgType, string content)
    {
        if (!string.IsNullOrWhiteSpace(content) && content.Length <= 500 && msgType == 1)
        {
            return content;
        }

        return msgType switch
        {
            3 => "[图片]",
            34 => "[语音]",
            43 => "[视频]",
            47 => "[表情]",
            49 => "[文件/链接]",
            50 => "[通话]",
            10000 => "[系统消息]",
            _ => string.IsNullOrWhiteSpace(content) ? string.Empty : (content.Length > 500 ? content[..500] : content),
        };
    }

    private static bool PickIsSender(JsonElement element, string normalizedWxid)
    {
        if (TryPickBoolean(element, out var boolValue, "is_sender", "isSender", "isSelf", "is_self"))
        {
            return boolValue;
        }

        var numeric = PickLong(element, "is_send", "isSend", "is_sender", "isSender");
        if (numeric != 0)
        {
            return true;
        }

        var sender = PickString(element, "senderUsername", "sender_username", "realSenderId", "real_sender_id");
        if (!string.IsNullOrWhiteSpace(sender) && !string.IsNullOrWhiteSpace(normalizedWxid))
        {
            return string.Equals(NormalizeWxid(sender), normalizedWxid, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool TryPickBoolean(JsonElement element, out bool value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var found))
            {
                continue;
            }

            switch (found.ValueKind)
            {
                case JsonValueKind.True:
                    value = true;
                    return true;
                case JsonValueKind.False:
                    value = false;
                    return true;
                case JsonValueKind.Number:
                    if (found.TryGetInt64(out var number))
                    {
                        value = number != 0;
                        return true;
                    }
                    break;
                case JsonValueKind.String:
                    var text = found.GetString();
                    if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1")
                    {
                        value = true;
                        return true;
                    }
                    if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0")
                    {
                        value = false;
                        return true;
                    }
                    break;
            }
        }

        value = false;
        return false;
    }

    private static string PickString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var found))
            {
                continue;
            }

            if (found.ValueKind == JsonValueKind.String)
            {
                var value = found.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
            else if (found.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                var value = found.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return string.Empty;
    }

    private static long PickLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var found))
            {
                continue;
            }

            switch (found.ValueKind)
            {
                case JsonValueKind.Number:
                    if (found.TryGetInt64(out var number))
                    {
                        return number;
                    }
                    break;
                case JsonValueKind.String:
                    if (long.TryParse(found.GetString(), out var parsed))
                    {
                        return parsed;
                    }
                    break;
                case JsonValueKind.True:
                    return 1;
                case JsonValueKind.False:
                    return 0;
            }
        }

        return 0;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static T GetExport<T>(nint libraryHandle, string name) where T : Delegate
    {
        return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(libraryHandle, name));
    }

    private static T? GetExportOptional<T>(nint libraryHandle, string name) where T : Delegate
    {
        try
        {
            return GetExport<T>(libraryHandle, name);
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitProtectionDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string resourcePath);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbInitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbOpenAccountDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        out nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbCloseAccountDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbSetMyWxidDelegate(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string wxid);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbGetSessionsDelegate(nint handle, out nint outJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbGetMessagesDelegate(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username,
        int limit,
        int offset,
        out nint outJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbGetDisplayNamesDelegate(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string usernamesJson,
        out nint outJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WcdbFreeStringDelegate(nint ptr);
}

internal sealed class ExportResult
{
    public bool Success { get; set; }
    public string Source { get; set; } = "weflow_wcdb";
    public string Error { get; set; } = string.Empty;
    public string DllPath { get; set; } = string.Empty;
    public string AccountDir { get; set; } = string.Empty;
    public string SessionDbPath { get; set; } = string.Empty;
    public string NormalizedWxid { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public int SessionCount { get; set; }
    public int ExportedMessageCount { get; set; }
    public string OutputJsonPath { get; set; } = string.Empty;
    public List<SessionSummary> Sessions { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> InitProtectionTriedPaths { get; set; } = new();
}

internal sealed class SessionSummary
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long SortTimestamp { get; set; }
}

internal sealed class ExportMessage
{
    public string Wxid { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long CreateTime { get; set; }
    public bool IsSender { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public int MsgType { get; set; }
    public int MsgSubType { get; set; }
    public string MediaType { get; set; } = string.Empty;
    public string MediaMime { get; set; } = string.Empty;
    public string MediaName { get; set; } = string.Empty;
    public string MediaData { get; set; } = string.Empty;

    [JsonIgnore]
    public long InternalLocalId { get; set; }

    [JsonIgnore]
    public string InternalServerId { get; set; } = string.Empty;

    [JsonIgnore]
    public string InternalSenderUsername { get; set; } = string.Empty;
}
