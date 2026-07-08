using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.Wpf.Services;
using Forms = System.Windows.Forms;

namespace DesktopPet.Wpf;

public partial class MainWindow : Window
{
    private readonly PetEngine _engine = new();
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _autonomyTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _energyTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly List<BitmapSource> _frames = [];
    private readonly Forms.NotifyIcon _notifyIcon = new();
    private readonly SpeechSessionController _speechSessionController = new();
    private readonly AudioCaptureService _audioCaptureService = new();
    private readonly AudioPlaybackService _audioPlaybackService = new();
    private System.Windows.Point _dragStart;
    private bool _isDraggingWindow;
    private bool _dragVisualShown;
    private bool _suppressClickRelease;
    private string? _speechOverrideText;
    private string? _emotionOverrideText;
    private DateTime _speechOverrideUntil;
    private ChatInputWindow? _chatInputWindow;
    private CancellationTokenSource? _activeChatCts;

    public MainWindow()
    {
        InitializeComponent();
        LoadFrames();
        ApplyVisual(_engine.Tick());

        Left = SystemParameters.WorkArea.Right - Width - 48;
        Top = SystemParameters.WorkArea.Bottom - Height - 48;

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonUp += OnMouseRightButtonUp;

        _renderTimer.Tick += (_, _) =>
        {
            if (_isDraggingWindow)
                return;

            ApplyVisual(_engine.Tick());
        };
        _autonomyTimer.Tick += (_, _) =>
        {
            if (_isDraggingWindow)
                return;

            ApplyVisual(_engine.AutoBehavior());
        };
        _energyTimer.Tick += (_, _) => _engine.RecoverEnergy();

        _renderTimer.Start();
        _autonomyTimer.Start();
        _energyTimer.Start();
        ApplyVisual(_engine.AutoBehavior());

        ConfigureTray();
    }

    protected override void OnClosed(EventArgs e)
    {
        _activeChatCts?.Cancel();
        _activeChatCts?.Dispose();
        _audioCaptureService.Dispose();
        _audioPlaybackService.Dispose();
        if (_chatInputWindow is not null)
        {
            _chatInputWindow.Submitted -= ChatInputWindowOnSubmitted;
            _chatInputWindow.RecordingToggled -= ChatInputWindowOnRecordingToggled;
            _chatInputWindow.Close();
            _chatInputWindow = null;
        }
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.OnClosed(e);
    }

    private void ConfigureTray()
    {
        _notifyIcon.Text = "桌宠助手";
        _notifyIcon.Icon = SystemIcons.Information;
        _notifyIcon.Visible = true;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("跟我聊天", null, (_, _) => OpenChatInputWindow());
        menu.Items.Add("陪我一下", null, (_, _) => ApplyVisual(_engine.Interact("happy")));
        menu.Items.Add("安静陪伴", null, (_, _) => ApplyVisual(_engine.Interact("idle")));
        menu.Items.Add("先去睡觉", null, (_, _) => ApplyVisual(_engine.Interact("sleep")));
        menu.Items.Add("退出", null, (_, _) => Close());
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) =>
        {
            Show();
            Activate();
        };
    }

    private void LoadFrames()
    {
        var spritePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sprite-sheet.png");
        if (!File.Exists(spritePath))
        {
            return;
        }

        var bitmap = new BitmapImage(new Uri(spritePath));
        const int cols = 4;
        const int rows = 4;

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var frameIndex = (row * cols) + col;
                var x0 = (int)Math.Round(bitmap.PixelWidth * col / (double)cols);
                var x1 = (int)Math.Round(bitmap.PixelWidth * (col + 1) / (double)cols);
                var y0 = (int)Math.Round(bitmap.PixelHeight * row / (double)rows);
                var y1 = (int)Math.Round(bitmap.PixelHeight * (row + 1) / (double)rows);
                var frame = new CroppedBitmap(bitmap, new Int32Rect(x0, y0, x1 - x0, y1 - y0));
                _frames.Add(CleanupFloatingArtifacts(frame, frameIndex));
            }
        }
    }

    private static BitmapSource CleanupFloatingArtifacts(BitmapSource source, int frameIndex)
    {
        var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var writable = new WriteableBitmap(formatted);
        var width = writable.PixelWidth;
        var height = writable.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[height * stride];
        writable.CopyPixels(pixels, stride, 0);

        var visited = new bool[width * height];
        var components = new List<ComponentInfo>();
        var maxArtifactArea = frameIndex == 9 ? 600 : 120;
        var minVerticalGap = frameIndex == 9 ? 8 : 12;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                if (visited[index] || GetAlpha(pixels, stride, x, y) == 0)
                    continue;

                var queue = new Queue<(int X, int Y)>();
                var points = new List<(int X, int Y)>();
                var minY = y;
                var maxY = y;

                visited[index] = true;
                queue.Enqueue((x, y));

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    points.Add(current);
                    minY = Math.Min(minY, current.Y);
                    maxY = Math.Max(maxY, current.Y);

                    TryVisit(current.X + 1, current.Y);
                    TryVisit(current.X - 1, current.Y);
                    TryVisit(current.X, current.Y + 1);
                    TryVisit(current.X, current.Y - 1);
                }

                components.Add(new ComponentInfo(points, minY, maxY));

                void TryVisit(int nextX, int nextY)
                {
                    if (nextX < 0 || nextX >= width || nextY < 0 || nextY >= height)
                        return;

                    var nextIndex = (nextY * width) + nextX;
                    if (visited[nextIndex] || GetAlpha(pixels, stride, nextX, nextY) == 0)
                        return;

                    visited[nextIndex] = true;
                    queue.Enqueue((nextX, nextY));
                }
            }
        }

        if (components.Count <= 1)
        {
            writable.Freeze();
            return writable;
        }

        var mainComponent = components.OrderByDescending(component => component.Area).First();
        foreach (var component in components)
        {
            var isTiny = component.Area < maxArtifactArea;
            var isClearlyAbove = component.MaxY < mainComponent.MinY - minVerticalGap;
            if (!isTiny || !isClearlyAbove)
                continue;

            foreach (var point in component.Points)
            {
                var pixelIndex = (point.Y * stride) + (point.X * 4);
                pixels[pixelIndex] = 0;
                pixels[pixelIndex + 1] = 0;
                pixels[pixelIndex + 2] = 0;
                pixels[pixelIndex + 3] = 0;
            }
        }

        writable.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        writable.Freeze();
        return writable;
    }

    private static byte GetAlpha(byte[] pixels, int stride, int x, int y)
    {
        return pixels[(y * stride) + (x * 4) + 3];
    }

    private void ApplyVisual(PetVisual visual)
    {
        if (visual.FrameIndex >= 0 && visual.FrameIndex < _frames.Count)
        {
            PetImage.Source = _frames[visual.FrameIndex];
        }

        SpeechText.Text = visual.Speech;
        EmotionText.Text = visual.Emotion;
        PropText.Text = visual.Prop.Text;

        var bubbleBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(visual.Brushes.BubbleHex)!;
        var textBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(visual.Brushes.TextHex)!;
        var emotionBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(visual.Brushes.EmotionHex)!;
        var propBackgroundBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(visual.Prop.BackgroundHex)!;
        var propForegroundBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(visual.Prop.ForegroundHex)!;

        SpeechBubble.Background = bubbleBrush;
        SpeechText.Foreground = textBrush;
        EmotionText.Foreground = emotionBrush;
        PropBadge.Background = propBackgroundBrush;
        PropText.Foreground = propForegroundBrush;

        ApplySpeechOverrideIfNeeded();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            _suppressClickRelease = true;
            ApplyVisual(_engine.Interact(_engine.IsSleeping ? "idle" : "sleep"));
            e.Handled = true;
            return;
        }

        _suppressClickRelease = false;
        _dragStart = e.GetPosition(this);
        _isDraggingWindow = false;
        _dragVisualShown = false;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (!_isDraggingWindow)
        {
            var delta = current - _dragStart;
            if (Math.Abs(delta.X) >= 8 || Math.Abs(delta.Y) >= 8)
            {
                _isDraggingWindow = true;
            }
        }

        if (_isDraggingWindow && !_dragVisualShown)
        {
            _dragVisualShown = true;
            ApplyVisual(_engine.Interact("drag"));
        }

        var screen = PointToScreen(current);
        Left = screen.X - _dragStart.X;
        Top = screen.Y - _dragStart.Y;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (_suppressClickRelease)
        {
            _suppressClickRelease = false;
        }
        else if (!_isDraggingWindow)
        {
            var action = Random.Shared.Next(0, 6) switch
            {
                0 => "happy",
                1 => "wave",
                2 => "shy",
                3 => "listen",
                4 => "surprised",
                _ => "dizzy",
            };
            ApplyVisual(_engine.Interact(action));
        }

        _isDraggingWindow = false;
        _dragVisualShown = false;
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var action = Random.Shared.Next(0, 5) switch
        {
            0 => "wave",
            1 => "surprised",
            2 => "happy",
            3 => "shy",
            _ => "listen",
        };
        ApplyVisual(_engine.Interact(action));
    }

    private void OpenChatInputWindow()
    {
        if (_chatInputWindow is not null)
        {
            _chatInputWindow.Show();
            _chatInputWindow.Activate();
            return;
        }

        _chatInputWindow = new ChatInputWindow();
        _chatInputWindow.Submitted += ChatInputWindowOnSubmitted;
        _chatInputWindow.RecordingToggled += ChatInputWindowOnRecordingToggled;
        _chatInputWindow.Closed += (_, _) =>
        {
            if (_chatInputWindow is not null)
            {
                _chatInputWindow.Submitted -= ChatInputWindowOnSubmitted;
                _chatInputWindow.RecordingToggled -= ChatInputWindowOnRecordingToggled;
            }
            _chatInputWindow = null;
        };
        _chatInputWindow.Show();
        _chatInputWindow.Activate();
    }

    private async void ChatInputWindowOnSubmitted(object? sender, string text)
    {
        if (_activeChatCts is not null)
            return;

        _activeChatCts = new CancellationTokenSource();
        _chatInputWindow?.SetBusyState(true, "我正在认真想回复哦");

        SetSpeechOverride("我在认真听妈妈说话哦", TimeSpan.FromSeconds(8), "◔");
        ApplyVisual(_engine.Interact("listen"));

        try
        {
            await Task.Delay(250, _activeChatCts.Token);
            SetSpeechOverride("让我想一下呀", TimeSpan.FromSeconds(12), "…");
            ApplyVisual(_engine.Interact("blink"));

            var result = await _speechSessionController.SendTextAsync(text, _activeChatCts.Token);
            SetSpeechOverride(result.ReplyText, TimeSpan.FromSeconds(result.Success ? 20 : 14));
            ApplyVisual(_engine.Interact(result.SuggestedAction));
            _ = PlayReplyAudioIfNeededAsync(result.AudioFilePath);
        }
        catch (OperationCanceledException)
        {
            SetSpeechOverride("这次先停住啦，妈妈等会儿再叫我就好。", TimeSpan.FromSeconds(10), "!");
            ApplyVisual(_engine.Interact("surprised"));
        }
        finally
        {
            _activeChatCts?.Dispose();
            _activeChatCts = null;
            _chatInputWindow?.SetBusyState(false, "按 Enter 发送，Shift+Enter 换行");
        }
    }

    private async void ChatInputWindowOnRecordingToggled(object? sender, bool shouldStartRecording)
    {
        if (shouldStartRecording)
        {
            BeginVoiceCapture();
            return;
        }

        await EndVoiceCaptureAndReplyAsync();
    }

    private async void MicButton_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (_audioCaptureService.IsRecording)
        {
            await EndVoiceCaptureAndReplyAsync();
            return;
        }

        BeginVoiceCapture();
    }

    private async Task PlayReplyAudioIfNeededAsync(string audioFilePath)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath))
            return;

        try
        {
            await _audioPlaybackService.PlayAndDeleteAsync(audioFilePath);
        }
        catch
        {
        }
    }

    private void BeginVoiceCapture()
    {
        if (_activeChatCts is not null || _audioCaptureService.IsRecording)
            return;

        try
        {
            int maxRecordSeconds = Models.MiMoSettings.Load(PetAiPaths.GetConfigPath()).MaxRecordSeconds;
            _audioCaptureService.StartRecording(maxRecordSeconds);
            _chatInputWindow?.SetRecordingState(true, "正在听妈妈说话，再点一次结束");
            SetMicButtonVisual(isRecording: true, toolTip: "再点一次结束录音");
            SetSpeechOverride("我在认真听妈妈说话哦", TimeSpan.FromSeconds(maxRecordSeconds + 2), "◔");
            ApplyVisual(_engine.Interact("listen"));
        }
        catch (Exception ex)
        {
            _chatInputWindow?.SetBusyState(false, $"录音没能开始：{ex.Message}");
            SetMicButtonVisual(isRecording: false, toolTip: "点我开始说话");
            SetSpeechOverride("我刚刚没能顺利开始听呢，妈妈再试一次好不好。", TimeSpan.FromSeconds(12), "!");
            ApplyVisual(_engine.Interact("surprised"));
        }
    }

    private async Task EndVoiceCaptureAndReplyAsync()
    {
        if (_activeChatCts is not null)
            return;

        string audioFilePath;
        try
        {
            _chatInputWindow?.SetBusyState(true, "我先把刚刚的话听成文字哦");
            SetMicButtonVisual(isRecording: false, toolTip: "我正在听清刚刚那句话");
            SetSpeechOverride("让我认真听清楚呀", TimeSpan.FromSeconds(12), "…");
            ApplyVisual(_engine.Interact("blink"));
            audioFilePath = await _audioCaptureService.StopRecordingAsync();
        }
        catch (Exception ex)
        {
            _chatInputWindow?.SetRecordingState(false, "点一下开始录音，再点一下结束");
            _chatInputWindow?.SetBusyState(false, ex.Message);
            SetMicButtonVisual(isRecording: false, toolTip: "点我开始说话");
            SetSpeechOverride(ex.Message, TimeSpan.FromSeconds(10), "!");
            ApplyVisual(_engine.Interact("surprised"));
            return;
        }

        _activeChatCts = new CancellationTokenSource();

        try
        {
            var result = await _speechSessionController.SendAudioAsync(audioFilePath, _activeChatCts.Token);
            if (!string.IsNullOrWhiteSpace(result.UserText))
            {
                _chatInputWindow?.SetRecognizedText(result.UserText);
                _chatInputWindow?.SetBusyState(false, $"听到：{result.UserText}");
            }
            else
            {
                _chatInputWindow?.SetBusyState(false, "这次没有听清，妈妈可以再试一次");
            }

            SetSpeechOverride(result.ReplyText, TimeSpan.FromSeconds(result.Success ? 20 : 14));
            ApplyVisual(_engine.Interact(result.SuggestedAction));
            _ = PlayReplyAudioIfNeededAsync(result.AudioFilePath);
        }
        catch (OperationCanceledException)
        {
            _chatInputWindow?.SetBusyState(false, "这次先停住啦，妈妈等会儿再叫我就好");
            SetSpeechOverride("这次先停住啦，妈妈等会儿再叫我就好。", TimeSpan.FromSeconds(10), "!");
            ApplyVisual(_engine.Interact("surprised"));
        }
        finally
        {
            _activeChatCts?.Dispose();
            _activeChatCts = null;
            _chatInputWindow?.SetRecordingState(false, "点一下开始录音，再点一下结束");
            SetMicButtonVisual(isRecording: false, toolTip: "点我开始说话");
        }
    }

    private void SetMicButtonVisual(bool isRecording, string toolTip)
    {
        MicButton.Background = isRecording
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE9859D"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF6FB"));
        MicButton.BorderBrush = isRecording
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFCE5A7B"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFDCA2BC"));
        MicButton.Foreground = isRecording
            ? Brushes.White
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFC55287"));
        MicButtonText.Text = isRecording ? "停" : "麦";
        MicButton.ToolTip = toolTip;
    }

    private void SetSpeechOverride(string text, TimeSpan duration, string? emotionText = null)
    {
        _speechOverrideText = text;
        _emotionOverrideText = emotionText;
        _speechOverrideUntil = DateTime.Now.Add(duration);
    }

    private void ApplySpeechOverrideIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(_speechOverrideText))
            return;

        if (DateTime.Now > _speechOverrideUntil)
        {
            _speechOverrideText = null;
            _emotionOverrideText = null;
            return;
        }

        SpeechText.Text = _speechOverrideText;
        if (!string.IsNullOrWhiteSpace(_emotionOverrideText))
            EmotionText.Text = _emotionOverrideText;
    }

    private sealed record ComponentInfo(List<(int X, int Y)> Points, int MinY, int MaxY)
    {
        public int Area => Points.Count;
    }
}
