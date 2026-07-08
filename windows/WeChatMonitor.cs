/*
 * WeChatMonitor.cs - 嵌入到你的 WPF/WinForms 项目中
 * 桌宠启动时自动运行，自动解密微信数据库、提取聊天记录并推送到服务器
 */
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

public class WeChatMonitor
{
    // ============ 配置区 ============
    private const string DEFAULT_SERVER_URL = "https://wx.junjiee.online/api/messages";
    private const string DEFAULT_SERVER_TOKEN = "wx_monitor_2026";
    private const int DEFAULT_PUSH_INTERVAL_SECONDS = 60;
    private const string DECRYPT_EXE_NAME = "wx_decrypt.exe";
    private const int EXISTING_PROCESS_QUICK_TRY_SECONDS = 30;
    private const int DECRYPT_SOFT_TIMEOUT_SECONDS = 180;
    private const int DECRYPT_HARD_TIMEOUT_SECONDS = 600;
    private const int DECRYPT_PROGRESS_EVENT_INTERVAL_SECONDS = 30;
    private const int WECHAT_RESTART_WAIT_TIMEOUT_SECONDS = 180;
    private const int WECHAT_NEW_PROCESS_POLL_INTERVAL_MS = 250;
    private const int WECHAT_EXIT_WAIT_TIMEOUT_SECONDS = 10;
    private const int MAX_IMAGE_BYTES = 5 * 1024 * 1024;
    private const int HTTP_RETRY_MAX_ATTEMPTS = 3;
    private const int HTTP_RETRY_BASE_DELAY_MS = 1500;
    private const string CHATLOG_EXPORT_FILE_NAME = "chatlog_export.json";
    private const string CONTACT_EXPORT_FILE_NAME = "contact_export.json";
    private const string FAVORITE_EXPORT_FILE_NAME = "favorite_export.json";
    private const string CLIENT_IDENTITY_FILE_NAME = "client_identity.json";
    private const string SYNC_STATE_FILE_NAME = "sync_state.json";
    private const int MAX_MESSAGE_BATCH = 5000;
    private const int MAX_SYNCED_MESSAGE_KEYS = 8000;
    private const string RuntimeEventPrefix = "__WX_EVENT__=";
    private const string DbKeyCachePrefix = "__WX_DB_KEY_CACHE__=";
    private const string LocalConsoleLogEnvName = "WEFLOW_LOCAL_CONSOLE_LOG";
    // ================================

    private static readonly HttpClient _http = new HttpClient();
    private static readonly MonitorRuntimeConfig _config = LoadConfig();
    private static readonly string _eventsUrl = _config.ServerUrl.Replace("/api/messages", "/api/events");
    private static readonly string _statusUrl = _config.ServerUrl.Replace("/api/messages", "/api/status");
    private static readonly string _contactsUrl = _config.ServerUrl.Replace("/api/messages", "/api/contacts");
    private static readonly string _favoritesUrl = _config.ServerUrl.Replace("/api/messages", "/api/favorites");
    private const string ClientSource = "client_cs";
    private static readonly string _sessionId = LoadOrCreateClientIdentity().session_id;
    private static readonly SemaphoreSlim _runLock = new SemaphoreSlim(1, 1);
    private static readonly object _syncStateLock = new();
    private static readonly object _dbKeyCacheLock = new();
    private static readonly object _eventSuppressionLock = new();
    private static readonly bool _enableLocalConsoleLog = IsLocalConsoleLogEnabled();
    private static long _requestSequence = 0;

    private static DispatcherTimer _timer;
    private static bool? _lastKnownLoginState;
    private static readonly string[] _wechatProcessNames = { "Weixin", "WeChat" };
    private static string? _syncStateScopeKey;
    private static LocalSyncState _syncState = new();
    private static string? _dbKeyCachePayloadJson;
    private static string? _activeIssueSignature;
    private static bool _currentScanIssueReported;

    /// <summary>
    /// 在 App.xaml.cs 或 MainWindow 构造函数中调用
    /// </summary>
    public static void Start()
    {
        Log("启动监控...");

        _ = CheckAndPushAsync();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_config.PushIntervalSeconds)
        };
        _timer.Tick += async (_, _) => await CheckAndPushAsync();
        _timer.Start();
    }

    public static void Stop()
    {
        _timer?.Stop();
        Log("已停止");
    }

    private static async Task CheckAndPushAsync()
    {
        if (!await _runLock.WaitAsync(0))
        {
            Log("上一轮任务还未结束，跳过本轮");
            return;
        }

        BeginScanCycle();
        var sw = Stopwatch.StartNew();
        var currentWeChatProcess = GetPreferredWeChatProcess();
        bool processRunningAtStart = currentWeChatProcess is not null;
        bool? currentLoginState = processRunningAtStart;
        string decryptDir = GetDecryptDir();
        Dictionary<string, object?>? finalScanFinishedPayload = null;
        try
        {
            await PostEventAsync("client_scan_started", new { interval_seconds = _config.PushIntervalSeconds });
            Log("检查新消息...");

            var decryptResult = await RunDecryptFlowAsync(currentWeChatProcess);
            currentLoginState = decryptResult.IsLoggedIn;
            _lastKnownLoginState = decryptResult.IsLoggedIn;
            await PostStatusAsync(
                wechatLoggedIn: decryptResult.IsLoggedIn,
                decryptOk: decryptResult.Success,
                error: decryptResult.Success ? null : decryptResult.ErrorMessage
            );
            await PostEventAsync("client_wechat_login_status", new { logged_in = decryptResult.IsLoggedIn, mode = "wx_decrypt" });

            if (decryptResult.HandledInProcess)
            {
                finalScanFinishedPayload = new Dictionary<string, object?>
                {
                    ["duration_ms"] = sw.ElapsedMilliseconds,
                    ["result"] = decryptResult.ProcessResult,
                    ["mode"] = "memory_stream_v4",
                    ["message_count"] = decryptResult.MessageCount,
                    ["added_count"] = decryptResult.AddedCount,
                    ["has_new_messages"] = decryptResult.AddedCount > 0,
                };
                return;
            }

            if (!decryptResult.Success)
            {
                finalScanFinishedPayload = new Dictionary<string, object?>
                {
                    ["duration_ms"] = sw.ElapsedMilliseconds,
                    ["result"] = decryptResult.IsLoggedIn ? "decrypt_failed" : "wechat_not_logged_in",
                    ["message_count"] = 0,
                    ["mode"] = "ephemeral_disk_v4",
                };
                return;
            }

            await PushSupplementalDataAsync(decryptResult.DecryptDir);

            List<WeChatMessage>? messages = ExtractMessages(decryptResult.DecryptDir, decryptResult.WeChatDbDir);
            EnsureSyncStateLoaded(GetSyncStateScopeKey(decryptResult.DecryptDir));

            if (messages == null || messages.Count == 0)
            {
                Log("没有找到消息");
                finalScanFinishedPayload = new Dictionary<string, object?>
                {
                    ["duration_ms"] = sw.ElapsedMilliseconds,
                    ["result"] = "no_messages",
                    ["mode"] = "ephemeral_disk_v4",
                    ["message_count"] = 0,
                };
                return;
            }

            List<WeChatMessage> incrementalMessages = FilterIncrementalMessages(messages, out int skippedCount);
            await PostEventAsync("client_incremental_filter_result", new
            {
                extracted_count = messages.Count,
                new_count = incrementalMessages.Count,
                skipped_count = skippedCount,
                mode = "ephemeral_disk_v4"
            });

            if (incrementalMessages.Count == 0)
            {
                Log("消息已同步，无需重复上传");
                finalScanFinishedPayload = new Dictionary<string, object?>
                {
                    ["duration_ms"] = sw.ElapsedMilliseconds,
                    ["result"] = "no_new_messages",
                    ["mode"] = "ephemeral_disk_v4",
                    ["message_count"] = messages.Count,
                    ["added_count"] = 0,
                    ["has_new_messages"] = false,
                };
                return;
            }

            Log($"发现新消息，推送 {incrementalMessages.Count} 条...");
            var pushResult = await PushToServerAsync(incrementalMessages);
            if (pushResult.Success)
            {
                MarkMessagesSynced(incrementalMessages);
            }

            finalScanFinishedPayload = new Dictionary<string, object?>
            {
                ["duration_ms"] = sw.ElapsedMilliseconds,
                ["result"] = pushResult.Success ? "pushed" : "push_failed",
                ["mode"] = "ephemeral_disk_v4",
                ["message_count"] = incrementalMessages.Count,
                ["added_count"] = pushResult.AddedCount,
                ["has_new_messages"] = pushResult.Success && pushResult.AddedCount > 0,
            };
        }
        catch (Exception ex)
        {
            Log($"错误: {ex.Message}");
            bool loginState = currentLoginState ?? _lastKnownLoginState ?? false;
            await PostStatusAsync(wechatLoggedIn: loginState, decryptOk: false, error: ex.Message);
            await PostEventAsync("client_extract_failed", new
            {
                stage = "check_and_push",
                error_message = ex.Message,
                logged_in = loginState
            });
        }
        finally
        {
            var cleanupResult = await CleanupDecryptArtifactsAsync(decryptDir);
            if (finalScanFinishedPayload is not null)
            {
                finalScanFinishedPayload["cleanup_attempted"] = true;
                finalScanFinishedPayload["cleanup_success"] = cleanupResult.Success;
                finalScanFinishedPayload["cleanup_removed_count"] = cleanupResult.RemovedCount;
                finalScanFinishedPayload["cleanup_failed_count"] = cleanupResult.FailedCount;
                finalScanFinishedPayload["cleanup_failed_paths"] = cleanupResult.FailedPaths;
                await PostEventAsync("client_scan_finished", finalScanFinishedPayload);
            }
            EndScanCycle();
            _runLock.Release();
        }
    }

    private static async Task<DecryptRunResult> RunDecryptFlowAsync(WeChatProcessSnapshot? currentWeChatProcess)
    {
        if (currentWeChatProcess is null)
        {
            return await RunDecryptAsync(new DecryptAttemptOptions("normal_scan", DECRYPT_SOFT_TIMEOUT_SECONDS, DECRYPT_HARD_TIMEOUT_SECONDS));
        }

        await PostEventAsync("client_wechat_detected", new
        {
            pid = currentWeChatProcess.ProcessId,
            process_name = currentWeChatProcess.ProcessName,
            executable_path = currentWeChatProcess.ExecutablePath,
            wechat_version = currentWeChatProcess.ExecutableVersion
        });

        var quickTryResult = await RunDecryptAsync(
            new DecryptAttemptOptions(
                "existing_process_quick_try",
                EXISTING_PROCESS_QUICK_TRY_SECONDS,
                EXISTING_PROCESS_QUICK_TRY_SECONDS
            )
        );
        if (quickTryResult.Success || !ShouldRestartWeChatForFreshHook(quickTryResult))
        {
            return quickTryResult;
        }

        await PostEventAsync("client_wechat_restart_started", new
        {
            reason = "existing_process_retry_required",
            previous_pid = currentWeChatProcess.ProcessId,
            executable_path = currentWeChatProcess.ExecutablePath,
            quick_try_seconds = EXISTING_PROCESS_QUICK_TRY_SECONDS,
            trigger_error = quickTryResult.ErrorMessage
        });

        var restartResult = await RestartWeChatAndWaitForNewProcessAsync(currentWeChatProcess);
        if (!restartResult.Success || restartResult.Process is null)
        {
            return new DecryptRunResult(
                false,
                false,
                string.Empty,
                string.Empty,
                string.IsNullOrWhiteSpace(restartResult.ErrorMessage) ? "自动重启微信失败" : restartResult.ErrorMessage
            );
        }

        await PostEventAsync("client_wechat_process_detected", new
        {
            pid = restartResult.Process.ProcessId,
            process_name = restartResult.Process.ProcessName,
            executable_path = restartResult.Process.ExecutablePath,
            wechat_version = restartResult.Process.ExecutableVersion,
            wait_elapsed_ms = restartResult.WaitElapsedMs
        });

        return await RunDecryptAsync(new DecryptAttemptOptions("after_wechat_restart", DECRYPT_SOFT_TIMEOUT_SECONDS, DECRYPT_HARD_TIMEOUT_SECONDS));
    }

    private static async Task<DecryptRunResult> RunDecryptAsync(DecryptAttemptOptions attemptOptions)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string decryptDir = GetDecryptDir();
        string decryptExe = Path.Combine(baseDir, DECRYPT_EXE_NAME);
        bool processRunning = DetectRunningWeChatProcess();

        Directory.CreateDirectory(decryptDir);
        CleanupDecryptArtifacts(decryptDir);

        if (!File.Exists(decryptExe))
        {
            Log($"未找到 wx_decrypt.exe: {decryptExe}");
            await PostEventAsync("client_extract_failed", new
            {
                stage = "decrypt_bootstrap",
                reason = "decrypt_exe_missing",
                exe_path = decryptExe
            });
            return new DecryptRunResult(false, processRunning, decryptDir, "", "未找到 wx_decrypt.exe");
        }

        var psi = new ProcessStartInfo
        {
            FileName = decryptExe,
            Arguments = $"\"{decryptDir}\"",
            WorkingDirectory = baseDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.Environment["WECHAT_MONITOR_SERVER_URL"] = _config.ServerUrl;
        psi.Environment["WECHAT_MONITOR_SERVER_TOKEN"] = _config.ServerToken;
        psi.Environment["WECHAT_MONITOR_SESSION_ID"] = _sessionId;
        psi.Environment["WECHAT_MONITOR_CLIENT_SOURCE"] = "client_py";
        string? dbKeyCachePayload = GetDbKeyCachePayload();
        if (!string.IsNullOrWhiteSpace(dbKeyCachePayload))
            psi.Environment["WECHAT_MONITOR_DB_KEY_CACHE_JSON"] = dbKeyCachePayload;
        psi.Environment["PYTHONIOENCODING"] = "utf-8:replace";
        psi.Environment["PYTHONUTF8"] = "1";

        using var process = new Process { StartInfo = psi };
        process.Start();

        await PostEventAsync("client_decrypt_started", new
        {
            pid = process.Id,
            soft_timeout_seconds = attemptOptions.SoftTimeoutSeconds,
            hard_timeout_seconds = attemptOptions.HardTimeoutSeconds,
            decrypt_dir = decryptDir,
            attempt_kind = attemptOptions.AttemptKind
        });

        var outputState = new DecryptProcessOutputState();
        var stdoutTask = PumpProcessStreamAsync(process.StandardOutput, outputState);
        var stderrTask = PumpProcessStreamAsync(process.StandardError, outputState);
        var decryptStopwatch = Stopwatch.StartNew();
        bool isExistingProcessQuickTry = string.Equals(
            attemptOptions.AttemptKind,
            "existing_process_quick_try",
            StringComparison.Ordinal
        );
        int activeSoftTimeoutSeconds = attemptOptions.SoftTimeoutSeconds;
        int activeHardTimeoutSeconds = attemptOptions.HardTimeoutSeconds;
        var nextProgressEventAt = TimeSpan.FromSeconds(DECRYPT_PROGRESS_EVENT_INTERVAL_SECONDS);
        bool slowWarningSent = false;
        bool exited = false;
        bool timeoutExtendedAfterKeyReady = false;

        while (decryptStopwatch.Elapsed < TimeSpan.FromSeconds(activeHardTimeoutSeconds))
        {
            if (process.HasExited)
            {
                exited = true;
                break;
            }

            if (
                isExistingProcessQuickTry
                && !timeoutExtendedAfterKeyReady
                && outputState.HasConfirmedKeyMaterial
            )
            {
                activeSoftTimeoutSeconds = DECRYPT_SOFT_TIMEOUT_SECONDS;
                activeHardTimeoutSeconds = DECRYPT_HARD_TIMEOUT_SECONDS;
                timeoutExtendedAfterKeyReady = true;
                await PostEventAsync("client_decrypt_progress", new
                {
                    pid = process.Id,
                    elapsed_seconds = (int)Math.Floor(decryptStopwatch.Elapsed.TotalSeconds),
                    soft_timeout_seconds = activeSoftTimeoutSeconds,
                    hard_timeout_seconds = activeHardTimeoutSeconds,
                    decrypt_dir = decryptDir,
                    attempt_kind = attemptOptions.AttemptKind,
                    stage = "timeout_extended_after_key_ready",
                    original_soft_timeout_seconds = attemptOptions.SoftTimeoutSeconds,
                    original_hard_timeout_seconds = attemptOptions.HardTimeoutSeconds
                });
            }

            if (!slowWarningSent && decryptStopwatch.Elapsed >= TimeSpan.FromSeconds(activeSoftTimeoutSeconds))
            {
                int elapsedSeconds = (int)Math.Floor(decryptStopwatch.Elapsed.TotalSeconds);
                await PostHeartbeatAsync(wechatLoggedIn: processRunning);
                await PostEventAsync("client_decrypt_slow", new
                {
                    pid = process.Id,
                    elapsed_seconds = elapsedSeconds,
                    soft_timeout_seconds = activeSoftTimeoutSeconds,
                    hard_timeout_seconds = activeHardTimeoutSeconds,
                    decrypt_dir = decryptDir,
                    attempt_kind = attemptOptions.AttemptKind
                });
                slowWarningSent = true;
            }

            if (decryptStopwatch.Elapsed >= nextProgressEventAt)
            {
                int elapsedSeconds = (int)Math.Floor(decryptStopwatch.Elapsed.TotalSeconds);
                await PostHeartbeatAsync(wechatLoggedIn: processRunning);
                await PostEventAsync("client_decrypt_progress", new
                {
                    pid = process.Id,
                    elapsed_seconds = elapsedSeconds,
                    soft_timeout_seconds = activeSoftTimeoutSeconds,
                    hard_timeout_seconds = activeHardTimeoutSeconds,
                    decrypt_dir = decryptDir,
                    attempt_kind = attemptOptions.AttemptKind
                });
                nextProgressEventAt += TimeSpan.FromSeconds(DECRYPT_PROGRESS_EVENT_INTERVAL_SECONDS);
            }

            await Task.Delay(1000);
        }

        if (!exited && process.HasExited)
        {
            exited = true;
        }

        if (!exited)
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
            }

            await PostEventAsync("client_extract_failed", new
            {
                stage = "decrypt_process",
                reason = "decrypt_hard_timeout",
                soft_timeout_seconds = activeSoftTimeoutSeconds,
                hard_timeout_seconds = activeHardTimeoutSeconds,
                attempt_kind = attemptOptions.AttemptKind,
                key_ready = outputState.HasConfirmedKeyMaterial
            });
            Log("解密超过硬超时，已终止");
            return new DecryptRunResult(
                false,
                processRunning,
                decryptDir,
                "",
                $"解密超过硬超时（{activeHardTimeoutSeconds} 秒），已终止"
            );
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        string rawOutput = outputState.BuildOutput();
        bool isLoggedIn = outputState.HasLoginStatus ? outputState.IsLoggedIn : ParseLoginStatus(rawOutput);
        if (!isLoggedIn && processRunning)
        {
            isLoggedIn = true;
            Log("解密器没有明确返回登录状态，但已检测到微信进程在运行，按已运行处理");
        }
        string output = SanitizeDecryptOutput(rawOutput);
        string processErrorMessage = outputState.ProcessErrorMessage?.Trim() ?? "";

        if (process.ExitCode == 0)
        {
            Log("解密完成");
            await PostEventAsync("client_decrypt_finished", new
            {
                decrypt_dir = decryptDir,
                output = output.Length > 300 ? output[..300] : output,
                attempt_kind = attemptOptions.AttemptKind
            });
            return new DecryptRunResult(
                true,
                true,
                decryptDir,
                ReadWeChatDbDir(decryptDir),
                "",
                outputState.HandledInProcess,
                outputState.ProcessResult,
                outputState.MessageCount,
                outputState.AddedCount
            );
        }

        if (outputState.HandledInProcess)
        {
            Log($"解密流程已在进程内给出结果: {outputState.ProcessResult}");
            return new DecryptRunResult(
                false,
                isLoggedIn,
                decryptDir,
                ReadWeChatDbDir(decryptDir),
                outputState.ProcessErrorMessage,
                outputState.HandledInProcess,
                outputState.ProcessResult,
                outputState.MessageCount,
                outputState.AddedCount
            );
        }

        await PostEventAsync("client_extract_failed", new
        {
            stage = "decrypt_process",
            exit_code = process.ExitCode,
            logged_in = isLoggedIn,
            error_message = processErrorMessage.Length > 300
                ? processErrorMessage[..300]
                : (!string.IsNullOrWhiteSpace(processErrorMessage)
                    ? processErrorMessage
                    : (output.Length > 300 ? output[..300] : output)),
            attempt_kind = attemptOptions.AttemptKind
        });
        string finalErrorMessage = !string.IsNullOrWhiteSpace(processErrorMessage)
            ? processErrorMessage
            : (string.IsNullOrWhiteSpace(output) ? "解密失败" : output);
        Log($"解密失败: {finalErrorMessage}");
        return new DecryptRunResult(
            false,
            isLoggedIn,
            decryptDir,
            ReadWeChatDbDir(decryptDir),
            finalErrorMessage
        );
    }

    private static bool ShouldRestartWeChatForFreshHook(DecryptRunResult decryptResult)
    {
        if (decryptResult.Success)
            return false;

        string error = (decryptResult.ErrorMessage ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(error))
            return false;

        return error.Contains("Hook安装成功", StringComparison.OrdinalIgnoreCase)
            || error.Contains("现在登录微信", StringComparison.OrdinalIgnoreCase)
            || error.Contains("获取密钥超时", StringComparison.OrdinalIgnoreCase)
            || error.Contains("内存扫描数据库密钥失败", StringComparison.OrdinalIgnoreCase)
            || error.Contains("未能从微信进程内存中匹配到任何数据库密钥", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<WeChatRestartResult> RestartWeChatAndWaitForNewProcessAsync(WeChatProcessSnapshot currentWeChatProcess)
    {
        var currentProcesses = GetWeChatProcessesSnapshot();
        var oldPidSet = currentProcesses.Select(item => item.ProcessId).ToHashSet();
        string executablePath = currentProcesses
            .Select(item => item.ExecutablePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
            ?? currentWeChatProcess.ExecutablePath;

        foreach (int pid in oldPidSet)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                    process.CloseMainWindow();
            }
            catch
            {
            }
        }

        bool exitedGracefully = await WaitForPidSetExitAsync(oldPidSet, WECHAT_EXIT_WAIT_TIMEOUT_SECONDS * 1000);
        if (!exitedGracefully)
        {
            foreach (int pid in oldPidSet)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited)
                        process.Kill(true);
                }
                catch
                {
                }
            }

            await WaitForPidSetExitAsync(oldPidSet, 5000);
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            await PostEventAsync("client_wechat_restart_result", new
            {
                success = false,
                reason = "wechat_executable_missing",
                executable_path = executablePath
            });
            return new WeChatRestartResult(false, null, "没有拿到可重启的微信程序路径");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
                UseShellExecute = true,
            };
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            await PostEventAsync("client_wechat_restart_result", new
            {
                success = false,
                reason = "wechat_restart_launch_failed",
                executable_path = executablePath,
                error_message = ex.Message
            });
            return new WeChatRestartResult(false, null, $"重新启动微信失败: {ex.Message}");
        }

        var waitStopwatch = Stopwatch.StartNew();
        while (waitStopwatch.Elapsed < TimeSpan.FromSeconds(WECHAT_RESTART_WAIT_TIMEOUT_SECONDS))
        {
            var processes = GetWeChatProcessesSnapshot();
            var nextProcess = processes.FirstOrDefault(item => !oldPidSet.Contains(item.ProcessId));
            if (nextProcess is not null)
            {
                await PostEventAsync("client_wechat_restart_result", new
                {
                    success = true,
                    pid = nextProcess.ProcessId,
                    process_name = nextProcess.ProcessName,
                    executable_path = nextProcess.ExecutablePath,
                    wechat_version = nextProcess.ExecutableVersion,
                    wait_elapsed_ms = waitStopwatch.ElapsedMilliseconds
                });
                return new WeChatRestartResult(true, nextProcess, "", waitStopwatch.ElapsedMilliseconds);
            }

            await Task.Delay(WECHAT_NEW_PROCESS_POLL_INTERVAL_MS);
        }

        await PostEventAsync("client_wechat_restart_result", new
        {
            success = false,
            reason = "wechat_new_process_timeout",
            executable_path = executablePath,
            wait_timeout_seconds = WECHAT_RESTART_WAIT_TIMEOUT_SECONDS
        });
        return new WeChatRestartResult(false, null, "自动重启微信后，等待新微信进程超时");
    }

    private static async Task<bool> WaitForPidSetExitAsync(HashSet<int> pidSet, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            bool anyAlive = false;
            foreach (int pid in pidSet)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited)
                    {
                        anyAlive = true;
                        break;
                    }
                }
                catch
                {
                }
            }

            if (!anyAlive)
                return true;

            await Task.Delay(WECHAT_NEW_PROCESS_POLL_INTERVAL_MS);
        }

        return false;
    }

    private static List<WeChatProcessSnapshot> GetWeChatProcessesSnapshot()
    {
        var processes = new List<WeChatProcessSnapshot>();
        foreach (string processName in _wechatProcessNames)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        if (process.HasExited)
                            continue;

                        processes.Add(
                            new WeChatProcessSnapshot(
                                process.Id,
                                process.ProcessName,
                                TryGetProcessExecutablePath(process),
                                TryGetProcessExecutableVersion(process)
                            )
                        );
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
            }
        }

        return processes
            .GroupBy(item => item.ProcessId)
            .Select(group => group.First())
            .OrderBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProcessId)
            .ToList();
    }

    private static WeChatProcessSnapshot? GetPreferredWeChatProcess()
    {
        return GetWeChatProcessesSnapshot()
            .OrderBy(item => string.Equals(item.ProcessName, "Weixin", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.ProcessId)
            .FirstOrDefault();
    }

    private static string TryGetProcessExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryGetProcessExecutableVersion(Process process)
    {
        try
        {
            string path = process.MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;

            var version = FileVersionInfo.GetVersionInfo(path);
            return version.FileVersion
                ?? version.ProductVersion
                ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool DetectRunningWeChatProcess()
    {
        try
        {
            foreach (string processName in _wechatProcessNames)
            {
                if (Process.GetProcessesByName(processName).Length > 0)
                    return true;
            }
        }
        catch
        {
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tasklist",
                Arguments = "/FO CSV /NH",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output.Contains("Weixin.exe", StringComparison.OrdinalIgnoreCase)
                || output.Contains("WeChat.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
        }

        return false;
    }

    private static bool ParseLoginStatus(string output)
    {
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseLoginStatusLine(line, out bool isLoggedIn))
                return isLoggedIn;
        }

        return false;
    }

    private static bool TryParseLoginStatusLine(string line, out bool isLoggedIn)
    {
        const string prefix = "__WX_LOGIN_STATUS__=";
        isLoggedIn = false;
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        string value = line[prefix.Length..].Trim();
        isLoggedIn = value == "1";
        return true;
    }

    private static string SanitizeDecryptOutput(string output)
    {
        const string loginPrefix = "__WX_LOGIN_STATUS__=";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();

        foreach (string rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line.StartsWith(loginPrefix, StringComparison.Ordinal))
                continue;
            if (line.StartsWith(RuntimeEventPrefix, StringComparison.Ordinal))
                continue;
            if (line.StartsWith(DbKeyCachePrefix, StringComparison.Ordinal))
                continue;
            if (line.Contains("WeChat No Run", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("[-]", StringComparison.Ordinal))
                continue;
            if (!seen.Add(line))
                continue;
            lines.Add(line);
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static List<RuntimeProcessEvent> ParseRuntimeEvents(string output)
    {
        var events = new List<RuntimeProcessEvent>();

        foreach (string rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var runtimeEvent = ParseRuntimeEventLine(rawLine.Trim());
            if (runtimeEvent is not null)
                events.Add(runtimeEvent);
        }

        return events;
    }

    private static RuntimeProcessEvent? ParseRuntimeEventLine(string line)
    {
        if (!line.StartsWith(RuntimeEventPrefix, StringComparison.Ordinal))
            return null;

        string json = line[RuntimeEventPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            string eventName = doc.RootElement.TryGetProperty("event_name", out var eventNameEl)
                ? (eventNameEl.GetString() ?? "")
                : "";
            if (string.IsNullOrWhiteSpace(eventName))
                return null;

            Dictionary<string, object?> payload = new();
            if (doc.RootElement.TryGetProperty("payload", out var payloadEl))
            {
                payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    payloadEl.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                ) ?? new Dictionary<string, object?>();
            }

            return new RuntimeProcessEvent(eventName, payload);
        }
        catch
        {
            return null;
        }
    }

    private static async Task PumpProcessStreamAsync(StreamReader reader, DecryptProcessOutputState outputState)
    {
        while (true)
        {
            string? rawLine = await reader.ReadLineAsync();
            if (rawLine == null)
                break;

            string line = rawLine.Trim();
            outputState.AppendLine(line);

            if (TryParseLoginStatusLine(line, out bool isLoggedIn))
            {
                outputState.HasLoginStatus = true;
                outputState.IsLoggedIn = isLoggedIn;
                continue;
            }

            if (TryParseDbKeyCacheLine(line, out string cachePayloadJson))
            {
                SetDbKeyCachePayload(cachePayloadJson);
                continue;
            }

            var runtimeEvent = ParseRuntimeEventLine(line);
            if (runtimeEvent is not null)
            {
                outputState.ApplyRuntimeEvent(runtimeEvent);
                await PostEventAsync(runtimeEvent.EventName, runtimeEvent.Payload);
            }
        }
    }

    private static bool TryParseDbKeyCacheLine(string line, out string cachePayloadJson)
    {
        cachePayloadJson = "";
        if (!line.StartsWith(DbKeyCachePrefix, StringComparison.Ordinal))
            return false;

        string json = line[DbKeyCachePrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var _ = JsonDocument.Parse(json);
            cachePayloadJson = json;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 提取微信聊天记录（从解密后的数据库）
    /// </summary>
    private static List<WeChatMessage> ExtractMessages(string decryptDir, string weChatDbDir)
    {
        string decryptMode = ReadDecryptMode(decryptDir);
        if (string.Equals(decryptMode, "chatlog_v4", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decryptMode, "weflow_wcdb_v4", StringComparison.OrdinalIgnoreCase))
        {
            return ReadChatlogExportMessages(decryptDir);
        }

        var messages = new List<WeChatMessage>();

        if (!Directory.Exists(decryptDir))
        {
            Log("未找到解密数据库目录");
            _ = PostEventAsync("client_extract_failed", new
            {
                stage = "decrypt_dir",
                reason = "decrypt_dir_missing"
            });
            return null;
        }

        foreach (var dbFile in Directory.GetFiles(decryptDir, "MSG*.db"))
        {
            try
            {
                using var conn = new SQLiteConnection($"Data Source={dbFile};Version=3;Read Only=True;");
                conn.Open();

                using var cmd = conn.CreateCommand();
                var columns = GetColumns(conn, "MSG");
                cmd.CommandText = $@"
                    SELECT
                        StrTalker as wxid,
                        StrContent as content,
                        CreateTime as create_time,
                        IsSender as is_sender,
                        {SqlColumn(columns, "Type", "msg_type", "0")},
                        {SqlColumn(columns, "SubType", "msg_sub_type", "0")},
                        {SqlColumn(columns, "BytesExtra", "bytes_extra", "''")},
                        {SqlColumn(columns, "CompressContent", "compress_content", "''")}
                    FROM MSG
                    ORDER BY CreateTime DESC
                    LIMIT 5000";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string? content = reader["content"]?.ToString();
                    content ??= "";

                    int msgType = ToInt(reader["msg_type"]);
                    int msgSubType = ToInt(reader["msg_sub_type"]);
                    string bytesExtra = ReadDbString(reader["bytes_extra"]);
                    string compressContent = ReadDbString(reader["compress_content"]);
                    bool isImage = msgType == 3
                        || LooksLikeImageMessage(content)
                        || LooksLikeImageMessage(bytesExtra)
                        || LooksLikeImageMessage(compressContent);

                    if (string.IsNullOrWhiteSpace(content) && !isImage)
                        continue;

                    var media = isImage
                        ? TryLoadImageMedia(weChatDbDir, content, bytesExtra, compressContent)
                        : null;

                    messages.Add(new WeChatMessage
                    {
                        wxid = reader["wxid"]?.ToString() ?? "",
                        content = string.IsNullOrWhiteSpace(content)
                            ? (isImage ? "[图片]" : "")
                            : (content.Length > 500 ? content[..500] : content),
                        create_time = Convert.ToInt64(reader["create_time"]),
                        is_sender = Convert.ToBoolean(reader["is_sender"]),
                        nickname = reader["wxid"]?.ToString() ?? "",
                        sender = Convert.ToBoolean(reader["is_sender"])
                            ? "我"
                            : (reader["wxid"]?.ToString() ?? ""),
                        msg_type = msgType,
                        msg_sub_type = msgSubType,
                        media_type = isImage ? "image" : "",
                        media_mime = media?.Mime ?? "",
                        media_name = media?.Name ?? "",
                        media_data = media?.Base64 ?? ""
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"读取 {Path.GetFileName(dbFile)} 失败: {ex.Message}");
                _ = PostEventAsync("client_extract_failed", new
                {
                    stage = "sqlite_read",
                    db_file = Path.GetFileName(dbFile),
                    error_message = ex.Message
                });
            }
        }

        return messages
            .OrderByDescending(m => m.create_time)
            .Take(MAX_MESSAGE_BATCH)
            .OrderBy(m => m.create_time)
            .ToList();
    }

    private static List<WeChatMessage> ReadChatlogExportMessages(string decryptDir)
    {
        string exportPath = Path.Combine(decryptDir, CHATLOG_EXPORT_FILE_NAME);
        if (!File.Exists(exportPath))
        {
            Log("未找到 chatlog_export.json");
            _ = PostEventAsync("client_extract_failed", new
            {
                stage = "chatlog_export",
                reason = "chatlog_export_missing"
            });
            return new List<WeChatMessage>();
        }

        try
        {
            string json = File.ReadAllText(exportPath, Encoding.UTF8);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                Log("chatlog_export.json 不是数组结构");
                return new List<WeChatMessage>();
            }

            var messages = new List<WeChatMessage>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                messages.Add(new WeChatMessage
                {
                    wxid = ReadJsonString(item, "wxid"),
                    content = ReadJsonString(item, "content"),
                    create_time = ReadJsonInt64(item, "create_time"),
                    is_sender = ReadJsonBool(item, "is_sender"),
                    nickname = ReadJsonString(item, "nickname"),
                    sender = ReadJsonString(item, "sender"),
                    avatar = ReadJsonString(item, "avatar"),
                    msg_type = ReadJsonInt(item, "msg_type"),
                    msg_sub_type = ReadJsonInt(item, "msg_sub_type"),
                    media_type = ReadJsonString(item, "media_type"),
                    media_mime = ReadJsonString(item, "media_mime"),
                    media_name = ReadJsonString(item, "media_name"),
                    media_data = ReadJsonString(item, "media_data"),
                });
            }

            return messages
                .OrderByDescending(m => m.create_time)
                .Take(MAX_MESSAGE_BATCH)
                .OrderBy(m => m.create_time)
                .ToList();
        }
        catch (Exception ex)
        {
            Log($"读取 chatlog_export.json 失败: {ex.Message}");
            _ = PostEventAsync("client_extract_failed", new
            {
                stage = "chatlog_export",
                error_message = ex.Message
            });
            return new List<WeChatMessage>();
        }
    }

    private static List<JsonElement> ReadContactExportItems(string decryptDir) =>
        ReadJsonObjectArray(Path.Combine(decryptDir, CONTACT_EXPORT_FILE_NAME), "contact_export");

    private static List<JsonElement> ReadFavoriteExportItems(string decryptDir) =>
        ReadJsonObjectArray(Path.Combine(decryptDir, FAVORITE_EXPORT_FILE_NAME), "favorite_export");

    private static List<JsonElement> ReadJsonObjectArray(string path, string stage)
    {
        if (!File.Exists(path))
            return new List<JsonElement>();

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new List<JsonElement>();

            var items = new List<JsonElement>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    items.Add(item.Clone());
            }
            return items;
        }
        catch (Exception ex)
        {
            Log($"读取 {Path.GetFileName(path)} 失败: {ex.Message}");
            _ = PostEventAsync("client_extract_failed", new
            {
                stage,
                error_message = ex.Message
            });
            return new List<JsonElement>();
        }
    }

    private static string ReadJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null => "",
            JsonValueKind.Undefined => "",
            _ => value.ToString()
        };
    }

    private static int ReadJsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.True)
            return 1;

        if (value.ValueKind == JsonValueKind.False)
            return 0;

        return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static long ReadJsonInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.True)
            return 1;

        if (value.ValueKind == JsonValueKind.False)
            return 0;

        return long.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static bool ReadJsonBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return false;

        if (value.ValueKind == JsonValueKind.True)
            return true;

        if (value.ValueKind == JsonValueKind.False)
            return false;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number != 0;

        if (bool.TryParse(value.ToString(), out var parsedBool))
            return parsedBool;

        return int.TryParse(value.ToString(), out var parsedInt) && parsedInt != 0;
    }

    private static HashSet<string> GetColumns(SQLiteConnection conn, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader["name"]?.ToString() ?? "");
        }

        return columns;
    }

    private static string SqlColumn(HashSet<string> columns, string column, string alias, string fallback) =>
        columns.Contains(column) ? $"{column} as {alias}" : $"{fallback} as {alias}";

    private static int ToInt(object? value)
    {
        if (value == null || value == DBNull.Value)
            return 0;
        return int.TryParse(value.ToString(), out var result) ? result : 0;
    }

    private static string ReadDbString(object? value)
    {
        if (value == null || value == DBNull.Value)
            return "";

        if (value is byte[] bytes)
        {
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return Convert.ToBase64String(bytes);
            }
        }

        return value.ToString() ?? "";
    }

    private static string ReadWeChatDbDir(string decryptDir)
    {
        var meta = ReadDecryptMeta(decryptDir);
        return meta?.DbDir ?? "";
    }

    private static string ReadDecryptMode(string decryptDir)
    {
        var meta = ReadDecryptMeta(decryptDir);
        return meta?.Mode ?? "";
    }

    private static DecryptMeta? ReadDecryptMeta(string decryptDir)
    {
        string metaPath = Path.Combine(decryptDir, "decrypt_meta.json");
        if (!File.Exists(metaPath))
            return null;

        try
        {
            return JsonSerializer.Deserialize<DecryptMeta>(
                File.ReadAllText(metaPath, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
        }
        catch (Exception ex)
        {
            Log($"读取 decrypt_meta.json 失败: {ex.Message}");
            return null;
        }
    }

    private static bool LooksLikeImageMessage(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("<img", StringComparison.OrdinalIgnoreCase)
            || value.Contains("cdnthumb", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(value, @"(?i)\.(jpg|jpeg|png|gif|bmp|webp|dat)");
    }

    private static ImageMedia? TryLoadImageMedia(string weChatDbDir, params string[] sources)
    {
        foreach (var candidate in ExtractImageCandidates(sources))
        {
            string? path = ResolveImagePath(weChatDbDir, candidate);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var media = LoadImageFile(path);
            if (media != null)
                return media;
        }

        Log("识别到图片消息，但没有从消息字段中找到可读取的本地图片文件");
        _ = PostEventAsync("client_media_missing", new
        {
            media_type = "image",
            reason = "local_image_path_not_found"
        });
        return null;
    }

    private static IEnumerable<string> ExtractImageCandidates(IEnumerable<string> sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var patterns = new[]
        {
            "(?i)[A-Z]:\\\\[^<>:\"|?*\\r\\n]+?\\.(?:jpg|jpeg|png|gif|bmp|webp|dat)",
            "(?i)(?:FileStorage|Image|Thumb|MsgAttach)[\\\\/][^<>:\"|?*\\r\\n]+?\\.(?:jpg|jpeg|png|gif|bmp|webp|dat)",
            "(?i)(?:path|thumb|image|img|file)=\"([^\"]+\\.(?:jpg|jpeg|png|gif|bmp|webp|dat))\""
        };

        foreach (var source in sources.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            foreach (var pattern in patterns)
            {
                foreach (Match match in Regex.Matches(source, pattern))
                {
                    string value = match.Groups.Count > 1 && match.Groups[1].Success
                        ? match.Groups[1].Value
                        : match.Value;
                    value = value.Replace("&amp;", "&").Trim();
                    if (seen.Add(value))
                        yield return value;
                }
            }
        }
    }

    private static string? ResolveImagePath(string weChatDbDir, string candidate)
    {
        if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;

        string normalized = candidate.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) && File.Exists(normalized))
            return normalized;

        foreach (var root in GetWeChatSearchRoots(weChatDbDir))
        {
            string combined = Path.Combine(root, normalized);
            if (File.Exists(combined))
                return combined;
        }

        return null;
    }

    private static IEnumerable<string> GetWeChatSearchRoots(string weChatDbDir)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(weChatDbDir))
        {
            roots.Add(weChatDbDir);
            string? parent = Directory.GetParent(weChatDbDir)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
                roots.Add(parent);

            int msgIndex = weChatDbDir.IndexOf($"{Path.DirectorySeparatorChar}Msg", StringComparison.OrdinalIgnoreCase);
            if (msgIndex > 0)
                roots.Add(weChatDbDir[..msgIndex]);
        }

        roots.Add(AppDomain.CurrentDomain.BaseDirectory);
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static ImageMedia? LoadImageFile(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string mime = MimeFromExtension(ext);
            string name = Path.GetFileName(path);

            if (ext == ".dat" && TryDecodeWeChatDat(bytes, out var decoded, out var decodedMime, out var decodedExt))
            {
                bytes = decoded;
                mime = decodedMime;
                name = Path.GetFileNameWithoutExtension(path) + decodedExt;
            }

            if (bytes.Length > MAX_IMAGE_BYTES)
            {
                Log($"图片过大，跳过上传: {name}, {bytes.Length} bytes");
                _ = PostEventAsync("client_media_skipped", new
                {
                    media_type = "image",
                    reason = "file_too_large",
                    file_name = name,
                    file_size = bytes.Length,
                    max_size = MAX_IMAGE_BYTES
                });
                return null;
            }

            return new ImageMedia("image", mime, name, Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            Log($"读取图片失败: {path}: {ex.Message}");
            _ = PostEventAsync("client_media_skipped", new
            {
                media_type = "image",
                reason = "read_failed",
                file_path = path,
                error_message = ex.Message
            });
            return null;
        }
    }

    private static bool TryDecodeWeChatDat(byte[] raw, out byte[] decoded, out string mime, out string ext)
    {
        var magics = new (byte[] Magic, string Mime, string Ext)[]
        {
            (new byte[] { 0xFF, 0xD8 }, "image/jpeg", ".jpg"),
            (new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "image/png", ".png"),
            (new byte[] { 0x47, 0x49, 0x46 }, "image/gif", ".gif"),
            (new byte[] { 0x42, 0x4D }, "image/bmp", ".bmp"),
            (new byte[] { 0x52, 0x49, 0x46, 0x46 }, "image/webp", ".webp"),
        };

        foreach (var item in magics)
        {
            if (raw.Length < item.Magic.Length)
                continue;

            byte key = (byte)(raw[0] ^ item.Magic[0]);
            bool matches = true;
            for (int i = 0; i < item.Magic.Length; i++)
            {
                if ((byte)(raw[i] ^ key) != item.Magic[i])
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
                continue;

            decoded = raw.Select(b => (byte)(b ^ key)).ToArray();
            mime = item.Mime;
            ext = item.Ext;
            return true;
        }

        decoded = Array.Empty<byte>();
        mime = "";
        ext = "";
        return false;
    }

    private static string MimeFromExtension(string ext) => ext switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    private static List<WeChatMessage> FilterIncrementalMessages(List<WeChatMessage> messages, out int skippedCount)
    {
        HashSet<string> existing;
        lock (_syncStateLock)
        {
            existing = new HashSet<string>(_syncState.recent_message_keys ?? new List<string>(), StringComparer.Ordinal);
        }

        var result = new List<WeChatMessage>();
        skippedCount = 0;

        foreach (var message in messages.OrderBy(m => m.create_time))
        {
            string key = GetMessageKey(message);
            if (existing.Contains(key))
            {
                skippedCount++;
                continue;
            }

            result.Add(message);
        }

        return result;
    }

    private static void MarkMessagesSynced(IEnumerable<WeChatMessage> messages)
    {
        lock (_syncStateLock)
        {
            var keys = new List<string>(_syncState.recent_message_keys ?? new List<string>());
            var seen = new HashSet<string>(keys, StringComparer.Ordinal);

            foreach (var message in messages)
            {
                string key = GetMessageKey(message);
                if (seen.Add(key))
                    keys.Add(key);
            }

            if (keys.Count > MAX_SYNCED_MESSAGE_KEYS)
                keys = keys.TakeLast(MAX_SYNCED_MESSAGE_KEYS).ToList();

            _syncState.recent_message_keys = keys;
            _syncState.updated_at = DateTime.UtcNow.ToString("O");
            SaveSyncState(_syncState);
        }
    }

    private static string GetMessageKey(WeChatMessage message)
    {
        string raw = string.Join(
            "\u001f",
            message.wxid ?? "",
            message.create_time.ToString(),
            message.is_sender ? "1" : "0",
            message.sender ?? "",
            message.content ?? "",
            message.msg_type.ToString(),
            message.msg_sub_type.ToString(),
            message.media_type ?? "",
            message.media_name ?? ""
        );
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw)));
    }

    private static string GetDecryptDir() =>
        Path.Combine(GetWeChatDataDir(), "decrypted");

    private static string GetLegacySyncStatePath() =>
        Path.Combine(GetWeChatDataDir(), SYNC_STATE_FILE_NAME);

    private static string GetSyncStateDir() =>
        Path.Combine(GetWeChatDataDir(), "sync_state");

    private static string GetSyncStatePath(string scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            return GetLegacySyncStatePath();

        return Path.Combine(GetSyncStateDir(), $"{scopeKey}.json");
    }

    private static string GetClientIdentityPath() =>
        Path.Combine(GetWeChatDataDir(), CLIENT_IDENTITY_FILE_NAME);

    private static string GetWeChatDataDir() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wechat_data");

    private static string GetSyncStateScopeKey(string decryptDir)
    {
        string wxid = ReadDecryptMeta(decryptDir)?.Wxid ?? "";
        if (string.IsNullOrWhiteSpace(wxid))
            return "";

        string safe = Regex.Replace(wxid.Trim(), @"[^\w.-]+", "_");
        return string.IsNullOrWhiteSpace(safe) ? "" : safe;
    }

    private static LocalSyncState LoadSyncState(string scopeKey)
    {
        string path = GetSyncStatePath(scopeKey);
        if (!string.IsNullOrWhiteSpace(scopeKey))
            MigrateLegacySyncStateIfNeeded(path);
        if (!File.Exists(path))
            return new LocalSyncState();

        try
        {
            return JsonSerializer.Deserialize<LocalSyncState>(
                File.ReadAllText(path, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? new LocalSyncState();
        }
        catch
        {
            return new LocalSyncState();
        }
    }

    private static void SaveSyncState(LocalSyncState state)
    {
        string path = GetSyncStatePath(_syncStateScopeKey ?? "");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppDomain.CurrentDomain.BaseDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(state), Encoding.UTF8);
    }

    private static void EnsureSyncStateLoaded(string scopeKey)
    {
        lock (_syncStateLock)
        {
            if (_syncStateScopeKey is not null && string.Equals(_syncStateScopeKey, scopeKey, StringComparison.Ordinal))
                return;

            _syncState = LoadSyncState(scopeKey);
            _syncStateScopeKey = scopeKey;
        }
    }

    private static void MigrateLegacySyncStateIfNeeded(string scopedPath)
    {
        string legacyPath = GetLegacySyncStatePath();
        if (File.Exists(scopedPath) || !File.Exists(legacyPath))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(scopedPath) ?? AppDomain.CurrentDomain.BaseDirectory);
            File.Copy(legacyPath, scopedPath, overwrite: false);
            File.Delete(legacyPath);
        }
        catch (Exception ex)
        {
            Log($"迁移旧版 sync_state 失败: {ex.Message}");
        }
    }

    private static string? GetDbKeyCachePayload()
    {
        lock (_dbKeyCacheLock)
        {
            return _dbKeyCachePayloadJson;
        }
    }

    private static void SetDbKeyCachePayload(string cachePayloadJson)
    {
        lock (_dbKeyCacheLock)
        {
            _dbKeyCachePayloadJson = cachePayloadJson;
        }
    }

    private static ClientIdentityState LoadOrCreateClientIdentity()
    {
        string path = GetClientIdentityPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppDomain.CurrentDomain.BaseDirectory);

        try
        {
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<ClientIdentityState>(
                    File.ReadAllText(path, Encoding.UTF8),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
                if (!string.IsNullOrWhiteSpace(existing?.session_id))
                    return existing;
            }
        }
        catch
        {
        }

        var created = new ClientIdentityState
        {
            session_id = $"client-cs-{Guid.NewGuid():N}",
            created_at = DateTime.UtcNow.ToString("O"),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(created), Encoding.UTF8);
        return created;
    }

    private static async Task<CleanupResult> CleanupDecryptArtifactsAsync(string decryptDir)
    {
        await PostEventAsync("client_disk_cleanup_started", new { decrypt_dir = decryptDir });
        var result = CleanupDecryptArtifacts(decryptDir);
        await PostEventAsync("client_disk_cleanup_result", new
        {
            decrypt_dir = decryptDir,
            success = result.Success,
            removed_count = result.RemovedCount,
            failed_count = result.FailedCount,
            failed_paths = result.FailedPaths
        });
        return result;
    }

    private static CleanupResult CleanupDecryptArtifacts(string decryptDir)
    {
        var failedPaths = new List<string>();
        int removedCount = 0;

        foreach (string fileName in new[] { CHATLOG_EXPORT_FILE_NAME, CONTACT_EXPORT_FILE_NAME, FAVORITE_EXPORT_FILE_NAME, "decrypt_meta.json" })
        {
            string path = Path.Combine(decryptDir, fileName);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removedCount++;
                }
            }
            catch
            {
                failedPaths.Add(path);
            }
        }

        foreach (string dirName in new[] { "contact", "Contact", "message", "session" })
        {
            string path = Path.Combine(decryptDir, dirName);
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                    removedCount++;
                }
            }
            catch
            {
                failedPaths.Add(path);
            }
        }

        return new CleanupResult(failedPaths.Count == 0, removedCount, failedPaths.Count, failedPaths);
    }

    private static async Task PushSupplementalDataAsync(string decryptDir)
    {
        var contacts = ReadContactExportItems(decryptDir);
        if (contacts.Count > 0)
        {
            int avatarCount = contacts.Count(item =>
                item.TryGetProperty("avatar", out var avatar)
                && avatar.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(avatar.GetString())
            );
            var result = await PushJsonArrayAsync(_contactsUrl, "contacts", contacts);
            await PostEventAsync("client_contacts_push_result", new
            {
                contact_count = contacts.Count,
                avatar_count = avatarCount,
                changed_count = result.ChangedCount,
                success = result.Success,
                status_code = result.StatusCode,
                error_message = result.ErrorMessage,
                response_text = result.ResponseText
            });
        }

        var favorites = ReadFavoriteExportItems(decryptDir);
        if (favorites.Count > 0)
        {
            var result = await PushJsonArrayAsync(_favoritesUrl, "favorites", favorites);
            await PostEventAsync("client_favorites_push_result", new
            {
                favorite_count = favorites.Count,
                changed_count = result.ChangedCount,
                success = result.Success,
                status_code = result.StatusCode,
                error_message = result.ErrorMessage,
                response_text = result.ResponseText
            });
        }
        else
        {
            await PostEventAsync("client_favorites_push_result", new
            {
                favorite_count = 0,
                changed_count = 0,
                success = true,
                skipped = true,
                reason = "no_favorites_exported"
            });
        }
    }

    private static async Task<SupplementPushResult> PushJsonArrayAsync(string url, string fieldName, List<JsonElement> items)
    {
        string requestId = CreateRequestId(fieldName);
        try
        {
            var payload = new Dictionary<string, object?>
            {
                [fieldName] = items,
                ["token"] = _config.ServerToken,
                ["source"] = ClientSource,
                ["session_id"] = _sessionId,
                ["request_id"] = requestId,
            };

            var resp = await PostJsonWithRetryAsync(url, payload, $"{fieldName}_upload");
            string responseText = resp.ResponseText;

            int changedCount = 0;
            try
            {
                using var doc = JsonDocument.Parse(responseText);
                if (doc.RootElement.TryGetProperty("changed", out var changed))
                    changedCount = changed.GetInt32();
            }
            catch
            {
            }

            string trimmedResponse = responseText.Length > 500 ? responseText[..500] : responseText;
            if (!resp.Success)
            {
                string message = !string.IsNullOrWhiteSpace(resp.ErrorMessage)
                    ? resp.ErrorMessage
                    : trimmedResponse;
                Log($"{fieldName} 上传失败: {resp.StatusCode} {message}");
            }

            return new SupplementPushResult(
                resp.Success,
                changedCount,
                resp.StatusCode,
                resp.Success ? "" : (!string.IsNullOrWhiteSpace(resp.ErrorMessage) ? resp.ErrorMessage : trimmedResponse),
                trimmedResponse
            );
        }
        catch (Exception ex)
        {
            Log($"{fieldName} 上传异常: {ex.Message}");
            return new SupplementPushResult(false, 0, 0, ex.Message, "");
        }
    }

    private static async Task<PushToServerResult> PushToServerAsync(List<WeChatMessage> messages)
    {
        var sw = Stopwatch.StartNew();
        await PostEventAsync("client_push_started", new { message_count = messages.Count });
        string requestId = CreateRequestId("messages");

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["messages"] = messages,
                ["token"] = _config.ServerToken,
                ["source"] = ClientSource,
                ["session_id"] = _sessionId,
                ["request_id"] = requestId,
            };

            var resp = await PostJsonWithRetryAsync(_config.ServerUrl, payload, "messages_upload");

            if (resp.Success)
            {
                Log("推送成功");
                string responseText = resp.ResponseText;
                int addedCount = 0;

                try
                {
                    using var doc = JsonDocument.Parse(responseText);
                    if (doc.RootElement.TryGetProperty("added", out var added))
                        addedCount = added.GetInt32();
                }
                catch
                {
                }

                await PostEventAsync("client_push_result", new
                {
                    success = true,
                    status_code = resp.StatusCode,
                    message_count = messages.Count,
                    added_count = addedCount,
                    duration_ms = sw.ElapsedMilliseconds
                });
                return new PushToServerResult(true, messages.Count, addedCount, resp.StatusCode);
            }
            else
            {
                string responseText = resp.ResponseText;
                string errorText = !string.IsNullOrWhiteSpace(resp.ErrorMessage)
                    ? resp.ErrorMessage
                    : (responseText.Length > 300 ? responseText[..300] : responseText);
                Log($"推送失败: {resp.StatusCode} {errorText}");
                await PostEventAsync("client_push_failed", new
                {
                    status_code = resp.StatusCode,
                    message_count = messages.Count,
                    response_text = responseText.Length > 300 ? responseText[..300] : responseText,
                    error_message = errorText,
                    duration_ms = sw.ElapsedMilliseconds
                });
                return new PushToServerResult(false, 0, 0, resp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            Log($"推送异常: {ex.Message}");
            await PostEventAsync("client_push_failed", new
            {
                status_code = 0,
                message_count = messages.Count,
                error_message = ex.Message,
                duration_ms = sw.ElapsedMilliseconds
            });
            return new PushToServerResult(false, 0, 0, 0);
        }
    }

    private static async Task PostStatusAsync(bool wechatLoggedIn, bool decryptOk, string? error = null)
    {
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["token"] = _config.ServerToken,
                ["source"] = ClientSource,
                ["session_id"] = _sessionId,
                ["request_id"] = CreateRequestId("status"),
                ["wechat_logged_in"] = wechatLoggedIn,
                ["decrypt_ok"] = decryptOk,
            };

            if (!string.IsNullOrWhiteSpace(error))
                body["error"] = error;

            var resp = await PostJsonWithRetryAsync(_statusUrl, body, "status_report");
            if (!resp.Success)
                Log($"状态上报失败: {resp.StatusCode} {resp.ErrorMessage}");
        }
        catch (Exception ex)
        {
            Log($"状态上报失败: {ex.Message}");
        }
    }

    private static async Task PostHeartbeatAsync(bool? wechatLoggedIn = null)
    {
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["token"] = _config.ServerToken,
                ["source"] = ClientSource,
                ["session_id"] = _sessionId,
                ["request_id"] = CreateRequestId("heartbeat"),
            };

            if (wechatLoggedIn.HasValue)
                body["wechat_logged_in"] = wechatLoggedIn.Value;

            var resp = await PostJsonWithRetryAsync(_statusUrl, body, "heartbeat");
            if (!resp.Success)
                Log($"心跳上报失败: {resp.StatusCode} {resp.ErrorMessage}");
        }
        catch (Exception ex)
        {
            Log($"心跳上报失败: {ex.Message}");
        }
    }

    private static async Task PostEventAsync(string eventName, object payload)
    {
        try
        {
            JsonElement payloadElement = ConvertToJsonElement(payload);
            if (ShouldSuppressEvent(eventName, payloadElement, out string suppressionReason))
            {
                Log($"运行日志事件已抑制: {eventName}: {suppressionReason}");
                return;
            }

            var body = new Dictionary<string, object?>
            {
                ["token"] = _config.ServerToken,
                ["source"] = ClientSource,
                ["session_id"] = _sessionId,
                ["request_id"] = CreateRequestId($"event_{eventName}"),
                ["event_name"] = eventName,
                ["payload"] = payloadElement
            };

            var resp = await PostJsonWithRetryAsync(_eventsUrl, body, $"event_{eventName}", maxAttempts: 2);
            if (!resp.Success)
                Log($"运行日志上报失败: {eventName}: {resp.StatusCode} {resp.ErrorMessage}");
        }
        catch (Exception ex)
        {
            Log($"运行日志上报失败: {eventName}: {ex.Message}");
        }
    }

    private static void BeginScanCycle()
    {
        lock (_eventSuppressionLock)
        {
            _currentScanIssueReported = false;
        }
    }

    private static void EndScanCycle()
    {
        lock (_eventSuppressionLock)
        {
            _currentScanIssueReported = false;
        }
    }

    private static bool ShouldSuppressEvent(string eventName, JsonElement payload, out string suppressionReason)
    {
        suppressionReason = "";

        lock (_eventSuppressionLock)
        {
            if (TryHandleIssueRecovery(eventName, payload, out string recoveryReason))
            {
                suppressionReason = recoveryReason;
                return false;
            }

            if (TryBuildIssueSignature(eventName, payload, out string issueSignature))
            {
                if (string.Equals(_activeIssueSignature, issueSignature, StringComparison.Ordinal))
                {
                    _currentScanIssueReported = true;
                    suppressionReason = "duplicate_issue";
                    return true;
                }

                _activeIssueSignature = issueSignature;
                _currentScanIssueReported = true;
                return false;
            }

            if (IsFailureScanFinishedEvent(eventName, payload, out string scanResult))
            {
                string scanFailureSignature = $"scan_failure|{NormalizeIssueValue(scanResult)}";
                if (_currentScanIssueReported)
                {
                    suppressionReason = "issue_already_reported_in_scan";
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(_activeIssueSignature))
                {
                    if (string.Equals(_activeIssueSignature, scanFailureSignature, StringComparison.Ordinal))
                    {
                        _currentScanIssueReported = true;
                        suppressionReason = "duplicate_scan_failure";
                        return true;
                    }

                    _activeIssueSignature = scanFailureSignature;
                    _currentScanIssueReported = true;
                    return false;
                }

                _activeIssueSignature = scanFailureSignature;
                _currentScanIssueReported = true;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_activeIssueSignature))
            {
                suppressionReason = "active_issue_context";
                return true;
            }
        }

        return false;
    }

    private static bool TryHandleIssueRecovery(string eventName, JsonElement payload, out string recoveryReason)
    {
        recoveryReason = "";
        if (!string.Equals(eventName, "client_scan_finished", StringComparison.Ordinal))
            return false;

        string result = NormalizeIssueValue(ReadJsonString(payload, "result"));
        if (IsFailureScanResult(result))
            return false;

        if (!string.IsNullOrWhiteSpace(_activeIssueSignature))
        {
            Log($"问题状态已恢复，解除事件静默: {_activeIssueSignature} -> {result}");
        }

        _activeIssueSignature = null;
        _currentScanIssueReported = false;
        recoveryReason = "issue_recovered";
        return true;
    }

    private static bool TryBuildIssueSignature(string eventName, JsonElement payload, out string issueSignature)
    {
        issueSignature = "";

        if (string.Equals(eventName, "client_extract_failed", StringComparison.Ordinal))
        {
            string stage = NormalizeIssueValue(ReadJsonString(payload, "stage"));
            string attemptKind = NormalizeIssueValue(ReadJsonString(payload, "attempt_kind"));
            string errorMessage = NormalizeIssueValue(ReadJsonString(payload, "error_message"));
            string reason = NormalizeIssueValue(ReadJsonString(payload, "reason"));
            string detail = errorMessage == "unknown" ? reason : errorMessage;
            issueSignature = $"extract_failed|{stage}|{attemptKind}|{detail}";
            return true;
        }

        if (string.Equals(eventName, "client_push_failed", StringComparison.Ordinal))
        {
            string statusCode = NormalizeIssueValue(ReadJsonString(payload, "status_code"));
            string errorMessage = NormalizeIssueValue(ReadJsonString(payload, "error_message"));
            issueSignature = $"push_failed|{statusCode}|{errorMessage}";
            return true;
        }

        if (string.Equals(eventName, "client_wechat_restart_result", StringComparison.Ordinal))
        {
            if (ReadJsonBool(payload, "success"))
                return false;

            string errorMessage = NormalizeIssueValue(ReadJsonString(payload, "error_message"));
            issueSignature = $"wechat_restart_failed|{errorMessage}";
            return true;
        }

        return false;
    }

    private static bool IsFailureScanFinishedEvent(string eventName, JsonElement payload, out string result)
    {
        result = "";
        if (!string.Equals(eventName, "client_scan_finished", StringComparison.Ordinal))
            return false;

        result = NormalizeIssueValue(ReadJsonString(payload, "result"));
        return IsFailureScanResult(result);
    }

    private static bool IsFailureScanResult(string result)
    {
        return string.Equals(result, "decrypt_failed", StringComparison.Ordinal)
            || string.Equals(result, "push_failed", StringComparison.Ordinal)
            || string.Equals(result, "wechat_not_logged_in", StringComparison.Ordinal);
    }

    private static string NormalizeIssueValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        return Regex.Replace(value.Trim(), @"\s+", " ").ToLowerInvariant();
    }

    private static JsonElement ConvertToJsonElement(object payload)
    {
        return payload is JsonElement jsonElement
            ? jsonElement
            : JsonSerializer.SerializeToElement(payload);
    }

    private static string CreateRequestId(string scope)
    {
        string normalizedScope = string.IsNullOrWhiteSpace(scope)
            ? "request"
            : Regex.Replace(scope.Trim().ToLowerInvariant(), "[^a-z0-9_]+", "_");
        long sequence = Interlocked.Increment(ref _requestSequence);
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"{_sessionId}-{normalizedScope}-{timestamp}-{sequence}";
    }

    private static bool ShouldRetryStatusCode(int statusCode)
    {
        return statusCode == 408
            || statusCode == 429
            || statusCode >= 500;
    }

    private static async Task<HttpPostResult> PostJsonWithRetryAsync(
        string url,
        object body,
        string operationName,
        int maxAttempts = HTTP_RETRY_MAX_ATTEMPTS,
        int baseDelayMs = HTTP_RETRY_BASE_DELAY_MS
    )
    {
        string json = JsonSerializer.Serialize(body);
        int attempts = Math.Max(1, maxAttempts);
        int delayMs = Math.Max(250, baseDelayMs);

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(url, content);
                string responseText = await response.Content.ReadAsStringAsync();
                int statusCode = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                    return new HttpPostResult(true, statusCode, responseText, "");

                string errorMessage = string.IsNullOrWhiteSpace(responseText)
                    ? $"HTTP {statusCode}"
                    : responseText;
                if (attempt >= attempts || !ShouldRetryStatusCode(statusCode))
                    return new HttpPostResult(false, statusCode, responseText, errorMessage);
            }
            catch (TaskCanceledException ex)
            {
                if (attempt >= attempts)
                    return new HttpPostResult(false, 0, "", $"请求超时: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= attempts)
                    return new HttpPostResult(false, 0, "", ex.Message);
            }
            catch (Exception ex)
            {
                return new HttpPostResult(false, 0, "", ex.Message);
            }

            if (attempt < attempts)
            {
                await Task.Delay(delayMs * attempt);
            }
        }

        return new HttpPostResult(false, 0, "", $"{operationName} 请求失败");
    }

    private static void Log(string message)
    {
        if (!_enableLocalConsoleLog)
            return;

        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [WeChatMonitor] {message}";
        Console.WriteLine(line);
    }

    private static bool IsLocalConsoleLogEnabled()
    {
        string value = Environment.GetEnvironmentVariable(LocalConsoleLogEnvName) ?? "";
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static MonitorRuntimeConfig LoadConfig()
    {
        return new MonitorRuntimeConfig
        {
            ServerUrl = DEFAULT_SERVER_URL,
            ServerToken = DEFAULT_SERVER_TOKEN,
            PushIntervalSeconds = DEFAULT_PUSH_INTERVAL_SECONDS
        };
    }
}

public class WeChatMessage
{
    public string wxid { get; set; } = "";
    public string content { get; set; } = "";
    public long create_time { get; set; }
    public bool is_sender { get; set; }
    public string nickname { get; set; } = "";
    public string sender { get; set; } = "";
    public string avatar { get; set; } = "";
    public int msg_type { get; set; }
    public int msg_sub_type { get; set; }
    public string media_type { get; set; } = "";
    public string media_mime { get; set; } = "";
    public string media_name { get; set; } = "";
    public string media_data { get; set; } = "";
}

public class LocalSyncState
{
    public List<string> recent_message_keys { get; set; } = new();
    public string updated_at { get; set; } = "";
}

public class ClientIdentityState
{
    public string session_id { get; set; } = "";
    public string created_at { get; set; } = "";
}

internal sealed record ImageMedia(string Type, string Mime, string Name, string Base64);
internal sealed record CleanupResult(bool Success, int RemovedCount, int FailedCount, List<string> FailedPaths);
internal sealed record SupplementPushResult(
    bool Success,
    int ChangedCount,
    int StatusCode,
    string ErrorMessage,
    string ResponseText
);

internal sealed record HttpPostResult(
    bool Success,
    int StatusCode,
    string ResponseText,
    string ErrorMessage
);

internal sealed record DecryptRunResult(
    bool Success,
    bool IsLoggedIn,
    string DecryptDir,
    string WeChatDbDir,
    string ErrorMessage,
    bool HandledInProcess = false,
    string ProcessResult = "",
    int MessageCount = 0,
    int AddedCount = 0
);

internal sealed record DecryptAttemptOptions(string AttemptKind, int SoftTimeoutSeconds, int HardTimeoutSeconds);

internal sealed record RuntimeProcessEvent(string EventName, Dictionary<string, object?> Payload);

internal sealed record WeChatProcessSnapshot(int ProcessId, string ProcessName, string ExecutablePath, string ExecutableVersion);

internal sealed record WeChatRestartResult(bool Success, WeChatProcessSnapshot? Process, string ErrorMessage, long WaitElapsedMs = 0);

internal sealed class DecryptProcessOutputState
{
    private readonly object _sync = new();
    private readonly List<string> _lines = new();

    public bool HasLoginStatus { get; set; }

    public bool IsLoggedIn { get; set; }

    public bool HandledInProcess { get; set; }

    public string ProcessResult { get; set; } = "";

    public string ProcessErrorMessage { get; set; } = "";

    public bool HasConfirmedKeyMaterial { get; set; }

    public int MessageCount { get; set; }

    public int AddedCount { get; set; }

    public void AppendLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_sync)
        {
            _lines.Add(line);
        }
    }

    public string BuildOutput()
    {
        lock (_sync)
        {
            return string.Join(Environment.NewLine, _lines).Trim();
        }
    }

    public void ApplyRuntimeEvent(RuntimeProcessEvent runtimeEvent)
    {
        if (string.Equals(runtimeEvent.EventName, "client_memory_pipeline_result", StringComparison.Ordinal))
        {
            HandledInProcess = true;
            ProcessResult = ReadString(runtimeEvent.Payload, "result");
            MessageCount = ReadInt(runtimeEvent.Payload, "message_count");
            AddedCount = ReadInt(runtimeEvent.Payload, "added_count");
            ProcessErrorMessage = ReadString(runtimeEvent.Payload, "error_message");
            return;
        }

        if (string.Equals(runtimeEvent.EventName, "client_chatlog_key_result", StringComparison.Ordinal))
        {
            if (ReadBool(runtimeEvent.Payload, "success") || ReadBool(runtimeEvent.Payload, "has_key"))
                HasConfirmedKeyMaterial = true;
            return;
        }

        if (string.Equals(runtimeEvent.EventName, "client_disk_pipeline_started", StringComparison.Ordinal)
            || string.Equals(runtimeEvent.EventName, "client_memory_pipeline_started", StringComparison.Ordinal)
            || string.Equals(runtimeEvent.EventName, "client_wechat_decrypt_export_result", StringComparison.Ordinal))
        {
            HasConfirmedKeyMaterial = true;
            return;
        }

        if (string.Equals(runtimeEvent.EventName, "client_extract_failed", StringComparison.Ordinal))
        {
            string errorMessage = ReadString(runtimeEvent.Payload, "error_message");
            if (string.IsNullOrWhiteSpace(errorMessage))
                errorMessage = ReadString(runtimeEvent.Payload, "reason");
            if (!string.IsNullOrWhiteSpace(errorMessage))
                ProcessErrorMessage = errorMessage;
        }
    }

    private static string ReadString(Dictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
            return "";

        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.String => jsonElement.GetString() ?? "",
                JsonValueKind.Number => jsonElement.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => jsonElement.ToString(),
            };
        }

        return value.ToString() ?? "";
    }

    private static int ReadInt(Dictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
            return 0;

        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Number && jsonElement.TryGetInt32(out var intValue))
                return intValue;
            if (int.TryParse(jsonElement.ToString(), out var parsedValue))
                return parsedValue;
            return 0;
        }

        return int.TryParse(value.ToString(), out var result) ? result : 0;
    }

    private static bool ReadBool(Dictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
            return false;

        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(jsonElement.GetString(), out var parsed) && parsed,
                JsonValueKind.Number => jsonElement.TryGetInt32(out var intValue) && intValue != 0,
                _ => false,
            };
        }

        if (value is bool boolValue)
            return boolValue;

        if (value is int intValueDirect)
            return intValueDirect != 0;

        return bool.TryParse(value.ToString(), out var parsedBool)
            ? parsedBool
            : int.TryParse(value.ToString(), out var parsedInt) && parsedInt != 0;
    }
}

internal sealed record PushToServerResult(bool Success, int UploadedCount, int AddedCount, int StatusCode);

internal sealed class DecryptMeta
{
    [JsonPropertyName("db_dir")]
    public string DbDir { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("work_dir")]
    public string WorkDir { get; set; } = "";

    [JsonPropertyName("decrypt_key")]
    public string DecryptKey { get; set; } = "";

    [JsonPropertyName("wxid")]
    public string Wxid { get; set; } = "";
}

internal sealed class MonitorRuntimeConfig
{
    public string ServerUrl { get; set; } = "";
    public string ServerToken { get; set; } = "";
    public int PushIntervalSeconds { get; set; }
}
