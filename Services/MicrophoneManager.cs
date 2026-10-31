using System.Collections.ObjectModel;
using ClassIsland.ClassRecorderPlugin.Audio;
using ClassIsland.ClassRecorderPlugin.Models;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClassIsland.ClassRecorderPlugin.Services;

/// <summary>
/// 一个可用的麦克风设备。
/// </summary>
public sealed record MicrophoneInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// 管理麦克风枚举与采集。采集会持续运行（由设置项控制），录音时把采样送入 <see cref="SampleSink"/>。
/// </summary>
public sealed class MicrophoneManager : IDisposable
{
    private readonly ILogger<MicrophoneManager> _logger;
    private readonly object _gate = new();
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pumpTask;
    private bool _isStopping;
    private bool _disposed;
    private bool _syntheticRunning;

    public MicrophoneManager(ILogger<MicrophoneManager> logger)
    {
        _logger = logger;
    }

#if DEBUG
    private const string SyntheticDeviceId = "classisland.classrecorder.synthetic";

    /// <summary>
    /// 测试用：在没有录音设备的机器上，用合成正弦波代替麦克风输入。
    /// </summary>
    private static bool IsSyntheticMicrophoneEnabled =>
        Environment.GetEnvironmentVariable("CLASSISLAND_CLASSRECORDER_SYNTHETIC_MIC") == "1";
#endif

    /// <summary>当前可用的麦克风列表。仅在初始化或用户点击重试时刷新，不会自动探测新设备。</summary>
    public ObservableCollection<MicrophoneInfo> Devices { get; } = [];

    /// <summary>设备列表发生变化。</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>采集过程中发生异常（设备被拔出等）。</summary>
    public event EventHandler<Exception>? CaptureFailed;

    /// <summary>录音采样接收者。为 null 时采样被丢弃。</summary>
    public Action<short[], int>? SampleSink { get; set; }

    public bool IsCapturing
    {
        get
        {
            lock (_gate)
            {
                return _capture != null || _syntheticRunning;
            }
        }
    }

    public bool IsAvailable => Devices.Count > 0;

    /// <summary>当前采集使用的采样率（单声道）。</summary>
    public int CaptureSampleRate { get; private set; } = RecorderSettings.SampleRate;

    /// <summary>当前采集使用的声道数（固定为单声道）。</summary>
    public int CaptureChannels => 1;

    private static WasapiCapture CreateCapture(MMDevice? device) =>
        device != null ? new WasapiCapture(device) : new WasapiCapture();

    public string? LastError { get; private set; }

    /// <summary>
    /// 重新枚举录音设备。
    /// </summary>
    public void RefreshDevices()
    {
        var devices = new List<MicrophoneInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    devices.Add(new MicrophoneInfo(device.ID, device.FriendlyName));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "读取麦克风设备信息失败，已跳过该设备。");
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "枚举麦克风设备失败。");
        }

#if DEBUG
        if (IsSyntheticMicrophoneEnabled)
        {
            devices.Add(new MicrophoneInfo(SyntheticDeviceId, "（测试）合成麦克风"));
        }
#endif

        Devices.Clear();
        foreach (var device in devices)
        {
            Devices.Add(device);
        }
        _logger.LogInformation("发现 {Count} 个可用麦克风。", Devices.Count);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 打开麦克风并开始采集。未指定设备或设备不存在时使用系统默认麦克风。
    /// </summary>
    public bool TryStartCapture(string? deviceId, out string? error)
    {
        error = null;

        lock (_gate)
        {
            if (_disposed)
            {
                error = "麦克风管理器已释放。";
                return false;
            }

            if (_capture != null || _syntheticRunning)
                return true;
        }

#if DEBUG
        if (IsSyntheticMicrophoneEnabled)
        {
            StartSyntheticCapture();
            return true;
        }
#endif

        WasapiCapture? capture = null;
        BufferedWaveProvider? buffer = null;
        CancellationTokenSource? cancellation = null;

        try
        {
            MMDevice? device = null;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    var candidate = enumerator.GetDevice(deviceId);
                    if (candidate != null && candidate.State == DeviceState.Active)
                    {
                        device = candidate;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "打开指定麦克风 {DeviceId} 失败，将回退到系统默认麦克风。", deviceId);
                }
            }

            capture = CreateCapture(device);
            _logger.LogInformation("尝试使用采样格式：{Format}", capture.WaveFormat);

            try
            {
                // 优先让 WASAPI 直接输出 48kHz 单声道（Windows 会做高质量重采样），
                // 这样可以避免自己重采样带来的时长漂移问题。
                capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(RecorderSettings.SampleRate, 1);
                capture.StartRecording();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "无法按 48kHz 单声道采集，回退到设备默认格式。");
                try
                {
                    capture.Dispose();
                }
                catch
                {
                    // 忽略。
                }

                capture = CreateCapture(device);
                capture.StartRecording();
            }

            _logger.LogInformation("实际采集格式：{Format}", capture.WaveFormat);

            buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(15),
                DiscardOnBufferOverflow = true,
                // NAudio 2.2.x 的 ReadFully 默认为 true：缓冲区为空时也会返回满额静音数据，
                // 会导致消费线程以 CPU 速度写入海量静音。必须关掉。
                ReadFully = false
            };

            ISampleProvider samples = buffer.ToSampleProvider();
            if (samples.WaveFormat.Channels > 1)
                samples = new DownmixToMonoSampleProvider(samples);

            CaptureSampleRate = samples.WaveFormat.SampleRate;

            _isStopping = false;
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            cancellation = new CancellationTokenSource();
            lock (_gate)
            {
                _capture = capture;
                _buffer = buffer;
                _pumpCancellation = cancellation;
                _pumpTask = Task.Run(() => PumpAsync(samples, cancellation.Token));
            }

            LastError = null;
            _logger.LogInformation("麦克风已打开。");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            LastError = ex.Message;
            _logger.LogError(ex, "打开麦克风失败。");

            try
            {
                cancellation?.Cancel();
                if (capture != null)
                {
                    capture.DataAvailable -= OnDataAvailable;
                    capture.RecordingStopped -= OnRecordingStopped;
                    capture.Dispose();
                }
            }
            catch
            {
                // 忽略清理异常。
            }

            return false;
        }
    }

    /// <summary>
    /// 关闭麦克风。
    /// </summary>
    public void StopCapture()
    {
        WasapiCapture? capture;
        CancellationTokenSource? cancellation;
        Task? pump;

        lock (_gate)
        {
            capture = _capture;
            cancellation = _pumpCancellation;
            pump = _pumpTask;
            _capture = null;
            _buffer = null;
            _pumpCancellation = null;
            _pumpTask = null;
            _syntheticRunning = false;
            _isStopping = true;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch
        {
            // 忽略。
        }

        if (capture != null)
        {
            try
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.StopRecording();
                capture.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "关闭麦克风时发生异常。");
            }
        }

        try
        {
            pump?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 忽略。
        }

        cancellation?.Dispose();
    }

#if DEBUG
    private void StartSyntheticCapture()
    {
        lock (_gate)
        {
            _pumpCancellation = new CancellationTokenSource();
            _syntheticRunning = true;
            _pumpTask = Task.Run(() => SyntheticPumpAsync(_pumpCancellation.Token));
        }

        LastError = null;
        _logger.LogWarning("已启用合成麦克风（仅 Debug 构建的测试用输入源）。");
    }

    private async Task SyntheticPumpAsync(CancellationToken cancellationToken)
    {
        var buffer = new short[RecorderSettings.SampleRate / 50]; // 20ms
        const double step = 2 * Math.PI * 440 / RecorderSettings.SampleRate;
        var phase = 0.0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long generated = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 按真实流逝时间生成采样，避免 Task.Delay 的计时精度导致录音时长偏短。
                // 落后时在一个循环里补齐，防止线程调度抖动累积成缺口。
                var target = (long)(stopwatch.Elapsed.TotalSeconds * RecorderSettings.SampleRate);
                while (target > generated && !cancellationToken.IsCancellationRequested)
                {
                    var count = (int)Math.Min(buffer.Length, target - generated);
                    for (var i = 0; i < count; i++)
                    {
                        buffer[i] = (short)(Math.Sin(phase) * 8000);
                        phase += step;
                    }

                    phase %= 2 * Math.PI;
                    SampleSink?.Invoke(buffer, count);
                    generated += count;
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }
#endif

    private void OnDataAvailable(object? sender, WaveInEventArgs e)    {
        var buffer = _buffer;
        if (buffer == null || e.BytesRecorded <= 0)
            return;

        try
        {
            buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入采集缓冲区失败。");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null || _isStopping)
            return;

        LastError = e.Exception.Message;
        _logger.LogError(e.Exception, "麦克风采集中断。");

        // 清理当前采集，使后续重试可以重新打开设备。
        try
        {
            StopCapture();
        }
        catch
        {
            // 忽略。
        }

        CaptureFailed?.Invoke(this, e.Exception);
    }

    private async Task PumpAsync(ISampleProvider samples, CancellationToken cancellationToken)
    {
        var floatBuffer = new float[2048];
        var shortBuffer = new short[2048];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = samples.Read(floatBuffer, 0, floatBuffer.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "读取麦克风采样失败。");
                    CaptureFailed?.Invoke(this, ex);
                    return;
                }

                if (read <= 0)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var sink = SampleSink;
                if (sink == null)
                    continue;

                for (var i = 0; i < read; i++)
                {
                    var value = (int)(floatBuffer[i] * 32767f);
                    shortBuffer[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
                }

                try
                {
                    sink(shortBuffer, read);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "写入录音数据失败。");
                    CaptureFailed?.Invoke(this, ex);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopCapture();
    }
}
