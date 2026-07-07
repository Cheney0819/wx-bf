using System.Text;

internal sealed class DbProbe
{
    private static readonly int[] CommonPageSizes = [1024, 2048, 4096, 8192, 16384, 32768, 65536];

    public ProbeResult Run(CliOptions options)
    {
        var accountDir = ResolveDirectory(options.AccountDir);
        var sessionDbPath = ResolveSessionDbPath(accountDir, options.SessionDbPath);
        var messageCandidates = ResolveMessageDbCandidates(accountDir, options.MessageDbPath);
        var messageDbPath = messageCandidates.FirstOrDefault() ?? string.Empty;

        var sessionProbe = string.IsNullOrWhiteSpace(sessionDbPath) ? null : ProbeFile(sessionDbPath);
        var messageProbe = string.IsNullOrWhiteSpace(messageDbPath) ? null : ProbeFile(messageDbPath);

        return new ProbeResult
        {
            AccountDir = accountDir,
            Wxid = ResolveWxid(accountDir, options.Wxid),
            KeyProvided = !string.IsNullOrWhiteSpace(options.Key),
            KeyLength = string.IsNullOrWhiteSpace(options.Key) ? 0 : options.Key.Length,
            SessionDbPath = string.IsNullOrWhiteSpace(sessionDbPath) ? null : sessionDbPath,
            MessageDbPath = string.IsNullOrWhiteSpace(messageDbPath) ? null : messageDbPath,
            CandidateMessageDbPaths = messageCandidates,
            SessionDb = sessionProbe,
            MessageDb = messageProbe,
            Assessment = BuildAssessment(sessionProbe, messageProbe),
            NextStep = BuildNextStep(sessionProbe, messageProbe),
        };
    }

    private static string ResolveDirectory(string accountDir)
    {
        if (string.IsNullOrWhiteSpace(accountDir))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(accountDir);
        }
        catch
        {
            return accountDir;
        }
    }

    private static string ResolveSessionDbPath(string accountDir, string sessionDbPath)
    {
        if (!string.IsNullOrWhiteSpace(sessionDbPath))
        {
            return ResolveDirectory(sessionDbPath);
        }

        if (string.IsNullOrWhiteSpace(accountDir))
        {
            return string.Empty;
        }

        return Path.Combine(accountDir, "db_storage", "session", "session.db");
    }

    private static List<string> ResolveMessageDbCandidates(string accountDir, string messageDbPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(messageDbPath))
        {
            candidates.Add(ResolveDirectory(messageDbPath));
            return candidates;
        }

        if (string.IsNullOrWhiteSpace(accountDir))
        {
            return candidates;
        }

        var messageDir = Path.Combine(accountDir, "db_storage", "message");
        foreach (var name in new[] { "message_0.db", "biz_message_0.db" })
        {
            candidates.Add(Path.Combine(messageDir, name));
        }

        return candidates;
    }

    private static string ResolveWxid(string accountDir, string explicitWxid)
    {
        if (!string.IsNullOrWhiteSpace(explicitWxid))
        {
            return explicitWxid;
        }

        if (string.IsNullOrWhiteSpace(accountDir))
        {
            return string.Empty;
        }

        return Path.GetFileName(accountDir);
    }

    private static FileProbeResult ProbeFile(string path)
    {
        var fullPath = ResolveDirectory(path);
        if (!File.Exists(fullPath))
        {
            return new FileProbeResult
            {
                Path = fullPath,
                Exists = false,
            };
        }

        var fileInfo = new FileInfo(fullPath);
        using var stream = File.OpenRead(fullPath);
        var head256 = ReadBytes(stream, 256);
        var head64 = head256.Take(64).ToArray();
        var hasSqliteHeader = StartsWithSqliteHeader(head256);
        var pageSizeFromHeader = TryReadSqlitePageSize(head256, hasSqliteHeader);
        var commonCandidates = BuildPageSizeCandidates(fileInfo.Length, pageSizeFromHeader);
        var layoutProbes = BuildLayoutProbes(fullPath, fileInfo.Length, commonCandidates);

        return new FileProbeResult
        {
            Path = fullPath,
            Exists = true,
            SizeBytes = fileInfo.Length,
            HasSqliteHeader = hasSqliteHeader,
            PageSizeFromHeader = pageSizeFromHeader,
            CommonPageSizeCandidates = commonCandidates,
            HeaderHex64 = ToHex(head64),
            HeaderHex256 = ToHex(head256),
            HeaderAsciiPreview = ToAsciiPreview(head256),
            HeadEntropy = CalculateEntropy(head256),
            LayoutProbes = layoutProbes,
        };
    }

    private static byte[] ReadBytes(Stream stream, int count)
    {
        var buffer = new byte[count];
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, totalRead, count - totalRead);
            if (read <= 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead == buffer.Length ? buffer : buffer[..totalRead];
    }

    private static bool StartsWithSqliteHeader(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes("SQLite format 3\0");
        return bytes.Length >= header.Length && bytes.Take(header.Length).SequenceEqual(header);
    }

    private static int? TryReadSqlitePageSize(byte[] bytes, bool hasSqliteHeader)
    {
        if (!hasSqliteHeader || bytes.Length < 18)
        {
            return null;
        }

        var value = (bytes[16] << 8) | bytes[17];
        if (value == 1)
        {
            return 65536;
        }

        if (value is >= 512 and <= 65536 && (value & (value - 1)) == 0)
        {
            return value;
        }

        return null;
    }

    private static List<int> BuildPageSizeCandidates(long fileLength, int? pageSizeFromHeader)
    {
        var candidates = new List<int>();
        if (pageSizeFromHeader is not null)
        {
            candidates.Add(pageSizeFromHeader.Value);
        }

        foreach (var size in CommonPageSizes)
        {
            if (candidates.Contains(size))
            {
                continue;
            }

            if (fileLength >= size && fileLength / size >= 2)
            {
                candidates.Add(size);
            }
        }

        return candidates.Take(4).ToList();
    }

    private static List<PageLayoutProbe> BuildLayoutProbes(string path, long fileLength, List<int> candidates)
    {
        var probes = new List<PageLayoutProbe>();
        foreach (var pageSize in candidates)
        {
            var samples = new List<PageSample>();
            using var stream = File.OpenRead(path);
            var estimatedTotalPages = Math.Max(1, fileLength / pageSize);
            var sampleCount = (int)Math.Min(3, estimatedTotalPages);
            for (var pageIndex = 0; pageIndex < sampleCount; pageIndex++)
            {
                var offset = (long)pageIndex * pageSize;
                if (offset >= fileLength)
                {
                    break;
                }

                var window = ReadPageWindow(stream, offset, pageSize, fileLength);
                samples.Add(window);
            }

            probes.Add(
                new PageLayoutProbe
                {
                    PageSize = pageSize,
                    EstimatedTotalPages = estimatedTotalPages,
                    SampledPageCount = samples.Count,
                    PageSamples = samples,
                    RepeatingTailPrefix = HasRepeatingTail(samples),
                    AverageTailNonZeroBytes = samples.Count == 0 ? 0 : samples.Average(item => item.TailNonZeroBytes),
                }
            );
        }

        return probes;
    }

    private static PageSample ReadPageWindow(FileStream stream, long offset, int pageSize, long fileLength)
    {
        stream.Position = offset;
        var readSize = (int)Math.Min(pageSize, fileLength - offset);
        var pageBuffer = new byte[readSize];
        var totalRead = 0;
        while (totalRead < readSize)
        {
            var read = stream.Read(pageBuffer, totalRead, readSize - totalRead);
            if (read <= 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead < pageBuffer.Length)
        {
            pageBuffer = pageBuffer[..totalRead];
        }

        var head = pageBuffer.Take(16).ToArray();
        var tail = pageBuffer.Length <= 16 ? pageBuffer : pageBuffer[^16..];
        return new PageSample
        {
            PageIndex = (int)(offset / pageSize),
            Offset = offset,
            StartHex16 = ToHex(head),
            EndHex16 = ToHex(tail),
            Entropy = CalculateEntropy(pageBuffer.Take(Math.Min(256, pageBuffer.Length)).ToArray()),
            TailNonZeroBytes = tail.Count(item => item != 0),
        };
    }

    private static bool HasRepeatingTail(List<PageSample> samples)
    {
        if (samples.Count < 2)
        {
            return false;
        }

        var first = samples[0].EndHex16;
        return samples.Skip(1).All(item => string.Equals(item.EndHex16, first, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildAssessment(FileProbeResult? sessionProbe, FileProbeResult? messageProbe)
    {
        var sqliteVisible = new[] { sessionProbe, messageProbe }
            .Where(item => item is not null && item.Exists)
            .Any(item => item!.HasSqliteHeader);

        if (sqliteVisible)
        {
            return "至少有一个样本库直接暴露 SQLite 头，优先验证是否是明文库或仅首页部分明文。";
        }

        var encryptedLooking = new[] { sessionProbe, messageProbe }
            .Where(item => item is not null && item.Exists)
            .Any(item => item!.HeadEntropy >= 7.2);

        if (encryptedLooking)
        {
            return "样本头部高熵且未出现 SQLite 头，更像页级加密库，下一步应围绕页大小、页尾结构和密钥派生做验证。";
        }

        return "当前样本没有出现明确的 SQLite 头，但仍需结合真实页边界和更多样本确认格式。";
    }

    private static string BuildNextStep(FileProbeResult? sessionProbe, FileProbeResult? messageProbe)
    {
        var pageSize = sessionProbe?.PageSizeFromHeader
            ?? messageProbe?.PageSizeFromHeader
            ?? sessionProbe?.CommonPageSizeCandidates.FirstOrDefault()
            ?? messageProbe?.CommonPageSizeCandidates.FirstOrDefault();

        if (pageSize is not null && pageSize > 0)
        {
            return $"下一步建议围绕 {pageSize} 字节页做逐页样本分析，并尝试验证第一页是否可恢复 SQLite 头。";
        }

        return "下一步建议先固定一份真实 session.db 样本，比较 1024/2048/4096/8192 页边界上的页头页尾特征。";
    }

    private static string ToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ToAsciiPreview(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        foreach (var value in bytes)
        {
            builder.Append(value is >= 32 and <= 126 ? (char)value : '.');
        }

        return builder.ToString();
    }

    private static double CalculateEntropy(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return 0;
        }

        Span<int> counts = stackalloc int[256];
        foreach (var value in bytes)
        {
            counts[value]++;
        }

        double entropy = 0;
        foreach (var count in counts)
        {
            if (count == 0)
            {
                continue;
            }

            var probability = (double)count / bytes.Length;
            entropy -= probability * Math.Log2(probability);
        }

        return Math.Round(entropy, 4);
    }
}
