using NAudio.MediaFoundation;
using NAudio.Wave;
using Microsoft.Extensions.Logging;

namespace ClassIsland.ClassRecorderPlugin.Audio;

/// <summary>
/// 使用 Windows Media Foundation 把 PCM WAV 编码为 AAC（.m4a）。
/// </summary>
public static class AacEncoder
{
    public static void Encode(string wavPath, string outputPath, int bitrate, ILogger logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        MediaFoundationApi.Startup();
        try
        {
            using var reader = new WaveFileReader(wavPath);
            var effectiveBitrate = ResolveBitrate(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels, bitrate, logger);
            logger.LogInformation("正在编码 AAC：{Source} -> {Target}（{Bitrate} bps，{Duration}）",
                wavPath, outputPath, effectiveBitrate, reader.TotalTime);

            MediaFoundationEncoder.EncodeToAac(reader, outputPath, effectiveBitrate);
        }
        finally
        {
            MediaFoundationApi.Shutdown();
        }
    }

    private static int ResolveBitrate(int sampleRate, int channels, int desired, ILogger logger)
    {
        try
        {
            var supported = MediaFoundationEncoder.GetEncodeBitrates(AudioSubtypes.MFAudioFormat_AAC, sampleRate, channels);
            if (supported.Length == 0)
                return desired;

            var nearest = supported.OrderBy(x => Math.Abs(x - desired)).First();
            if (nearest != desired)
            {
                logger.LogInformation("AAC 编码器不支持 {Desired} bps，改用最接近的 {Nearest} bps。", desired, nearest);
            }

            return nearest;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询 AAC 支持的比特率失败，沿用 {Desired} bps。", desired);
            return desired;
        }
    }
}
