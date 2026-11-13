using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using ClassIsland.ClassRecorderPlugin.Models;
using Microsoft.Extensions.Logging;

namespace ClassIsland.ClassRecorderPlugin.Audio;

/// <summary>
/// 实时把麦克风 PCM 采样编码为 Ogg Opus 并直接写入最终文件。
/// </summary>
/// <remarks>
/// 编码后的数据先留在内存缓冲区中，达到最大字节数或最大音频时长后写入磁盘，
/// 因此不会产生 PCM 临时文件。进程意外退出时最多丢失一个缓冲窗口的音频。
/// </remarks>
public sealed class OpusRecordingStream : IDisposable
{
    private const int EncoderSampleRate = RecorderSettings.SampleRate;
    private const int EncoderChannels = RecorderSettings.Channels;
    private const int ResamplerQuality = 10;

    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly int _inputSampleRate;
    private readonly FileStream _file;
    private readonly BufferedWriteStream _buffer;
    private readonly OpusOggWriteStream _ogg;

    private long _sampleCount;
    private long _lastFlushSampleCount;
    private double _maxBufferSizeMb;
    private int _maxBufferDurationSeconds;
    private bool _finished;

    public OpusRecordingStream(string filePath,
        int inputSampleRate,
        int bitrate,
        double maxBufferSizeMb,
        int maxBufferDurationSeconds,
        ILogger logger)
    {
        FilePath = filePath;
        _logger = logger;
        _inputSampleRate = inputSampleRate > 0 ? inputSampleRate : EncoderSampleRate;
        _maxBufferSizeMb = ClampBufferSize(maxBufferSizeMb);
        _maxBufferDurationSeconds = ClampBufferDuration(maxBufferDurationSeconds);

        var folder = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 1, FileOptions.SequentialScan);
        try
        {
            var buffer = new BufferedWriteStream(file, ToBytes(_maxBufferSizeMb));
            try
            {
                var encoder = OpusCodecFactory.CreateEncoder(EncoderSampleRate, EncoderChannels,
                    OpusApplication.OPUS_APPLICATION_AUDIO, TextWriter.Null);
                encoder.Bitrate = bitrate;
                encoder.Complexity = 10;
                encoder.UseVBR = true;
                encoder.UseInbandFEC = true;

                _file = file;
                _buffer = buffer;
                _ogg = new OpusOggWriteStream(encoder, buffer,
                    new OpusTags { Comment = "ClassIsland ClassRecorder" },
                    _inputSampleRate, ResamplerQuality, leaveOpen: true);
                ApplyPageLength();
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public string FilePath { get; }

    public long SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _sampleCount;
            }
        }
    }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)SampleCount / _inputSampleRate);

    /// <summary>尚未写入磁盘的音频时长。仅在持有 <c>_gate</c> 时读取。</summary>
    private TimeSpan UnflushedDuration =>
        TimeSpan.FromSeconds((double)(_sampleCount - _lastFlushSampleCount) / _inputSampleRate);

    public void Write(short[] samples, int count)
    {
        lock (_gate)
        {
            if (_finished || count <= 0)
                return;

            _ogg.WriteSamples(samples, 0, count);
            _sampleCount += count;

            if (UnflushedDuration >= TimeSpan.FromSeconds(_maxBufferDurationSeconds) ||
                _buffer.BufferedBytes >= _buffer.MaxBytes)
                FlushBuffer();
        }
    }

    /// <summary>应用最新的缓冲设置。录音过程中修改设置后立即生效。</summary>
    public void UpdateLimits(double maxBufferSizeMb, int maxBufferDurationSeconds)
    {
        lock (_gate)
        {
            if (_finished)
                return;

            _maxBufferSizeMb = ClampBufferSize(maxBufferSizeMb);
            _maxBufferDurationSeconds = ClampBufferDuration(maxBufferDurationSeconds);
            _buffer.MaxBytes = ToBytes(_maxBufferSizeMb);

            if (UnflushedDuration >= TimeSpan.FromSeconds(_maxBufferDurationSeconds) ||
                _buffer.BufferedBytes >= _buffer.MaxBytes)
                FlushBuffer();
        }
    }

    /// <summary>达到缓冲上限时写盘；供调度定时器调用。</summary>
    public void FlushIfNeeded()
    {
        lock (_gate)
        {
            if (!_finished &&
                (UnflushedDuration >= TimeSpan.FromSeconds(_maxBufferDurationSeconds) ||
                 _buffer.BufferedBytes >= _buffer.MaxBytes))
                FlushBuffer();
        }
    }

    /// <summary>结束编码并写入 Ogg 结束页。</summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_finished)
                return;

            _finished = true;
            try
            {
                _ogg.Finish();
            }
            finally
            {
                try
                {
                    FlushBuffer();
                }
                finally
                {
                    _buffer.Dispose();
                    _file.Dispose();
                }
            }

            _logger.LogInformation("录音片段已写入：{Path}（{Duration}）", FilePath, Duration);
        }
    }

    private void ApplyPageLength()
    {
        // 每秒生成一个 Ogg 页：编码器内部最多只保留约 1 秒的已编码数据，
        // 使实际内存占用更接近用户设置的缓冲上限，也能减少意外退出时丢失的数据。
        _ogg.MaxAudioLengthPerPage = TimeSpan.FromSeconds(1);
    }

    private void FlushBuffer()
    {
        // 只有真正写入了数据才重置计时；页未凑满时写入为空，需要在下一个 tick 继续尝试。
        if (_buffer.FlushPending())
            _lastFlushSampleCount = _sampleCount;
    }

    private static double ClampBufferSize(double value) =>
        Math.Clamp(double.IsFinite(value) ? value : RecorderSettings.DefaultMaxBufferSizeMb,
            RecorderSettings.MinBufferSizeMb,
            RecorderSettings.MaxBufferSizeLimitMb);

    private static int ClampBufferDuration(int value) =>
        Math.Clamp(value,
            RecorderSettings.MinBufferDurationSeconds,
            RecorderSettings.MaxBufferDurationLimitSeconds);

    private static long ToBytes(double megabytes) => Math.Max(1, (long)Math.Round(megabytes * 1024 * 1024));

    public void Dispose() => Finish();

    /// <summary>
    /// 控制编码数据何时落盘的写入流。数据先进入内存，达到上限或显式 Flush 时写入底层文件。
    /// </summary>
    private sealed class BufferedWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly MemoryStream _buffer = new();

        public BufferedWriteStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            MaxBytes = maxBytes;
        }

        public long MaxBytes { get; set; }

        public long BufferedBytes => _buffer.Length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > 0)
                _buffer.Write(buffer, offset, count);
        }

        public override void Flush() => FlushPending();

        /// <summary>把内存缓冲写入底层流。</summary>
        /// <returns>是否实际写入了数据。</returns>
        public bool FlushPending()
        {
            var written = false;
            if (_buffer.Length > 0)
            {
                _buffer.Position = 0;
                _buffer.WriteTo(_inner);
                _buffer.SetLength(0);
                _buffer.Position = 0;
                written = true;
            }

            _inner.Flush();
            return written;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FlushPending();
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
