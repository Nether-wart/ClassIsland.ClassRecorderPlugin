using NAudio.Wave;

namespace ClassIsland.ClassRecorderPlugin.Audio;

/// <summary>
/// 把任意声道数的采样降混为单声道。
/// </summary>
public sealed class DownmixToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer = [];

    public DownmixToMonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_sourceChannels == 1)
            return _source.Read(buffer, offset, count);

        var required = count * _sourceChannels;
        if (_sourceBuffer.Length < required)
            _sourceBuffer = new float[required];

        var read = _source.Read(_sourceBuffer, 0, required);
        var frames = read / _sourceChannels;
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            var baseIndex = frame * _sourceChannels;
            for (var ch = 0; ch < _sourceChannels; ch++)
            {
                sum += _sourceBuffer[baseIndex + ch];
            }

            buffer[offset + frame] = sum / _sourceChannels;
        }

        return frames;
    }
}
