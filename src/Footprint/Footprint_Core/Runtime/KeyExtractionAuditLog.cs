using System.Text;
using Footprint.Core.Capture;
using Footprint.Core.Contracts;
using Footprint.Core.State;

namespace Footprint.Core.Runtime;

public sealed class KeyExtractionAuditLog
{
    private const int MaximumFieldLength = 2048;
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
    private readonly string _path;
    private readonly SourceEventOutbox? _outbox;
    private readonly object _gate = new();

    public KeyExtractionAuditLog(string path, SourceEventOutbox? outbox = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _outbox = outbox;
    }

    public string Path => _path;

    public void Write(string componentZh, string runId, string eventZh, string? resultZh = null)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToOffset(BeijingOffset)
                .ToString("yyyy-MM-dd HH:mm:ss.fff zzz", System.Globalization.CultureInfo.InvariantCulture);
            var line = $"[{timestamp}] 组件={Clean(componentZh)} | 运行={Clean(runId)} | 事件={Clean(eventZh)}";
            if (!string.IsNullOrWhiteSpace(resultZh)) line += $" | 结果={Clean(resultZh)}";
            line += Environment.NewLine;
            var bytes = new UTF8Encoding(false).GetBytes(line);
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes);
            }
        }
        catch (Exception) { }

        try { _outbox?.Enqueue(componentZh, runId, eventZh, resultZh ?? string.Empty); }
        catch (Exception) { }
    }

    public static string StageNameZh(FootprintStage stage) => stage switch
    {
        FootprintStage.Footprint_Runtime => "采集运行时",
        FootprintStage.Footprint_WeixinDetection => "微信定位",
        FootprintStage.Footprint_VersionVerification => "微信版本校验",
        FootprintStage.Footprint_KeyValidation => "缓存密钥校验",
        FootprintStage.Footprint_KeyCapture => "密钥捕获",
        FootprintStage.Footprint_WeixinRestart => "微信重启",
        FootprintStage.Footprint_ConnectionBinding => "密钥连接绑定",
        FootprintStage.Footprint_DatabaseSnapshot => "数据库快照",
        FootprintStage.Footprint_ImageSnapshot => "图片快照",
        FootprintStage.Footprint_VoiceSnapshot => "语音快照",
        FootprintStage.Footprint_FavoriteSnapshot => "收藏快照",
        FootprintStage.Footprint_Decompression => "数据库解压",
        _ => "未知阶段"
    };

    public static string StatusNameZh(CaptureStageStatus status) => status switch
    {
        CaptureStageStatus.Pending => "等待",
        CaptureStageStatus.Running => "运行中",
        CaptureStageStatus.Succeeded => "已成功",
        CaptureStageStatus.Skipped => "已跳过",
        CaptureStageStatus.Waiting => "等待操作",
        CaptureStageStatus.Cancelled => "已取消",
        CaptureStageStatus.Retrying => "正在重试",
        CaptureStageStatus.Failed => "失败",
        _ => "未知状态"
    };

    private static string Clean(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "无" : value.Trim();
        normalized = normalized.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
        return normalized.Length <= MaximumFieldLength ? normalized : normalized[..MaximumFieldLength];
    }
}
