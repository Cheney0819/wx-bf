using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private System.Windows.Point _dragStart;
    private bool _isDraggingWindow;
    private bool _dragVisualShown;
    private bool _suppressClickRelease;

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

    private sealed record ComponentInfo(List<(int X, int Y)> Points, int MinY, int MaxY)
    {
        public int Area => Points.Count;
    }
}
