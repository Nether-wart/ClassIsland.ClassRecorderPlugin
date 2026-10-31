using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClassIsland.ClassRecorderPlugin.Audio;

/// <summary>
/// 使用纯托管 Opus 编码器把 PCM WAV 编码为 Ogg Opus（.opus）。
/// </summary>
public static class OpusEncoder
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameSize = SampleRate / 50; // 20ms

    public static void Encode(string wavPath, string outputPath, int bitrate, ILogger logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var reader = new WaveFileReader(wavPath);
        var sourceRate = reader.WaveFormat.SampleRate;
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1)
            samples = new DownmixToMonoSampleProvider(samples);

        logger.LogInformation("正在编码 Opus：{Source} -> {Target}（{Bitrate} bps，{Duration}）",
            wavPath, outputPath, bitrate, reader.TotalTime);

        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO, TextWriter.Null);
        encoder.Bitrate = bitrate;
        encoder.Complexity = 10;
        encoder.UseVBR = true;
        encoder.UseInbandFEC = true;

        // 输入采样率可能不是 48kHz（部分设备无法按 48kHz 采集），
        // 由 OpusOggWriteStream 内部的重采样器转换到编码器采样率。
        var frameSize = Math.Max(1, sourceRate / 50); // 20ms

        using var output = File.Create(outputPath);
        var tags = new OpusTags { Comment = "ClassIsland ClassRecorder" };
        var writeStream = new OpusOggWriteStream(encoder, output, tags, sourceRate, 10, false);

        try
        {
            var frame = new float[frameSize];
            var filled = 0;
            var buffer = new float[frameSize * 4];

            while (true)
            {
                var read = samples.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;

                var index = 0;
                while (index < read)
                {
                    var take = Math.Min(frameSize - filled, read - index);
                    Array.Copy(buffer, index, frame, filled, take);
                    filled += take;
                    index += take;

                    if (filled == frameSize)
                    {
                        writeStream.WriteSamples(frame, 0, frameSize);
                        filled = 0;
                    }
                }
            }

            if (filled > 0)
            {
                Array.Clear(frame, filled, frameSize - filled);
                writeStream.WriteSamples(frame, 0, frameSize);
            }
        }
        finally
        {
            writeStream.Finish();
        }
    }
}
