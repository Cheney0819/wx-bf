using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;

var cliOptions = CliOptions.Parse(args);
var bridge = new WxKeyBridge(cliOptions);
var result = bridge.Run();

Console.WriteLine(
    JsonSerializer.Serialize(
        result,
        JsonDefaults.Options
    )
);

return result.Success ? 0 : 1;

internal sealed record CliOptions(
    int ProcessId,
    int TimeoutMs,
    string DllPath
)
{
    public static CliOptions Parse(string[] args)
    {
        var processId = 0;
        var timeoutMs = 180_000;
        string? dllPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            if (string.Equals(current, "--pid", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                _ = int.TryParse(args[++index], out processId);
                continue;
            }

            if (string.Equals(current, "--timeout-ms", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (int.TryParse(args[++index], out var parsedTimeout) && parsedTimeout > 0)
                {
                    timeoutMs = parsedTimeout;
                }
                continue;
            }

            if (string.Equals(current, "--dll", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                dllPath = args[++index];
            }
        }

        return new CliOptions(
            processId,
            timeoutMs,
            string.IsNullOrWhiteSpace(dllPath) ? string.Empty : dllPath.Trim()
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

internal sealed class WxKeyBridge
{
    private const int StatusBufferSize = 2048;
    private const int PayloadBufferSize = 64 * 1024;
    private static readonly Regex DbKeyPattern = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);
    private static readonly Regex WxidPrefixPattern = new(@"^(wxid_[^_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WxidSuffixPattern = new(@"^(.+)_([a-zA-Z0-9]{4})$", RegexOptions.Compiled);

    private readonly CliOptions _options;

    public WxKeyBridge(CliOptions options)
    {
        _options = options;
    }

    public BridgeResult Run()
    {
        var startedAt = Stopwatch.StartNew();
        var statusMessages = new List<BridgeStatusMessage>();
        nint libraryHandle = 0;
        CleanupHookDelegate? cleanupHook = null;

        try
        {
            var process = ResolveProcess();
            if (process is null)
            {
                return Failure(
                    "未找到正在运行的 Weixin.exe 或 WeChat.exe",
                    startedAt.ElapsedMilliseconds,
                    statusMessages,
                    0,
                    string.Empty
                );
            }

            var dllPath = ResolveDllPath();
            if (!File.Exists(dllPath))
            {
                return Failure(
                    $"缺少 wx_key.dll: {dllPath}",
                    startedAt.ElapsedMilliseconds,
                    statusMessages,
                    process.Id,
                    process.ProcessName
                );
            }

            libraryHandle = NativeLibrary.Load(dllPath);
            var initializeHook = GetExport<InitializeHookDelegate>(libraryHandle, "InitializeHook");
            var pollKeyData = GetExport<PollKeyDataDelegate>(libraryHandle, "PollKeyData");
            var getImageKey = GetExport<GetImageKeyDelegate>(libraryHandle, "GetImageKey");
            var getStatusMessage = GetExport<GetStatusMessageDelegate>(libraryHandle, "GetStatusMessage");
            cleanupHook = GetExport<CleanupHookDelegate>(libraryHandle, "CleanupHook");
            var getLastErrorMsg = GetExport<GetLastErrorMsgDelegate>(libraryHandle, "GetLastErrorMsg");

            if (!initializeHook((uint)process.Id))
            {
                return Failure(
                    BuildNativeError("初始化 Hook 失败", getLastErrorMsg),
                    startedAt.ElapsedMilliseconds,
                    statusMessages,
                    process.Id,
                    process.ProcessName
                );
            }

            var payloadBuffer = new byte[PayloadBufferSize];
            var deadlineAt = DateTime.UtcNow.AddMilliseconds(_options.TimeoutMs);
            var loginRequiredDetected = false;
            string? dbKey = null;
            string? imageKey = null;
            string? rawPayload = null;
            List<BridgeAccount>? accounts = null;

            while (DateTime.UtcNow < deadlineAt)
            {
                if (process.HasExited)
                {
                    return Failure(
                        "微信进程已退出，未能获取到密钥",
                        startedAt.ElapsedMilliseconds,
                        statusMessages,
                        process.Id,
                        process.ProcessName,
                        loginRequiredDetected
                    );
                }

                DrainStatusMessages(getStatusMessage, statusMessages, ref loginRequiredDetected);

                Array.Clear(payloadBuffer, 0, payloadBuffer.Length);
                if (pollKeyData(payloadBuffer, payloadBuffer.Length))
                {
                    rawPayload = DecodeUtf8Z(payloadBuffer);
                    if (!string.IsNullOrWhiteSpace(rawPayload))
                    {
                        dbKey = ParseDbKey(rawPayload);
                        if (!string.IsNullOrWhiteSpace(dbKey))
                        {
                            break;
                        }

                        accounts = ParseAccounts(rawPayload);
                        if (accounts.Count > 0)
                        {
                            dbKey = accounts
                                .Select(item => FirstNonEmpty(item.DecryptKey, item.AesKey))
                                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
                            if (!string.IsNullOrWhiteSpace(dbKey))
                            {
                                break;
                            }
                        }
                    }
                }

                Thread.Sleep(120);
            }

            DrainStatusMessages(getStatusMessage, statusMessages, ref loginRequiredDetected);

            if (string.IsNullOrWhiteSpace(dbKey))
            {
                return Failure(
                    BuildTimeoutError(statusMessages, rawPayload, getLastErrorMsg),
                    startedAt.ElapsedMilliseconds,
                    statusMessages,
                    process.Id,
                    process.ProcessName,
                    loginRequiredDetected,
                    rawPayload
                );
            }

            imageKey = TryReadImageKey(getImageKey);

            return new BridgeResult
            {
                Success = true,
                Source = "weflow_wx_key",
                ProcessId = process.Id,
                ProcessName = $"{process.ProcessName}.exe",
                DllPath = dllPath,
                DurationMs = startedAt.ElapsedMilliseconds,
                LoginRequiredDetected = loginRequiredDetected,
                DbKey = dbKey,
                ImageKey = imageKey,
                StatusMessages = statusMessages,
                Accounts = accounts ?? [],
                AccountCount = accounts?.Count ?? 0,
                RawPayloadPreview = rawPayload is null
                    ? null
                    : rawPayload.Length <= 500
                        ? rawPayload
                        : rawPayload[..500],
            };
        }
        catch (Exception ex)
        {
            return Failure(
                ex.Message,
                startedAt.ElapsedMilliseconds,
                statusMessages,
                0,
                string.Empty
            );
        }
        finally
        {
            try
            {
                cleanupHook?.Invoke();
            }
            catch
            {
            }

            if (libraryHandle != 0)
            {
                NativeLibrary.Free(libraryHandle);
            }
        }
    }

    private Process? ResolveProcess()
    {
        if (_options.ProcessId > 0)
        {
            try
            {
                var process = Process.GetProcessById(_options.ProcessId);
                return process.HasExited ? null : process;
            }
            catch
            {
                return null;
            }
        }

        foreach (var processName in new[] { "Weixin", "WeChat" })
        {
            var match = Process
                .GetProcessesByName(processName)
                .FirstOrDefault(item => !item.HasExited);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private string ResolveDllPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.DllPath))
        {
            return Path.GetFullPath(_options.DllPath);
        }

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "wx_key.dll"),
            Path.Combine(baseDir, "key", "win32", "x64", "wx_key.dll"),
            Path.Combine(baseDir, "resources", "key", "win32", "x64", "wx_key.dll"),
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string DecodeUtf8Z(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        if (end < 0)
        {
            end = buffer.Length;
        }

        return Encoding.UTF8.GetString(buffer, 0, end).Trim();
    }

    private static List<BridgeAccount> ParseAccounts(string payload)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<KeyPayload>(payload, JsonDefaults.Options);
            if (parsed?.Accounts is null || parsed.Accounts.Count == 0)
            {
                return [];
            }

            var results = new List<BridgeAccount>();
            foreach (var account in parsed.Accounts)
            {
                var originalWxid = (account.Wxid ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(originalWxid))
                {
                    continue;
                }

                var normalizedWxid = NormalizeWxid(originalWxid);
                foreach (var key in account.Keys ?? [])
                {
                    var aesKey = FirstNonEmpty(key.AesKey, key.DbKey, key.DecryptKey);
                    if (string.IsNullOrWhiteSpace(aesKey))
                    {
                        continue;
                    }

                    results.Add(
                        new BridgeAccount
                        {
                            Wxid = originalWxid,
                            NormalizedWxid = normalizedWxid,
                            AesKey = aesKey,
                            XorKey = FirstNonEmpty(key.XorKey, key.ImageXorKey),
                            DecryptKey = aesKey,
                        }
                    );
                    break;
                }
            }

            return results
                .GroupBy(item => item.NormalizedWxid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string ParseDbKey(string payload)
    {
        var trimmed = (payload ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (DbKeyPattern.IsMatch(trimmed))
        {
            return trimmed;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var keyName in new[] { "dbKey", "decryptKey", "aesKey", "key" })
                {
                    if (doc.RootElement.TryGetProperty(keyName, out var valueElement))
                    {
                        var candidate = (valueElement.GetString() ?? string.Empty).Trim();
                        if (DbKeyPattern.IsMatch(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static void DrainStatusMessages(
        GetStatusMessageDelegate getStatusMessage,
        List<BridgeStatusMessage> statusMessages,
        ref bool loginRequiredDetected
    )
    {
        var buffer = new byte[StatusBufferSize];
        while (true)
        {
            Array.Clear(buffer, 0, buffer.Length);
            if (!getStatusMessage(buffer, buffer.Length, out var level))
            {
                break;
            }

            var message = DecodeUtf8Z(buffer);
            if (string.IsNullOrWhiteSpace(message))
            {
                break;
            }

            if (statusMessages.Count < 50)
            {
                statusMessages.Add(new BridgeStatusMessage { Message = message, Level = level });
            }

            if (LooksLikeLoginRequired(message))
            {
                loginRequiredDetected = true;
            }
        }
    }

    private static bool LooksLikeLoginRequired(string message)
    {
        return message.Contains("登录", StringComparison.OrdinalIgnoreCase)
            || message.Contains("扫码", StringComparison.OrdinalIgnoreCase)
            || message.Contains("重新连接", StringComparison.OrdinalIgnoreCase)
            || message.Contains("未找到账号", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadImageKey(GetImageKeyDelegate getImageKey)
    {
        try
        {
            var buffer = new byte[StatusBufferSize];
            return getImageKey(buffer, buffer.Length) ? DecodeUtf8Z(buffer) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeWxid(string input)
    {
        var trimmed = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var prefixMatch = WxidPrefixPattern.Match(trimmed);
        if (prefixMatch.Success)
        {
            return prefixMatch.Groups[1].Value;
        }

        var suffixMatch = WxidSuffixPattern.Match(trimmed);
        return suffixMatch.Success ? suffixMatch.Groups[1].Value : trimmed;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string BuildNativeError(string prefix, GetLastErrorMsgDelegate getLastErrorMsg)
    {
        try
        {
            var pointer = getLastErrorMsg();
            var detail = pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(pointer) ?? string.Empty;
            return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}: {detail}";
        }
        catch
        {
            return prefix;
        }
    }

    private static string BuildTimeoutError(
        IReadOnlyList<BridgeStatusMessage> statusMessages,
        string? rawPayload,
        GetLastErrorMsgDelegate getLastErrorMsg
    )
    {
        var lastStatus = statusMessages.LastOrDefault()?.Message ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(lastStatus))
        {
            return $"获取密钥超时: {lastStatus}";
        }

        if (!string.IsNullOrWhiteSpace(rawPayload))
        {
            return "获取密钥超时: 已收到原始数据，但未解析出可用数据库密钥";
        }

        return BuildNativeError("获取密钥超时", getLastErrorMsg);
    }

    private static BridgeResult Failure(
        string error,
        long durationMs,
        List<BridgeStatusMessage> statusMessages,
        int processId,
        string processName,
        bool loginRequiredDetected = false,
        string? rawPayload = null
    )
    {
        return new BridgeResult
        {
            Success = false,
            Source = "weflow_wx_key",
            Error = error,
            DurationMs = durationMs,
            ProcessId = processId,
            ProcessName = string.IsNullOrWhiteSpace(processName) ? null : $"{processName}.exe",
            StatusMessages = statusMessages,
            LoginRequiredDetected = loginRequiredDetected,
            RawPayloadPreview = string.IsNullOrWhiteSpace(rawPayload)
                ? null
                : rawPayload.Length <= 500
                    ? rawPayload
                    : rawPayload[..500],
        };
    }

    private static TDelegate GetExport<TDelegate>(nint libraryHandle, string exportName)
        where TDelegate : Delegate
    {
        var export = NativeLibrary.GetExport(libraryHandle, exportName);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(export);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool InitializeHookDelegate(uint targetPid);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool PollKeyDataDelegate(byte[] keyBuffer, int bufferSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool GetImageKeyDelegate(byte[] resultBuffer, int bufferSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool GetStatusMessageDelegate(byte[] msgBuffer, int bufferSize, out int outLevel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool CleanupHookDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetLastErrorMsgDelegate();
}

internal sealed class KeyPayload
{
    [JsonPropertyName("accounts")]
    public List<KeyAccountPayload>? Accounts { get; set; }
}

internal sealed class KeyAccountPayload
{
    [JsonPropertyName("wxid")]
    public string? Wxid { get; set; }

    [JsonPropertyName("keys")]
    public List<KeyValuePayload>? Keys { get; set; }
}

internal sealed class KeyValuePayload
{
    [JsonPropertyName("aesKey")]
    public string? AesKey { get; set; }

    [JsonPropertyName("xorKey")]
    public string? XorKey { get; set; }

    [JsonPropertyName("dbKey")]
    public string? DbKey { get; set; }

    [JsonPropertyName("decryptKey")]
    public string? DecryptKey { get; set; }

    [JsonPropertyName("imageXorKey")]
    public string? ImageXorKey { get; set; }
}

internal sealed class BridgeResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "weflow_wx_key";

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("processName")]
    public string? ProcessName { get; set; }

    [JsonPropertyName("dllPath")]
    public string? DllPath { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("loginRequiredDetected")]
    public bool LoginRequiredDetected { get; set; }

    [JsonPropertyName("dbKey")]
    public string? DbKey { get; set; }

    [JsonPropertyName("imageKey")]
    public string? ImageKey { get; set; }

    [JsonPropertyName("accountCount")]
    public int AccountCount { get; set; }

    [JsonPropertyName("accounts")]
    public List<BridgeAccount> Accounts { get; set; } = [];

    [JsonPropertyName("statusMessages")]
    public List<BridgeStatusMessage> StatusMessages { get; set; } = [];

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("rawPayloadPreview")]
    public string? RawPayloadPreview { get; set; }
}

internal sealed class BridgeAccount
{
    [JsonPropertyName("wxid")]
    public string Wxid { get; set; } = string.Empty;

    [JsonPropertyName("normalizedWxid")]
    public string NormalizedWxid { get; set; } = string.Empty;

    [JsonPropertyName("aesKey")]
    public string AesKey { get; set; } = string.Empty;

    [JsonPropertyName("xorKey")]
    public string XorKey { get; set; } = string.Empty;

    [JsonPropertyName("decryptKey")]
    public string DecryptKey { get; set; } = string.Empty;
}

internal sealed class BridgeStatusMessage
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public int Level { get; set; }
}
