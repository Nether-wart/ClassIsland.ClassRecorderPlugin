using System.Collections.ObjectModel;
using Avalonia.Threading;
using ClassIsland.ClassRecorderPlugin.Models;
using ClassIsland.ClassRecorderPlugin.Services;
using ClassIsland.Core.Abstractions.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassIsland.ClassRecorderPlugin.ViewModels;

public sealed record CodecOption(RecorderCodec Codec, string Name);

public sealed record QualityOption(RecorderQuality Quality, string Name);

public sealed record MicrophoneOption(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// 设置页中的一门可勾选课程。
/// </summary>
public partial class SubjectSelectionItem : ObservableObject
{
    private readonly Action _changed;

    public SubjectSelectionItem(Guid id, string name, bool isSelected, Action changed)
    {
        Id = id;
        Name = name;
        _isSelected = isSelected;
        _changed = changed;
    }

    public Guid Id { get; }

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed();
}

/// <summary>
/// 上课录制设置页的视图模型。
/// </summary>
public partial class ClassRecorderSettingsViewModel : ObservableObject, IDisposable
{
    private readonly RecorderSettingsService _settingsService;
    private readonly RecorderController _controller;
    private readonly MicrophoneManager _microphone;
    private readonly IProfileService _profileService;
    private bool _suppressSync;
    private bool _disposed;
    private readonly DispatcherTimer _levelTimer;

    public ClassRecorderSettingsViewModel(RecorderSettingsService settingsService,
        RecorderController controller,
        MicrophoneManager microphone,
        IProfileService profileService)
    {
        _settingsService = settingsService;
        _controller = controller;
        _microphone = microphone;
        _profileService = profileService;

        _controller.StateChanged += OnExternalStateChanged;
        _microphone.DevicesChanged += OnExternalStateChanged;
        Settings.PropertyChanged += OnSettingsPropertyChanged;

        LoadSubjects();
        InitializeMicrophoneDevices();
        UpdateStorageUsage();

        _levelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _levelTimer.Tick += (_, _) => OnPropertyChanged(nameof(AudioLevel));
        _levelTimer.Start();
    }

    public RecorderSettings Settings => _settingsService.Settings;

    public ObservableCollection<SubjectSelectionItem> Subjects { get; } = [];

    public ObservableCollection<MicrophoneOption> Microphones { get; } = [];

    public IReadOnlyList<CodecOption> CodecOptions { get; } =
    [
        new(RecorderCodec.Opus, "Opus（推荐）"),
        new(RecorderCodec.Aac, "AAC")
    ];

    public IReadOnlyList<QualityOption> QualityOptions { get; } =
    [
        new(RecorderQuality.High, "高"),
        new(RecorderQuality.Medium, "中"),
        new(RecorderQuality.Low, "低")
    ];

    #region 状态

    public string StatusText => _controller.StatusMessage;

    public bool IsRecording => _controller.IsRecording;

    public string RecordingTargetText => _controller.CurrentSession is { } session
        ? $"{session.SubjectName}（{session.LessonStart:HH:mm} 开始）"
        : "";

    public bool HasMicrophone => _microphone.IsAvailable;

    public bool HasNoMicrophone => !_microphone.IsAvailable;

    /// <summary>麦克风正常时才显示状态正文，避免和红色异常横幅重复。</summary>
    public bool ShowStatusText => _microphone.IsAvailable;

    public bool HasNoSubjects => Subjects.Count == 0;

    public bool HasSelectedSubjects => Settings.SelectedSubjectIds.Count > 0;

    public bool ShowNoSubjectHint => !HasSelectedSubjects;

    public string MicrophoneStatusText => _microphone.IsAvailable
        ? $"已发现 {_microphone.Devices.Count} 个可用麦克风"
        : "未找到麦克风。点击「刷新列表」重新枚举，插件不会自动探测新设备。";

    /// <summary>麦克风输入电平（0~1）。</summary>
    public double AudioLevel => _controller.AudioLevel;

    /// <summary>是否处于录音或试录状态（用于禁用「试录」按钮）。</summary>
    public bool IsBusyRecording => _controller.IsRecording || _controller.IsTestRecording;

    public bool CanTestRecord => _microphone.IsAvailable && !IsBusyRecording;

    public string TestRecordHint { get; private set; } = "";

    public string StorageUsageText { get; private set; } = "正在统计…";

    public double StorageUsagePercent { get; private set; }

    #endregion

    #region 绑定属性

    public string AudioRoot
    {
        get => Settings.AudioRoot;
        set
        {
            if (Settings.AudioRoot == value)
                return;

            Settings.AudioRoot = value;
            OnPropertyChanged();
        }
    }

    public decimal AdvanceMinutes
    {
        get => Settings.AdvanceMinutes;
        set
        {
            var v = (int)value;
            if (Settings.AdvanceMinutes == v)
                return;

            Settings.AdvanceMinutes = v;
            OnPropertyChanged();
        }
    }

    public decimal DelayMinutes
    {
        get => Settings.DelayMinutes;
        set
        {
            var v = (int)value;
            if (Settings.DelayMinutes == v)
                return;

            Settings.DelayMinutes = v;
            OnPropertyChanged();
        }
    }

    public decimal MaxTotalSizeMb
    {
        get => Settings.MaxTotalSizeMb;
        set
        {
            var v = Math.Max(RecorderSettings.MinTotalSizeMb, (int)value);
            if (Settings.MaxTotalSizeMb == v)
                return;

            Settings.MaxTotalSizeMb = v;
            OnPropertyChanged();
        }
    }

    public CodecOption SelectedCodec
    {
        get => CodecOptions.First(x => x.Codec == Settings.Codec);
        set
        {
            if (value == null || Settings.Codec == value.Codec)
                return;

            Settings.Codec = value.Codec;
            OnPropertyChanged();
        }
    }

    public QualityOption SelectedQuality
    {
        get => QualityOptions.First(x => x.Quality == Settings.Quality);
        set
        {
            if (value == null || Settings.Quality == value.Quality)
                return;

            Settings.Quality = value.Quality;
            OnPropertyChanged();
        }
    }

    public MicrophoneOption? SelectedMicrophone
    {
        get => Microphones.FirstOrDefault(x => string.Equals(x.Id ?? "", Settings.MicrophoneDeviceId ?? "",
                   StringComparison.OrdinalIgnoreCase)) ?? Microphones.FirstOrDefault();
        set
        {
            if (value == null)
                return;

            if (string.Equals(Settings.MicrophoneDeviceId ?? "", value.Id ?? "", StringComparison.OrdinalIgnoreCase))
                return;

            Settings.MicrophoneDeviceId = value.Id;
            OnPropertyChanged();
            _controller.RestartMicrophone();
        }
    }

    public string EstimatedCapacityText
    {
        get
        {
            var capacity = Settings.EstimateCapacity();
            var bitrate = RecorderSettings.GetBitrate(Settings.Codec, Settings.Quality) / 1000;
            var text = $"约 {FormatDuration(capacity)}（{bitrate} kbps）";

            var lessonMinutes = GetLessonMinutes();
            if (lessonMinutes > 0)
            {
                var lessons = (int)(capacity.TotalMinutes / lessonMinutes);
                if (lessons > 0)
                    text += $"，约 {lessons} 节课";
            }

            return text;
        }
    }

    #endregion

    #region 命令

    [RelayCommand]
    private void RetryMicrophone() => _controller.RetryMicrophone();

    [RelayCommand]
    private void RefreshMicrophones()
    {
        _controller.RetryMicrophone();
        RefreshMicrophoneList();
    }

    [RelayCommand]
    private void SelectAllSubjects() => SetAllSubjects(true);

    [RelayCommand]
    private void ClearSubjects() => SetAllSubjects(false);

    [RelayCommand]
    private void StartTestRecording()
    {
        if (_controller.TryStartTestRecording(TimeSpan.FromSeconds(10), out var error))
        {
            TestRecordHint = "正在试录 10 秒…";
        }
        else
        {
            TestRecordHint = error ?? "无法开始试录。";
        }

        OnPropertyChanged(nameof(TestRecordHint));
        OnPropertyChanged(nameof(IsBusyRecording));
        OnPropertyChanged(nameof(CanTestRecord));
    }

    [RelayCommand]
    private void RefreshStorageUsage() => UpdateStorageUsage();

    private void SetAllSubjects(bool selected)
    {
        _suppressSync = true;
        try
        {
            foreach (var subject in Subjects)
            {
                subject.IsSelected = selected;
            }
        }
        finally
        {
            _suppressSync = false;
        }

        SyncSelectedSubjects();
    }

    #endregion

    public void RefreshMicrophoneList()
    {
        var selected = Settings.MicrophoneDeviceId;
        _suppressSync = true;
        try
        {
            Microphones.Clear();
            Microphones.Add(new MicrophoneOption(null, "系统默认麦克风"));
            foreach (var device in _microphone.Devices)
            {
                Microphones.Add(new MicrophoneOption(device.Id, device.Name));
            }

            OnPropertyChanged(nameof(SelectedMicrophone));
            OnPropertyChanged(nameof(HasMicrophone));
            OnPropertyChanged(nameof(HasNoMicrophone));
            OnPropertyChanged(nameof(ShowStatusText));
            OnPropertyChanged(nameof(MicrophoneStatusText));
        }
        finally
        {
            _suppressSync = false;
        }

        Settings.MicrophoneDeviceId = selected;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _levelTimer.Stop();
        _controller.StateChanged -= OnExternalStateChanged;
        _microphone.DevicesChanged -= OnExternalStateChanged;
        Settings.PropertyChanged -= OnSettingsPropertyChanged;
    }

    private void LoadSubjects()
    {
        var selected = Settings.SelectedSubjectIds.ToHashSet();
        var profile = _profileService.Profile;
        if (profile?.Subjects == null)
            return;

        foreach (var pair in profile.Subjects.OrderBy(x => x.Value.Name))
        {
            Subjects.Add(new SubjectSelectionItem(pair.Key, pair.Value.Name, selected.Contains(pair.Key), SyncSelectedSubjects));
        }

        OnPropertyChanged(nameof(HasNoSubjects));
    }

    private void SyncSelectedSubjects()
    {
        if (_suppressSync)
            return;

        Settings.SelectedSubjectIds = Subjects.Where(x => x.IsSelected).Select(x => x.Id).ToList();
        OnPropertyChanged(nameof(HasSelectedSubjects));
        OnPropertyChanged(nameof(ShowNoSubjectHint));
    }

    private void InitializeMicrophoneDevices()
    {
        // 设备列表只由控制器在启动时枚举一次；按规范不自动探测新设备，
        // 需要重新枚举时必须由用户点击「刷新列表」或「重试」。
        RefreshMicrophoneList();
    }

    private void OnExternalStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(RecordingTargetText));
            OnPropertyChanged(nameof(HasMicrophone));
            OnPropertyChanged(nameof(HasNoMicrophone));
            OnPropertyChanged(nameof(ShowStatusText));
            OnPropertyChanged(nameof(MicrophoneStatusText));
            OnPropertyChanged(nameof(IsBusyRecording));
            OnPropertyChanged(nameof(CanTestRecord));

            // 试录/录音结束后清掉临时提示，避免一直显示「正在试录…」。
            if (!IsBusyRecording && TestRecordHint.Length > 0)
            {
                TestRecordHint = "";
                OnPropertyChanged(nameof(TestRecordHint));
            }

            RefreshMicrophoneList();
            UpdateStorageUsage();
        });
    }

    /// <summary>
    /// 在后台统计录音目录占用，避免打开设置页时卡顿。
    /// </summary>
    private void UpdateStorageUsage()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var (bytes, files) = _controller.GetStorageUsage();
                var maxMb = Math.Max(RecorderSettings.MinTotalSizeMb, Settings.MaxTotalSizeMb);
                var maxBytes = (long)maxMb * 1024 * 1024;
                var percent = maxBytes <= 0 ? 0 : Math.Min(100, bytes * 100.0 / maxBytes);

                Dispatcher.UIThread.Post(() =>
                {
                    StorageUsagePercent = percent;
                    StorageUsageText =
                        $"已用 {FormatSize(bytes)} / {FormatSize(maxBytes)}（{percent:F1}%）· {files} 个文件";
                    OnPropertyChanged(nameof(StorageUsageText));
                    OnPropertyChanged(nameof(StorageUsagePercent));
                });
            }
            catch
            {
                // 统计失败不影响其他功能。
            }
        });
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[0]}" : $"{value:F1} {units[unit]}";
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RecorderSettings.Codec):
                OnPropertyChanged(nameof(SelectedCodec));
                OnPropertyChanged(nameof(EstimatedCapacityText));
                break;
            case nameof(RecorderSettings.Quality):
                OnPropertyChanged(nameof(SelectedQuality));
                OnPropertyChanged(nameof(EstimatedCapacityText));
                break;
            case nameof(RecorderSettings.MaxTotalSizeMb):
                OnPropertyChanged(nameof(MaxTotalSizeMb));
                OnPropertyChanged(nameof(EstimatedCapacityText));
                UpdateStorageUsage();
                break;
            case nameof(RecorderSettings.AdvanceMinutes):
                OnPropertyChanged(nameof(AdvanceMinutes));
                break;
            case nameof(RecorderSettings.DelayMinutes):
                OnPropertyChanged(nameof(DelayMinutes));
                break;
            case nameof(RecorderSettings.AudioRoot):
                OnPropertyChanged(nameof(AudioRoot));
                UpdateStorageUsage();
                break;
        }
    }

    private int GetLessonMinutes()
    {
        try
        {
            var plan = _controller.GetPlanForToday();
            var item = plan?.Classes
                .Where(x => x.IsEnabled)
                .Select(x => x.CurrentTimeLayoutItem)
                .FirstOrDefault(x => x.EndTime > x.StartTime);

            return item == null ? 0 : (int)(item.EndTime - item.StartTime).TotalMinutes;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟";

        return $"{Math.Max(1, duration.Minutes)} 分钟";
    }
}

