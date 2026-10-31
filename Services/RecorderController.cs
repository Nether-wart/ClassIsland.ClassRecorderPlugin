using Avalonia.Threading;
using ClassIsland.ClassRecorderPlugin.Audio;
using ClassIsland.ClassRecorderPlugin.Models;
using ClassIsland.ClassRecorderPlugin.Views;
using ClassIsland.Core.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace ClassIsland.ClassRecorderPlugin.Services;

/// <summary>
/// 录音器状态。
/// </summary>
public enum RecorderState
{
    /// <summary>空闲。</summary>
    Idle = 0,

    /// <summary>正在录制。</summary>
    Recording = 1,

    /// <summary>麦克风不可用或正在重试。</summary>
    Unavailable = 2
}

/// <summary>
/// 上课录音的核心：按课表计算录音窗口，驱动麦克风采集、转码、滚动存储与意外恢复。
/// </summary>
public class RecorderController : IDisposable
{
    /// <summary>PCM 临时文件刷盘间隔。决定了意外退出时最多丢失的音频长度。</summary>
    private const int FlushIntervalSeconds = 10;

    /// <summary>开始录制前提示的提前秒数。</summary>
    private const int PromptLeadSeconds = 5;

    private readonly RecorderSettingsService _settingsService;
    private readonly MicrophoneManager _microphone;
    private readonly StorageService _storage;
    private readonly SessionStore _store;
    private readonly ILessonsService _lessonsService;
    private readonly IExactTimeService _timeService;
    private readonly IProfileService _profileService;
    private readonly ILogger<RecorderController> _logger;

    private readonly object _gate = new();
    private Timer? _timer;
    private RecordingSession? _session;
    private PcmRecorder? _pcm;
    private DateTime _lastFlush = DateTime.MinValue;
    private DateTime _nextMicrophoneRetry = DateTime.MinValue;
    private string? _lastPromptKey;
    private RecordingSession? _pendingResume;
    private bool _initialized;
    private bool _disposed;
    private RecorderState _state = RecorderState.Idle;
    private string _statusMessage = "待机";

    private PcmRecorder? _testPcm;
    private string _testTempPath = "";
    private DateTime _testStopAt = DateTime.MinValue;
    private int _audioLevelPermille;

    private int CaptureSampleRate =>
        _microphone.CaptureSampleRate > 0 ? _microphone.CaptureSampleRate : RecorderSettings.SampleRate;

    public RecorderController(RecorderSettingsService settingsService,
        MicrophoneManager microphone,
        StorageService storage,
        SessionStore store,
        ILessonsService lessonsService,
        IExactTimeService timeService,
        IProfileService profileService,
        ILogger<RecorderController> logger)
    {
        _settingsService = settingsService;
        _microphone = microphone;
        _storage = storage;
        _store = store;
        _lessonsService = lessonsService;
        _timeService = timeService;
        _profileService = profileService;
        _logger = logger;
    }

    /// <summary>状态发生变化。</summary>
    public event EventHandler? StateChanged;

    public RecorderState State => _state;

    public string StatusMessage => _statusMessage;

    public bool IsRecording => _session != null;

    /// <summary>当前是否正在试录。</summary>
    public bool IsTestRecording => _testPcm != null;

    /// <summary>当前麦克风输入电平（0~1），用于界面电平条。</summary>
    public double AudioLevel => Volatile.Read(ref _audioLevelPermille) / 1000.0;

    public RecordingSession? CurrentSession => _session;

    public RecorderSettings Settings => _settingsService.Settings;

    public MicrophoneManager Microphone => _microphone;

    public void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        _microphone.CaptureFailed += OnCaptureFailed;
        _microphone.DevicesChanged += (_, _) => RaiseStateChanged();

        _microphone.RefreshDevices();
        CleanupOrphanTempFiles();
        RecoverPendingSessions();
        Purge();

        _timer = new Timer(_ => SafeTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// 应用退出时调用。若正在录制，则保留临时文件与会话状态，下次启动走恢复流程。
    /// </summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;

            var pcm = _pcm;
            _pcm = null;
            pcm?.Flush();
            pcm?.Dispose();
            _microphone.SampleSink = null;

            // 试录属于临时验证，退出时直接丢弃，不进入恢复流程。
            var testPcm = _testPcm;
            _testPcm = null;
            testPcm?.Flush();
            testPcm?.Dispose();
            if (!string.IsNullOrEmpty(_testTempPath))
            {
                TryDelete(_testTempPath);
                _testTempPath = "";
            }

            if (_session != null)
            {
                _logger.LogWarning("应用在录制过程中退出，已保留临时录音，下次启动将转入 recovery 目录并继续录制。");
                _store.Update(_session);
                _session = null;
            }

            _settingsService.Save();
        }

        _microphone.Dispose();
    }

    #region 调度

    private void SafeTick()
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "录音调度发生异常。");
        }
    }

    private void Tick()
    {
        var now = _timeService.GetCurrentLocalDateTime();
        var settings = _settingsService.Settings;

        EnsureMicrophone(now, settings);

        List<RecordingWindow> windows;
        try
        {
            windows = GetWindows(now, settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "计算录音时间窗口失败。");
            windows = [];
        }

        lock (_gate)
        {
            MaybePrompt(windows, now, settings);

            // 试录到点结束。
            if (_testPcm != null && now >= _testStopAt)
            {
                FinishTestRecording();
            }

            // 没有新采样时让电平自然回落。
            var level = Volatile.Read(ref _audioLevelPermille);
            if (level > 0)
            {
                Volatile.Write(ref _audioLevelPermille, level * 6 / 10);
            }

            // 意外恢复：重启后若未到结束时间，继续录制。
            if (_session == null && _pendingResume != null)
            {
                if (now >= _pendingResume.PlannedStop)
                {
                    _pendingResume = null;
                }
                else if (_microphone.IsCapturing)
                {
                    var resume = _pendingResume;
                    _pendingResume = null;
                    _session = resume;
                    if (BeginSegment(resume, now))
                    {
                        SetState(RecorderState.Recording, $"正在录制（恢复）：{resume.SubjectName}");
                    }
                    else
                    {
                        _session = null;
                    }
                }
            }

            var current = windows
                .Where(w => now >= w.Start && now < w.Stop)
                .OrderByDescending(w => w.Start)
                .FirstOrDefault();

            if (_session != null)
            {
                // 计划时间到，或下一节课已经开始（提前结束上一份录音，正常保存）。
                var shouldStop = now >= _session.PlannedStop ||
                                 (current != null && current.LessonStart != _session.LessonStart);
                if (shouldStop)
                {
                    FinishSession();
                }
                else if (_pcm == null && _microphone.IsCapturing)
                {
                    // 麦克风恢复后继续写入新片段。
                    BeginSegment(_session, now);
                }
            }

            if (_session == null && current != null && _pendingResume == null && _testPcm == null)
            {
                var session = CreateSession(current, settings);
                _session = session;
                if (BeginSegment(session, now))
                {
                    SetState(RecorderState.Recording, $"正在录制：{session.SubjectName}");
                    _logger.LogInformation("开始录制「{Subject}」，计划结束时间 {Stop:HH:mm:ss}。",
                        session.SubjectName, session.PlannedStop);
                }
                else
                {
                    _session = null;
                }
            }

            if (_pcm != null && (now - _lastFlush).TotalSeconds >= FlushIntervalSeconds)
            {
                _pcm.Flush();
                _lastFlush = now;
            }
        }
    }

    private void EnsureMicrophone(DateTime now, RecorderSettings settings)
    {
        if (_microphone.IsCapturing)
            return;

        if (now < _nextMicrophoneRetry)
            return;

        // 未找到任何麦克风时不做自动探测，等待用户点击重试。
        if (!_microphone.IsAvailable)
        {
            SetState(RecorderState.Unavailable, "未找到可用麦克风");
            return;
        }

        var needed = settings.KeepMicrophoneOpen || _session != null || _pendingResume != null;
        if (!needed)
            return;

        if (_microphone.TryStartCapture(settings.MicrophoneDeviceId, out var error))
        {
            _microphone.SampleSink ??= OnSamples;
            if (_session == null && _pendingResume == null)
            {
                SetState(RecorderState.Idle, "待机");
            }
        }
        else
        {
            _nextMicrophoneRetry = now.AddSeconds(15);
            SetState(RecorderState.Unavailable, $"麦克风打开失败，稍后重试：{error}");
            _logger.LogWarning("麦克风打开失败，15 秒后重试：{Error}", error);
        }
    }

    /// <summary>
    /// 立即重试麦克风（设置页「重试」按钮与刷新设备共用）。
    /// </summary>
    public void RetryMicrophone()
    {
        _nextMicrophoneRetry = DateTime.MinValue;
        _microphone.RefreshDevices();
        if (!_microphone.IsAvailable)
        {
            SetState(RecorderState.Unavailable, "未找到可用麦克风");
            return;
        }

        string? error = null;
        if (!_microphone.IsCapturing &&
            _microphone.TryStartCapture(_settingsService.Settings.MicrophoneDeviceId, out error))
        {
            SetState(RecorderState.Idle, "待机");
        }
        else if (!_microphone.IsCapturing)
        {
            SetState(RecorderState.Unavailable, $"麦克风打开失败：{error}");
        }
    }

    /// <summary>
    /// 麦克风设备被更换后重新打开采集。
    /// </summary>
    public void RestartMicrophone()
    {
        _microphone.CaptureFailed -= OnCaptureFailed;
        _microphone.StopCapture();
        _microphone.CaptureFailed += OnCaptureFailed;
        _nextMicrophoneRetry = DateTime.MinValue;
        RetryMicrophone();
    }

    #endregion

    #region 会话生命周期

    private RecordingSession CreateSession(RecordingWindow window, RecorderSettings settings) => new()
    {
        SubjectName = window.SubjectName,
        LessonStart = window.LessonStart,
        PlannedStop = window.Stop,
        RecoveryMode = window.RecoveryMode,
        SegmentIndex = window.SegmentIndex,
        Codec = settings.Codec,
        Quality = settings.Quality
    };

    private bool BeginSegment(RecordingSession session, DateTime now)
    {
        if (!_microphone.IsCapturing)
            return false;

        var tempPath = Path.Combine(_settingsService.TempFolder,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{session.Id:N}.wav");

        try
        {
            _pcm?.Dispose();
            _pcm = new PcmRecorder(tempPath, CaptureSampleRate, _microphone.CaptureChannels);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建临时录音文件失败：{Path}", tempPath);
            SetState(RecorderState.Unavailable, "无法创建临时录音文件");
            return false;
        }

        session.TempFilePath = tempPath;
        _lastFlush = now;
        _microphone.SampleSink = OnSamples;
        _store.Update(session);

        _logger.LogInformation("开始写入录音片段 #{Segment}：{Path}", session.SegmentIndex, tempPath);
        return true;
    }

    private void OnSamples(short[] samples, int count)
    {
        // 电平始终更新，方便在未录音时也能确认麦克风有信号。
        var peak = 0;
        for (var i = 0; i < count; i++)
        {
            var value = Math.Abs((int)samples[i]);
            if (value > peak)
                peak = value;
        }

        Volatile.Write(ref _audioLevelPermille, peak * 1000 / 32768);

        try
        {
            _pcm?.Write(samples, count);
            _testPcm?.Write(samples, count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入录音数据失败。");
        }
    }

    private void FinishSession()
    {
        var session = _session;
        if (session == null)
            return;

        _session = null;

        var pcm = _pcm;
        _pcm = null;
        pcm?.Flush();
        pcm?.Dispose();

        _store.Remove(session);
        SetState(RecorderState.Idle, "正在保存录音…");

        var tempPath = session.TempFilePath;
        _ = Task.Run(() => EncodeAndStore(session, tempPath));
    }

    #region 试录

    /// <summary>
    /// 立即试录 <paramref name="duration"/>，用于在没有课时的情况下验证麦克风。
    /// </summary>
    public bool TryStartTestRecording(TimeSpan duration, out string? error)
    {
        error = null;

        lock (_gate)
        {
            if (_session != null)
            {
                error = "正在录制课程，无法试录。";
                return false;
            }

            if (_testPcm != null)
            {
                error = "试录正在进行中。";
                return false;
            }

            if (!_microphone.IsAvailable)
            {
                error = "未找到可用麦克风。";
                return false;
            }

            if (!_microphone.IsCapturing &&
                !_microphone.TryStartCapture(_settingsService.Settings.MicrophoneDeviceId, out var startError))
            {
                error = startError ?? "无法打开麦克风。";
                return false;
            }

            var now = _timeService.GetCurrentLocalDateTime();
            _testTempPath = Path.Combine(_settingsService.TempFolder, $"test-{DateTime.Now:yyyyMMdd-HHmmss}.wav");

            try
            {
                _testPcm = new PcmRecorder(_testTempPath, CaptureSampleRate, _microphone.CaptureChannels);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建试录临时文件失败。");
                error = "无法创建临时录音文件。";
                return false;
            }

            _testStopAt = now.Add(duration);
            _microphone.SampleSink = OnSamples;
            SetState(RecorderState.Recording, "正在试录…");
            _logger.LogInformation("开始试录 {Duration}。", duration);
            return true;
        }
    }

    private void FinishTestRecording()
    {
        var pcm = _testPcm;
        if (pcm == null)
            return;

        _testPcm = null;
        var tempPath = _testTempPath;
        _testTempPath = "";

        pcm.Flush();
        pcm.Dispose();

        SetState(RecorderState.Idle, "试录完成，正在保存…");
        _ = Task.Run(() => EncodeTestRecording(tempPath));
    }

    private void EncodeTestRecording(string tempPath)
    {
        try
        {
            if (!File.Exists(tempPath))
                return;

            var dataBytes = WavFileHelper.FixHeader(tempPath, _logger);
            if (dataBytes < RecorderSettings.SampleRate)
            {
                TryDelete(tempPath);
                SetState(RecorderState.Idle, "试录时间过短，已丢弃");
                return;
            }

            var settings = _settingsService.Settings;
            var session = new RecordingSession
            {
                SubjectName = "试录",
                LessonStart = DateTime.Now,
                Codec = settings.Codec,
                Quality = settings.Quality
            };

            var folder = Path.Combine(GetAudioRoot(), "test");
            Directory.CreateDirectory(folder);
            var extension = RecorderSettings.GetExtension(session.Codec);
            var outputPath = EnsureUnique(Path.Combine(folder, $"试录-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}"));

            AudioEncoder.Encode(session, tempPath, outputPath, _logger);
            TryDelete(tempPath);

            SetState(RecorderState.Idle, $"试录已保存：{outputPath}");
            _logger.LogInformation("试录已保存：{Path}", outputPath);
            Purge();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存试录失败。");
            SetState(RecorderState.Idle, "试录保存失败");
        }
    }

    /// <summary>
    /// 获取当前录音目录的占用情况。
    /// </summary>
    public (long TotalBytes, int FileCount) GetStorageUsage()
    {
        var root = GetAudioRoot();
        return (_storage.GetTotalSize(root), _storage.GetRecordingFileCount(root));
    }

    #endregion

    private void EncodeAndStore(RecordingSession session, string tempPath)
    {
        try
        {
            if (!File.Exists(tempPath))
                return;

            var dataBytes = WavFileHelper.FixHeader(tempPath, _logger);
            var minimumBytes = RecorderSettings.SampleRate * 2 / 2; // 0.5 秒
            if (dataBytes < minimumBytes)
            {
                TryDelete(tempPath);
                SetState(RecorderState.Idle, "录音时间过短，已丢弃");
                return;
            }

            var outputPath = BuildOutputPath(session);
            AudioEncoder.Encode(session, tempPath, outputPath, _logger);
            TryDelete(tempPath);

            SetState(RecorderState.Idle, $"已保存：{Path.GetFileName(outputPath)}");
            _logger.LogInformation("录音已保存：{Path}", outputPath);

            Purge();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存录音失败，尝试把原始录音转入 recovery 目录。");
            TryMoveToRecovery(session, tempPath);
            SetState(RecorderState.Idle, "录音保存失败，原始录音已转入 recovery 目录");
        }
    }

    /// <summary>
    /// 转码失败时，至少把 PCM 原始文件放进 recovery 目录，避免录音彻底丢失。
    /// </summary>
    private void TryMoveToRecovery(RecordingSession session, string tempPath)
    {
        try
        {
            if (!File.Exists(tempPath))
                return;

            WavFileHelper.FixHeader(tempPath, _logger);
            var folder = Path.Combine(GetAudioRoot(), "recovery");
            Directory.CreateDirectory(folder);
            var target = EnsureUnique(Path.Combine(folder,
                $"{session.LessonStart:yyyy-MM-dd}-{Sanitize(session.SubjectName)}-{session.LessonStart:HH_mm}-{session.SegmentIndex:00}.wav"));
            File.Move(tempPath, target, true);
            _logger.LogWarning("原始录音已保存到 {Path}", target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "把原始录音转入 recovery 目录失败，临时文件保留在 {Path}", tempPath);
        }
    }

    private void OnCaptureFailed(object? sender, Exception exception)
    {
        _logger.LogError(exception, "麦克风采集中断，稍后重试。");
        _nextMicrophoneRetry = _timeService.GetCurrentLocalDateTime().AddSeconds(15);

        lock (_gate)
        {
            if (_session == null)
            {
                SetState(RecorderState.Unavailable, "麦克风已断开，稍后重试…");
                return;
            }

            // 不完整的片段按意外恢复规则写入 recovery 目录，麦克风恢复后继续录制。
            var snapshot = _session.Clone();
            snapshot.RecoveryMode = true;
            var tempPath = _session.TempFilePath;

            _microphone.SampleSink = null;
            var pcm = _pcm;
            _pcm = null;
            pcm?.Flush();
            pcm?.Dispose();

            _session.RecoveryMode = true;
            _session.SegmentIndex = Math.Max(1, _session.SegmentIndex) + 1;
            _session.TempFilePath = "";
            _store.Update(_session);

            SetState(RecorderState.Unavailable, "麦克风已断开，稍后重试…");
            _ = Task.Run(() => EncodeAndStore(snapshot, tempPath));
        }
    }

    #endregion

    #region 恢复

    private void RecoverPendingSessions()
    {
        var now = _timeService.GetCurrentLocalDateTime();
        foreach (var stored in _store.Sessions.ToList())
        {
            var session = stored;
            session.RecoveryMode = true;

            if (File.Exists(session.TempFilePath))
            {
                var snapshot = session.Clone();
                var tempPath = session.TempFilePath;
                _logger.LogWarning("发现未完成的录音片段「{Subject}」，正在转入 recovery 目录。", session.SubjectName);
                _ = Task.Run(() => EncodeAndStore(snapshot, tempPath));
            }

            if (now < session.PlannedStop)
            {
                session.SegmentIndex = Math.Max(1, session.SegmentIndex) + 1;
                session.TempFilePath = "";
                _store.Update(session);
                _pendingResume = session;
                _logger.LogWarning("「{Subject}」的录音尚未到结束时间，将在启动后继续录制。", session.SubjectName);
            }
            else
            {
                _store.Remove(session);
            }
        }
    }

    private void CleanupOrphanTempFiles()
    {
        try
        {
            if (!Directory.Exists(_settingsService.TempFolder))
                return;

            var referenced = _store.Sessions.Select(x => x.TempFilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(_settingsService.TempFolder, "*.wav"))
            {
                if (referenced.Contains(file))
                    continue;

                if (DateTime.Now - File.GetLastWriteTime(file) < TimeSpan.FromHours(1))
                    continue;

                TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理临时录音文件失败。");
        }
    }

    #endregion

    #region 时间窗口与路径

    /// <summary>
    /// 获取当天的课表（用于设置页估算课时长度等）。
    /// </summary>
    public ClassIsland.Shared.Models.Profile.ClassPlan? GetPlanForToday()
    {
        var now = _timeService.GetCurrentLocalDateTime();
        return _lessonsService.CurrentClassPlan ?? _lessonsService.GetClassPlanByDate(now, out _);
    }

    private List<RecordingWindow> GetWindows(DateTime now, RecorderSettings settings)
    {
        List<RecordingWindow> windows = [];

#if DEBUG
        if (TryGetTestWindow(now, settings, out var testWindow))
        {
            return [testWindow];
        }
#endif

        if (settings.SelectedSubjectIds.Count == 0 || string.IsNullOrWhiteSpace(settings.AudioRoot))
            return windows;

        var plan = _lessonsService.CurrentClassPlan ?? _lessonsService.GetClassPlanByDate(now, out _);
        if (plan == null)
            return windows;

        var subjects = _profileService.Profile?.Subjects;

        foreach (var classInfo in plan.Classes)
        {
            if (!classInfo.IsEnabled)
                continue;
            if (!settings.SelectedSubjectIds.Contains(classInfo.SubjectId))
                continue;

            var item = classInfo.CurrentTimeLayoutItem;
            if (item == null || item.StartTime == TimeSpan.Zero || item.EndTime <= item.StartTime)
                continue;

            var lessonStart = now.Date + item.StartTime;
            var lessonEnd = now.Date + item.EndTime;
            var start = lessonStart.AddMinutes(-settings.AdvanceMinutes);
            var stop = lessonEnd.AddMinutes(settings.DelayMinutes);

            var name = "未命名课程";
            if (subjects != null && subjects.TryGetValue(classInfo.SubjectId, out var subject) &&
                !string.IsNullOrWhiteSpace(subject.Name))
            {
                name = subject.Name;
            }

            windows.Add(new RecordingWindow(name, lessonStart, start, stop));
        }

        return windows
            .GroupBy(w => w.Key)
            .Select(g => g.First())
            .ToList();
    }

#if DEBUG
    /// <summary>
    /// 测试用：通过环境变量 <c>CLASSISLAND_CLASSRECORDER_TEST_WINDOW="HH:mm-HH:mm|课程名"</c>
    /// 注入一个录音窗口，便于在没有课表数据的环境中验证完整录音流程。
    /// </summary>
    private static bool TryGetTestWindow(DateTime now, RecorderSettings settings, out RecordingWindow window)
    {
        window = null!;

        var raw = Environment.GetEnvironmentVariable("CLASSISLAND_CLASSRECORDER_TEST_WINDOW");
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var segments = raw.Split('|');
        var times = segments[0].Split('-');
        if (times.Length != 2 ||
            !TimeSpan.TryParse(times[0], out var start) ||
            !TimeSpan.TryParse(times[1], out var end))
        {
            return false;
        }

        var name = segments.Length > 1 && !string.IsNullOrWhiteSpace(segments[1]) ? segments[1] : "测试课程";
        var lessonStart = now.Date + start;
        var lessonEnd = now.Date + end;

        window = new RecordingWindow(name, lessonStart,
            lessonStart.AddMinutes(-settings.AdvanceMinutes),
            lessonEnd.AddMinutes(settings.DelayMinutes));
        return true;
    }
#endif

    private void MaybePrompt(List<RecordingWindow> windows, DateTime now, RecorderSettings settings)    {
        if (!settings.PromptBeforeRecording)
            return;

        var upcoming = windows
            .Where(w => now >= w.Start.AddSeconds(-PromptLeadSeconds) && now < w.Start)
            .OrderBy(w => w.Start)
            .FirstOrDefault();

        if (upcoming == null || _lastPromptKey == upcoming.Key)
            return;

        _lastPromptKey = upcoming.Key;
        var seconds = Math.Max(0, (int)Math.Ceiling((upcoming.Start - now).TotalSeconds));

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                new RecordingPromptWindow(upcoming.SubjectName, seconds).Show();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "显示开始录制提示失败。");
            }
        });
    }

    public string GetAudioRoot()
    {
        var root = _settingsService.Settings.AudioRoot;
        return string.IsNullOrWhiteSpace(root) ? RecorderSettings.DefaultAudioRoot : root;
    }

    private string BuildOutputPath(RecordingSession session)
    {
        var root = GetAudioRoot();
        var extension = RecorderSettings.GetExtension(session.Codec);
        var subject = Sanitize(session.SubjectName);

        string path;
        if (session.RecoveryMode)
        {
            var folder = Path.Combine(root, "recovery");
            path = Path.Combine(folder,
                $"{session.LessonStart:yyyy-MM-dd}-{subject}-{session.LessonStart:HH_mm}-{session.SegmentIndex:00}.{extension}");
        }
        else
        {
            var folder = Path.Combine(root,
                session.LessonStart.ToString("yyyy"),
                session.LessonStart.ToString("MM"),
                session.LessonStart.ToString("dd"));
            path = Path.Combine(folder, $"{subject}-{session.LessonStart:HH_mm}.{extension}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return EnsureUnique(path);
    }

    private static string EnsureUnique(string path)
    {
        if (!File.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{name}-{index}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(directory, $"{name}-{DateTime.Now:HHmmss}{extension}");
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(result) ? "未命名课程" : result;
    }

    private void Purge()
    {
        try
        {
            var settings = _settingsService.Settings;
            _storage.Purge(GetAudioRoot(), (long)settings.MaxTotalSizeMb * 1024 * 1024, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "滚动存储清理失败。");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除文件 {Path} 失败。", path);
        }
    }

    #endregion

    private void SetState(RecorderState state, string message)
    {
        if (_state == state && _statusMessage == message)
            return;

        _state = state;
        _statusMessage = message;
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新录音器状态时发生异常。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Shutdown();
    }
}

