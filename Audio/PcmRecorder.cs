using NAudio.Wave;

namespace ClassIsland.ClassRecorderPlugin.Audio;

/// <summary>
/// 把 PCM 音频持续写入 WAV 临时文件，并周期性刷盘。
/// </summary>
/// <remarks>
/// 临时文件在正常结束时会被转码后删除；若进程意外退出，残留文件会在下次启动时被修复并转入
/// recovery 目录。刷盘间隔决定了意外退出最多丢失的音频长度。
/// </remarks>
public sealed class PcmRecorder : IDisposable
{
    private readonly WaveFileWriter _writer;
    private readonly object _gate = new();
    private bool _disposed;

    public PcmRecorder(string path, int sampleRate, int channels)
    {
        var folder = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        FilePath = path;
        _writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, channels));
    }

    public string FilePath { get; }

    /// <summary>已写入的 PCM 采样数。</summary>
    public long SampleCount { get; private set; }

    /// <summary>累计写入的音频时长。</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)SampleCount / Math.Max(1, _writer.WaveFormat.SampleRate * _writer.WaveFormat.Channels));

    public void Write(short[] samples, int count)
    {
        lock (_gate)
        {
            if (_disposed || count <= 0)
                return;

            if (count > samples.Length)
                count = samples.Length;

            _writer.WriteSamples(samples, 0, count);
            SampleCount += count;
        }
    }

    /// <summary>
    /// 把缓冲区刷入磁盘。用于控制意外退出时可丢失的音频长度。
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                _writer.Flush();
            }
            catch
            {
                // 刷盘失败不应中断录音，等待下次刷盘重试。
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                _writer.Dispose();
            }
            catch
            {
                // 忽略释放时的刷盘异常，残留文件会走恢复流程。
            }
        }
    }
}
