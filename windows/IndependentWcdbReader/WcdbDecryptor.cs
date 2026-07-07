internal sealed class WcdbDecryptor
{
    public AppResult Run(CliOptions options, long durationMs)
    {
        return new AppResult
        {
            Success = false,
            Mode = "decrypt",
            DurationMs = durationMs,
            Error = "尚未实现独立解密器。请先运行 --mode probe，确认微信 4.1.11 样本库的页结构和页大小。",
            OutputSqlitePath = string.IsNullOrWhiteSpace(options.OutputSqlitePath) ? null : options.OutputSqlitePath,
            Warnings =
            [
                "当前阶段只落地了 DbProbe。",
                "在确认页格式之前，不直接承诺纯 C# 可以稳定解密。",
            ],
        };
    }
}
