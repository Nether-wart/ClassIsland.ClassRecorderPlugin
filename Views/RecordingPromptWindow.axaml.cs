using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ClassIsland.ClassRecorderPlugin.Views;

/// <summary>
/// 开始录制前的提示浮窗。
/// </summary>
public partial class RecordingPromptWindow : Window
{
    private DispatcherTimer? _autoCloseTimer;

    public RecordingPromptWindow()
    {
        InitializeComponent();
    }

    public RecordingPromptWindow(string subjectName, int secondsUntilStart) : this()
    {
        MessageText.Text = secondsUntilStart > 0
            ? $"{secondsUntilStart} 秒后开始录制「{subjectName}」"
            : $"开始录制「{subjectName}」";

        Opened += OnOpened;

        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer?.Stop();
            Close();
        };
        _autoCloseTimer.Start();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
            if (screen == null)
                return;

            var area = screen.WorkingArea;
            var width = (int)(Bounds.Width > 0 ? Bounds.Width : Width);
            Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + 72);
        }
        catch
        {
            // 定位失败时使用默认位置。
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoCloseTimer?.Stop();
        _autoCloseTimer = null;
        base.OnClosed(e);
    }
}
