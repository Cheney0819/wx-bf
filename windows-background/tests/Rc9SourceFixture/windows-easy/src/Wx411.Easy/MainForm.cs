using System.ComponentModel;
using System.Diagnostics;
using Wx411.Core;
using Wx411.Core.Windows;

namespace Wx411.Easy;

/// <summary>
/// Small, dependency-free WinForms front end for the .NET recovery core.
/// There is intentionally no designer file: keeping the form in one source
/// file makes the self-contained single-file publish reproducible.
/// </summary>
internal sealed class MainForm : Form
{
    private const string DisplayVersion = "1.5-dev";

    private readonly CallpointCaptureRecoveryService _captureRecoveryService;
    private readonly PendingCaptureVault _pendingCaptureVault;
    private readonly EvidenceSessionRecorder _evidenceRecorder;
    private readonly EvidenceBundleService _evidenceBundleService;

    private readonly ComboBox _processCombo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FormattingEnabled = true,
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        Height = 30,
    };

    private readonly ComboBox _databaseCombo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FormattingEnabled = true,
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        Height = 30,
    };

    private readonly TextBox _outputDirectory = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = SystemColors.Window,
    };

    private readonly Button _refreshButton = new()
    {
        Text = "刷新列表",
        AutoSize = true,
        MinimumSize = new Size(92, 32),
        Margin = new Padding(4, 0, 0, 0),
    };

    private readonly Button _browseOutputButton = new()
    {
        Text = "选择目录…",
        AutoSize = true,
        MinimumSize = new Size(92, 32),
        Margin = new Padding(4, 0, 0, 0),
    };

    private readonly Button _browseDatabaseButton = new()
    {
        Text = "选择数据库…",
        AutoSize = true,
        MinimumSize = new Size(108, 32),
        Margin = new Padding(4, 0, 0, 0),
    };

    private readonly Button _captureButton = new()
    {
        Text = "定位 key 并解密",
        AutoSize = true,
        MinimumSize = new Size(138, 38),
        BackColor = Color.FromArgb(31, 112, 210),
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        Margin = new Padding(0, 0, 8, 0),
    };

    private readonly Button _cancelButton = new()
    {
        Text = "取消",
        AutoSize = true,
        MinimumSize = new Size(92, 38),
        Enabled = false,
        Margin = new Padding(0, 0, 8, 0),
    };

    private readonly Button _openButton = new()
    {
        Text = "打开输出目录",
        AutoSize = true,
        MinimumSize = new Size(120, 38),
        Enabled = false,
    };

    private readonly Button _evidenceButton = new()
    {
        Text = "导出证据包",
        AutoSize = true,
        MinimumSize = new Size(118, 38),
    };

    private readonly ProgressBar _progress = new()
    {
        Dock = DockStyle.Fill,
        Minimum = 0,
        Maximum = 100,
        Style = ProgressBarStyle.Continuous,
        Height = 18,
    };

    private readonly Label _statusLabel = new()
    {
        AutoSize = true,
        Text = "准备就绪",
        Anchor = AnchorStyles.Left,
        Margin = new Padding(8, 0, 0, 0),
    };

    private readonly RichTextBox _log = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        DetectUrls = true,
        BackColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point),
        HideSelection = false,
    };

    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _refreshCancellation;
    private TaskCompletionSource? _refreshCompletion;
    private bool _isBusy;
    private string? _lastOutputPath;

    public MainForm(
        Func<ICallpointCaptureBackend>? captureBackendFactory = null,
        PendingCaptureVault? pendingCaptureVault = null,
        EvidenceSessionRecorder? evidenceRecorder = null,
        EvidenceBundleService? evidenceBundleService = null)
    {
        var captureBackend = captureBackendFactory ?? (() => new DebugCaptureBackend());
        var vault = pendingCaptureVault ?? new PendingCaptureVault(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wx411Easy",
                "pending-captures"),
            new WindowsDpapiProtector());
        _pendingCaptureVault = vault;
        _captureRecoveryService = new CallpointCaptureRecoveryService(
            captureBackend,
            _pendingCaptureVault);
        _evidenceRecorder = evidenceRecorder ?? new EvidenceSessionRecorder(DisplayVersion);
        _evidenceBundleService = evidenceBundleService ?? new EvidenceBundleService();

        Text = $"本地数据读取 · 4.1.x · 精准定位版 {DisplayVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 610);
        Size = new Size(980, 700);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = SystemColors.Control;

        _outputDirectory.Text = GetDefaultOutputDirectory();
        BuildLayout();

        _refreshButton.Click += async (_, _) => await RefreshSourcesAsync();
        _browseDatabaseButton.Click += (_, _) => BrowseDatabase();
        _browseOutputButton.Click += (_, _) => BrowseOutputDirectory();
        _captureButton.Click += async (_, _) => await StartCaptureAsync();
        _cancelButton.Click += (_, _) => CancelRecovery();
        _openButton.Click += (_, _) => OpenOutputDirectory();
        _evidenceButton.Click += async (_, _) => await ExportEvidenceAsync();
        FormClosing += (_, _) =>
        {
            _runCancellation?.Cancel();
            _refreshCancellation?.Cancel();
        };

        Shown += async (_, _) => await RefreshSourcesAsync();
    }

    private void BuildLayout()
    {
        var title = new Label
        {
            Text = $"本地数据一键读取 {DisplayVersion}",
            Font = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold, GraphicsUnit.Point),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 3),
        };

        var subtitle = new Label
        {
            Text = "精准监听 key 设置事件；命中后批量解密并检查 SQLite 完整性。",
            AutoSize = true,
            ForeColor = Color.FromArgb(80, 80, 80),
            Margin = new Padding(0, 0, 0, 12),
        };

        var intro = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = true,
            AutoSize = true,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        intro.Controls.Add(title);
        intro.Controls.Add(subtitle);

        var sourceTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            AutoSize = true,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        sourceTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        sourceTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        sourceTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        sourceTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        sourceTable.Controls.Add(MakeFieldLabel("目标进程"), 0, 0);
        sourceTable.Controls.Add(_processCombo, 1, 0);
        sourceTable.Controls.Add(_refreshButton, 2, 0);
        sourceTable.Controls.Add(MakeFieldLabel("数据库"), 0, 1);
        sourceTable.Controls.Add(_databaseCombo, 1, 1);
        sourceTable.Controls.Add(_browseDatabaseButton, 2, 1);
        sourceTable.Controls.Add(MakeFieldLabel("输出目录"), 0, 2);
        sourceTable.Controls.Add(_outputDirectory, 1, 2);
        sourceTable.Controls.Add(_browseOutputButton, 2, 2);

        var progressTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 14, 0, 8),
            Padding = new Padding(0),
        };
        progressTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progressTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        progressTable.Controls.Add(_progress, 0, 0);
        progressTable.Controls.Add(_statusLabel, 1, 0);

        var actionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 8),
            Padding = new Padding(0),
        };
        actionPanel.Controls.Add(_captureButton);
        actionPanel.Controls.Add(_cancelButton);
        actionPanel.Controls.Add(_openButton);
        actionPanel.Controls.Add(_evidenceButton);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(24, 20, 24, 18),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(sourceTable, 0, 1);
        root.Controls.Add(progressTable, 0, 2);
        root.Controls.Add(actionPanel, 0, 3);
        root.Controls.Add(_log, 0, 4);
        Controls.Add(root);

        AcceptButton = _captureButton;
        CancelButton = _cancelButton;
    }

    private static Label MakeFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 0, 8, 0),
    };

    private async Task RefreshSourcesAsync()
    {
        if (IsDisposed || _runCancellation is not null || _isBusy) return;

        var previousCompletion = _refreshCompletion;
        if (previousCompletion is not null)
        {
            _refreshCancellation?.Cancel();
            await previousCompletion.Task;
            if (IsDisposed || _runCancellation is not null || _isBusy) return;
        }

        var refreshCancellation = new CancellationTokenSource();
        var refreshCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshCancellation = refreshCancellation;
        _refreshCompletion = refreshCompletion;
        var token = refreshCancellation.Token;
        SetSourceControlsEnabled(false);
        SetStatus("正在查找目标进程和数据文件…", 0);
        AppendLog("开始查找本机目标进程和默认数据目录…");

        try
        {
            var result = await Task.Run(DiscoverSources, token);
            if (token.IsCancellationRequested) return;

            var oldProcess = _processCombo.SelectedItem as ProcessChoice;
            var oldDatabase = (_databaseCombo.SelectedItem as DatabaseChoice)?.Path;

            _processCombo.BeginUpdate();
            try
            {
                _processCombo.Items.Clear();
                _processCombo.Items.Add(ProcessChoice.Automatic);
                foreach (var item in result.Processes) _processCombo.Items.Add(item);
            }
            finally { _processCombo.EndUpdate(); }

            _databaseCombo.BeginUpdate();
            try
            {
                _databaseCombo.Items.Clear();
                foreach (var item in result.Databases) _databaseCombo.Items.Add(item);
                if (!string.IsNullOrWhiteSpace(oldDatabase) && File.Exists(oldDatabase) &&
                    !result.Databases.Any(item =>
                        string.Equals(item.Path, oldDatabase, StringComparison.OrdinalIgnoreCase)))
                {
                    var info = new FileInfo(oldDatabase);
                    _databaseCombo.Items.Insert(0, new DatabaseChoice(info.FullName, info.Length));
                }
            }
            finally { _databaseCombo.EndUpdate(); }

            SelectProcess(oldProcess);
            SelectDatabase(oldDatabase);
            if (_processCombo.SelectedIndex < 0 && _processCombo.Items.Count > 0) _processCombo.SelectedIndex = 0;
            if (_databaseCombo.SelectedIndex < 0 && _databaseCombo.Items.Count > 0) _databaseCombo.SelectedIndex = 0;

            AppendLog($"找到 {result.Processes.Count} 个目标进程，{result.Databases.Count} 个数据文件。");
            if (result.Processes.Count == 0)
                AppendLog("提示：没有找到目标进程。可直接定位并等待启动，或启动目标程序后点“刷新列表”。");
            if (result.Databases.Count == 0)
                AppendLog("提示：没有找到完整的 4096-byte 分页数据文件；可检查是否使用了自定义数据目录。");
            SetStatus("准备就绪", 0);
        }
        catch (OperationCanceledException)
        {
            AppendLog("列表刷新已取消。");
        }
        catch (Exception ex)
        {
            AppendLog("列表刷新失败：" + FormatException(ex));
            SetStatus("列表刷新失败", 0);
        }
        finally
        {
            SetSourceControlsEnabled(true);
            if (ReferenceEquals(_refreshCancellation, refreshCancellation))
            {
                refreshCancellation.Dispose();
                _refreshCancellation = null;
            }
            if (ReferenceEquals(_refreshCompletion, refreshCompletion))
                _refreshCompletion = null;
            refreshCompletion.TrySetResult();
        }
    }

    private void SelectProcess(ProcessChoice? selected)
    {
        if (selected is null) return;
        for (var i = 0; i < _processCombo.Items.Count; i++)
        {
            if (_processCombo.Items[i] is ProcessChoice item &&
                item.ScanAll == selected.ScanAll && item.Pid == selected.Pid)
            {
                _processCombo.SelectedIndex = i;
                return;
            }
        }
    }

    private void BrowseDatabase()
    {
        var current = (_databaseCombo.SelectedItem as DatabaseChoice)?.Path;
        var initialDirectory = !string.IsNullOrWhiteSpace(current)
            ? Path.GetDirectoryName(current)
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        using var dialog = new OpenFileDialog
        {
            Title = "选择本地数据文件",
            Filter = "数据文件 (*.db)|*.db|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var fullPath = Path.GetFullPath(dialog.FileName);
        for (var i = 0; i < _databaseCombo.Items.Count; i++)
        {
            if (_databaseCombo.Items[i] is DatabaseChoice existing &&
                string.Equals(existing.Path, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                _databaseCombo.SelectedIndex = i;
                return;
            }
        }

        var info = new FileInfo(fullPath);
        _databaseCombo.Items.Insert(0, new DatabaseChoice(info.FullName, info.Length));
        _databaseCombo.SelectedIndex = 0;
        AppendLog("手动选择数据库：" + info.FullName);
    }

    private void SelectDatabase(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        for (var i = 0; i < _databaseCombo.Items.Count; i++)
        {
            if (_databaseCombo.Items[i] is DatabaseChoice item &&
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                _databaseCombo.SelectedIndex = i;
                return;
            }
        }
    }

    private void BrowseOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择可读副本保存目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_outputDirectory.Text)
                ? _outputDirectory.Text
                : GetDefaultOutputDirectory(),
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputDirectory.Text = dialog.SelectedPath;
            AppendLog("输出目录：" + dialog.SelectedPath);
        }
    }

    private void CancelRecovery()
    {
        if (_runCancellation is null) return;
        _cancelButton.Enabled = false;
        _statusLabel.Text = "正在取消…";
        _runCancellation.Cancel();
        AppendLog("正在取消，请稍候…");
    }

    private void OpenOutputDirectory()
    {
        var path = _lastOutputPath;
        if (string.IsNullOrWhiteSpace(path)) path = _outputDirectory.Text;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{fullPath.Replace("\"", string.Empty)}\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                Directory.CreateDirectory(fullPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = fullPath,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            AppendLog("打开目录失败：" + FormatException(ex));
        }
    }

    private async Task ExportEvidenceAsync()
    {
        if (_runCancellation is not null || _isBusy) return;
        var outputDirectory = _outputDirectory.Text.Trim();
        if (outputDirectory.Length == 0)
        {
            ShowInputError("请选择输出目录。");
            return;
        }

        SetBusy(true, canCancel: false);
        SetStatus("正在生成证据包…", 0);
        try
        {
            var refreshCompletion = _refreshCompletion;
            _refreshCancellation?.Cancel();
            if (refreshCompletion is not null)
                await refreshCompletion.Task;

            var result = await _evidenceBundleService.ExportAsync(
                _evidenceRecorder.Snapshot(),
                _log.Text,
                outputDirectory,
                CancellationToken.None);
            _lastOutputPath = result.BundlePath;
            _openButton.Enabled = true;
            SetStatus($"证据包已生成：{result.Assessment.Overall}", 100);
            AppendLog($"证据包已生成：{result.BundlePath}；总结果={result.Assessment.Overall}");
            MessageBox.Show(
                this,
                $"证据包已生成：{result.BundlePath}{Environment.NewLine}{Environment.NewLine}" +
                $"总结果：{result.Assessment.Overall}",
                "证据包已生成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            OpenOutputDirectory();
        }
        catch (Exception ex)
        {
            SetStatus("证据包生成失败", 0);
            AppendLog("证据包生成失败：" + FormatException(ex));
            MessageBox.Show(
                this,
                FormatException(ex),
                "证据包生成失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<EvidenceOperationHandle?> BeginEvidenceOperationAsync(
        EvidenceOperationKind kind,
        RecoveryProcessSelection process,
        string sourcePath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _evidenceRecorder.BeginAsync(
                kind,
                process,
                sourcePath,
                outputDirectory,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog("证据记录启动失败，主操作继续：" + FormatException(ex));
            return null;
        }
    }

    private async Task CompleteEvidenceOperationAsync(
        EvidenceOperationHandle? handle,
        EvidenceOperationOutcome outcome,
        IEnumerable<string> outputPaths,
        Exception? error)
    {
        if (handle is null) return;
        try
        {
            await _evidenceRecorder.CompleteAsync(
                handle,
                outcome,
                outputPaths,
                error,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppendLog("证据记录结束失败，主操作结果不受影响：" + FormatException(ex));
        }
    }

    private void SetBusy(bool busy, bool canCancel = true)
    {
        _isBusy = busy;
        _captureButton.Enabled = !busy;
        _cancelButton.Enabled = busy && canCancel;
        _evidenceButton.Enabled = !busy;
        _refreshButton.Enabled = !busy;
        _browseDatabaseButton.Enabled = !busy;
        _browseOutputButton.Enabled = !busy;
        _processCombo.Enabled = !busy;
        _databaseCombo.Enabled = !busy;
        _progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
    }

    private void SetSourceControlsEnabled(bool enabled)
    {
        if (_runCancellation is not null || _isBusy) return;
        _refreshButton.Enabled = enabled;
        _browseDatabaseButton.Enabled = enabled;
        _browseOutputButton.Enabled = enabled;
        _processCombo.Enabled = enabled;
        _databaseCombo.Enabled = enabled;
        _captureButton.Enabled = enabled;
        _evidenceButton.Enabled = enabled;
    }

    private void SetStatus(string text, int percent)
    {
        _statusLabel.Text = text;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = Math.Clamp(percent, _progress.Minimum, _progress.Maximum);
    }

    private void AppendLog(string message)
    {
        if (IsDisposed) return;
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        _log.AppendText(line);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private static string FormatException(Exception ex)
    {
        if (ex is AggregateException aggregate) ex = aggregate.GetBaseException();
        if (ex is Win32Exception win32 && !string.IsNullOrWhiteSpace(win32.Message))
            return $"{win32.Message} (错误码 {win32.NativeErrorCode})";
        return ex.Message;
    }

    private void ShowInputError(string message)
    {
        AppendLog(message);
        MessageBox.Show(this, message, "请检查输入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ShowFailureHint(Exception ex)
    {
        var message = FormatException(ex);
        if (ex is PageAuthenticationException authentication)
        {
            message += Environment.NewLine + Environment.NewLine +
                       "数据库内存快照已通过全文件一致性复核，但完整页认证失败。" +
                       $"失败页 {authentication.Report.FailedPageCount}/{authentication.Report.PageCount}：" +
                       string.Join(", ", authentication.Report.FailedPages) + "。" +
                       Environment.NewLine +
                       "请完全退出目标程序后取得静态数据库副本再试；若仍在相同页失败，原文件可能已损坏。";
        }

        if (ContainsAccessDenied(ex))
        {
            message += Environment.NewLine + Environment.NewLine +
                       "Windows 拒绝了进程读取。请关闭本程序后右键“以管理员身份运行”，再保持目标程序运行并重试。";
        }
        else if (message.Contains("没有找到", StringComparison.Ordinal))
        {
            message += Environment.NewLine + Environment.NewLine +
                       "请确认目标程序正在运行，并在下拉框中选择对应数据文件。";
        }
        MessageBox.Show(this, message, "处理失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static bool ContainsAccessDenied(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is UnauthorizedAccessException ||
                exception is Win32Exception { NativeErrorCode: 5 }) return true;
            exception = exception.InnerException;
        }
        return false;
    }

    private static SourceDiscoveryResult DiscoverSources()
    {
        var processes = TargetProcessDiscovery.Discover()
            .Select(item => new ProcessChoice(item.Pid, item.Name))
            .ToArray();
        var databases = DatabaseSourceDiscovery.Discover()
            .Select(item => new DatabaseChoice(item.Path, item.Length))
            .ToArray();
        return new SourceDiscoveryResult(processes, databases);
    }

    private static string GetDefaultOutputDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            desktop = Path.Combine(profile, "Desktop");
        }
        return Path.Combine(desktop, "本地数据输出");
    }

    private sealed record ProcessChoice(int? Pid, string Name, bool ScanAll = false)
    {
        public static readonly ProcessChoice Automatic = new(null, "自动捕获全部 Weixin.exe", ScanAll: true);

        public override string ToString() => ScanAll ? Name : $"{Name}  ·  PID {Pid}";
    }

    private sealed record DatabaseChoice(string Path, long Length)
    {
        public override string ToString()
        {
            var size = Length >= 1024 * 1024
                ? $"{Length / 1024d / 1024d:0.0} MB"
                : $"{Length / 1024d:0} KB";
            return $"{System.IO.Path.GetFileName(Path)}  ·  {size}  ·  {Shorten(Path)}";
        }

        private static string Shorten(string path)
        {
            const int max = 82;
            if (path.Length <= max) return path;
            return path[..28] + "…" + path[^50..];
        }
    }

    private sealed record SourceDiscoveryResult(
        IReadOnlyList<ProcessChoice> Processes,
        IReadOnlyList<DatabaseChoice> Databases);


    private async Task StartCaptureAsync()
    {
        if (_runCancellation is not null) return;

        if (_processCombo.SelectedItem is not ProcessChoice process)
        {
            ShowInputError("没有选中的目标进程。请点“刷新列表”，或保留自动捕获并直接开始等待。");
            return;
        }

        if (!process.ScanAll && process.Pid is null)
        {
            ShowInputError("key 定位需要选择一个具体的目标进程 PID，或保留“自动捕获全部 Weixin.exe”。");
            return;
        }

        if (_databaseCombo.SelectedItem is not DatabaseChoice database)
        {
            ShowInputError("没有选中的数据库。");
            return;
        }

        var databases = _databaseCombo.Items
            .OfType<DatabaseChoice>()
            .Prepend(database)
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => new DatabaseSource(item.Path, item.Length))
            .ToArray();
        var selectedDatabase = new DatabaseSource(database.Path, database.Length);

        if (!DebugCaptureBackend.IsSupported)
        {
            ShowInputError("key 定位仅支持 64 位 Windows。");
            return;
        }

        var outputDirectory = _outputDirectory.Text.Trim();
        if (outputDirectory.Length == 0)
        {
            ShowInputError("请选择输出目录。");
            return;
        }

        var captureProcess = new RecoveryProcessSelection(process.Pid, process.Name, process.ScanAll);

        _runCancellation = new CancellationTokenSource();
        var token = _runCancellation.Token;
        var progress = new Progress<RecoveryProgress>(update =>
        {
            if (IsDisposed) return;
            SetStatus(update.Message, update.Percent);
            if (!string.IsNullOrWhiteSpace(update.Log)) AppendLog(update.Log);
        });

        SetBusy(true);
        _lastOutputPath = null;
        _openButton.Enabled = false;
        AppendLog("=== 定位 key 并解密 ===");
        AppendLog(process.ScanAll
            ? $"目标 PID: 自动捕获全部  数据库: 全部发现项 {databases.Length} 个；PID 优先库: {database.Path}"
            : $"目标 PID: {process.Pid}  数据库: 全部发现项 {databases.Length} 个；PID 优先库: {database.Path}");

        var pendingTicketIdsBefore = TrySnapshotPendingCaptureTicketIds();
        EvidenceOperationHandle? evidenceOperation = null;
        try
        {
            evidenceOperation = await BeginEvidenceOperationAsync(
                EvidenceOperationKind.PreciseCapture,
                captureProcess,
                database.Path,
                outputDirectory,
                token);
            var result = await Task.Run(
                () => _captureRecoveryService.CaptureAndDecryptAsync(
                    captureProcess,
                    selectedDatabase,
                    databases,
                    outputDirectory,
                    progress,
                    token),
                token);

            await CompleteEvidenceOperationAsync(
                evidenceOperation,
                EvidenceOperationOutcome.Success,
                result.OutputPaths,
                error: null);

            if (evidenceOperation is not null)
            {
                try
                {
                    _evidenceRecorder.RecordPendingCaptureFollowUp(
                        evidenceOperation,
                        result.LoadedPendingCaptureTicketIds);
                }
                catch (Exception ex)
                {
                    AppendLog("证据警告：待处理票据后续关联失败：" + FormatException(ex));
                }
            }

            _lastOutputPath = result.OutputPaths.FirstOrDefault();
            _openButton.Enabled = !string.IsNullOrWhiteSpace(_lastOutputPath);
            SetStatus($"[7/7] 完成：{result.OutputPaths.Count} 个副本", 100);
            foreach (var match in result.Matches)
                AppendLog($"数据库 key 命中：{match.DatabaseId}；观察点={match.CallpointName}；profile={match.ProfileMatch.Profile.Name}");
            foreach (var outputPath in result.OutputPaths)
                AppendLog($"解密[7/7] 完成：{outputPath}");
            foreach (var unmatchedPath in result.UnmatchedDatabasePaths)
                AppendLog($"未命中数据库：{unmatchedPath}");
            foreach (var failedPath in result.FailedDatabasePaths)
                AppendLog($"已命中但输出失败：{failedPath}");

            if (MessageBox.Show(this,
                    $"已生成 {result.OutputPaths.Count} 个数据库副本；" +
                    $"未命中/未读取 {result.UnmatchedDatabasePaths.Count} 个，输出失败 {result.FailedDatabasePaths.Count} 个。" +
                    Environment.NewLine + Environment.NewLine +
                    "现在打开输出目录？",
                    "[7/7] 完成",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information) == DialogResult.Yes)
            {
                OpenOutputDirectory();
            }
        }
        catch (OperationCanceledException)
        {
            await CompleteEvidenceOperationAsync(
                evidenceOperation,
                EvidenceOperationOutcome.Cancelled,
                Array.Empty<string>(),
                error: null);
            var pendingTicketIdsAfter = TrySnapshotPendingCaptureTicketIds();
            if (evidenceOperation is not null)
            {
                try
                {
                    if (pendingTicketIdsBefore is null || pendingTicketIdsAfter is null)
                    {
                        _evidenceRecorder.RecordCancelledPendingTickets(evidenceOperation, null);
                    }
                    else
                    {
                        var createdTicketIds = pendingTicketIdsAfter
                            .Except(pendingTicketIdsBefore, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        _evidenceRecorder.RecordCancelledPendingTickets(evidenceOperation, createdTicketIds);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("证据警告：待处理票据取消关联失败：" + FormatException(ex));
                }
            }
            SetStatus("已取消", 0);
            AppendLog("操作已取消。");
        }
        catch (Exception ex)
        {
            await CompleteEvidenceOperationAsync(
                evidenceOperation,
                EvidenceOperationOutcome.Failed,
                Array.Empty<string>(),
                ex);
            SetStatus("失败", 0);
            AppendLog("失败：" + FormatException(ex));
            ShowFailureHint(ex);
        }
        finally
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
            SetBusy(false);
        }
    }

    private IReadOnlyList<string>? TrySnapshotPendingCaptureTicketIds()
    {
        try
        {
            return _pendingCaptureVault.SnapshotRecordIds();
        }
        catch (Exception ex)
        {
            AppendLog("证据警告：待处理票据元数据枚举失败：" + FormatException(ex));
            return null;
        }
    }

}
