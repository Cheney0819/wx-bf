using System.Diagnostics;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = Encoding.UTF8;

var startedAt = Stopwatch.StartNew();
var cliOptions = CliOptions.Parse(args);
AppResult result;

try
{
    result = cliOptions.Mode.ToLowerInvariant() switch
    {
        "probe" => RunProbe(cliOptions, startedAt),
        "decrypt" => new WcdbDecryptor().Run(cliOptions, startedAt.ElapsedMilliseconds),
        "export" => new SqliteExporter().Run(cliOptions, startedAt.ElapsedMilliseconds),
        _ => new AppResult
        {
            Success = false,
            Mode = cliOptions.Mode,
            DurationMs = startedAt.ElapsedMilliseconds,
            Error = $"不支持的模式: {cliOptions.Mode}",
            Warnings = ["当前仅支持 --mode probe、--mode decrypt、--mode export。"],
        }
    };
}
catch (Exception ex)
{
    result = new AppResult
    {
        Success = false,
        Mode = cliOptions.Mode,
        DurationMs = startedAt.ElapsedMilliseconds,
        Error = ex.Message,
    };
}

PersistJsonIfNeeded(cliOptions.OutputJsonPath, result);
Console.WriteLine(JsonSerializer.Serialize(result, JsonDefaults.Options));
return result.Success ? 0 : 1;

static AppResult RunProbe(CliOptions options, Stopwatch startedAt)
{
    var probe = new DbProbe().Run(options);
    return new AppResult
    {
        Success = true,
        Mode = "probe",
        DurationMs = startedAt.ElapsedMilliseconds,
        Probe = probe,
        OutputJsonPath = string.IsNullOrWhiteSpace(options.OutputJsonPath) ? null : options.OutputJsonPath,
        Warnings = BuildProbeWarnings(probe),
    };
}

static void PersistJsonIfNeeded(string outputJsonPath, AppResult result)
{
    if (string.IsNullOrWhiteSpace(outputJsonPath))
    {
        return;
    }

    var fullPath = Path.GetFullPath(outputJsonPath);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    File.WriteAllText(fullPath, JsonSerializer.Serialize(result, JsonDefaults.Options), Encoding.UTF8);
}

static List<string> BuildProbeWarnings(ProbeResult probe)
{
    var warnings = new List<string>();
    if (!probe.KeyProvided)
    {
        warnings.Add("本次 probe 没有提供 dbKey，只能做结构探测，不能直接验证解密流程。");
    }

    if (probe.SessionDb is not null && !probe.SessionDb.Exists)
    {
        warnings.Add("session.db 不存在，请确认 accountDir 是否指向真实 wxid 目录。");
    }

    if (probe.MessageDb is not null && !probe.MessageDb.Exists)
    {
        warnings.Add("首选消息库不存在，请检查 message_0.db 或 biz_message_0.db。");
    }

    return warnings;
}
