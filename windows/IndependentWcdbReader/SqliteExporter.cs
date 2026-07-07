internal sealed class SqliteExporter
{
    public AppResult Run(CliOptions options, long durationMs)
    {
        return new AppResult
        {
            Success = false,
            Mode = "export",
            DurationMs = durationMs,
            Error = "尚未实现 SQLite 导出器。需要先完成 decrypt 模式，生成可读的临时 SQLite。",
            OutputJsonPath = string.IsNullOrWhiteSpace(options.OutputJsonPath) ? null : options.OutputJsonPath,
            Warnings =
            [
                "当前阶段优先验证数据库格式，不直接导出 chatlog_export.json。",
            ],
        };
    }
}
