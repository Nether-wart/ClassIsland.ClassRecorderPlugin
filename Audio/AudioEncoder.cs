using ClassIsland.ClassRecorderPlugin.Models;
using Microsoft.Extensions.Logging;

namespace ClassIsland.ClassRecorderPlugin.Audio;

/// <summary>
/// 按会话设置的编码格式，把 PCM 临时文件转码为最终音频文件。
/// </summary>
public static class AudioEncoder
{
    public static void Encode(RecordingSession session, string wavPath, string outputPath, ILogger logger)
    {
        var bitrate = RecorderSettings.GetBitrate(session.Codec, session.Quality);
        switch (session.Codec)
        {
            case RecorderCodec.Opus:
                OpusEncoder.Encode(wavPath, outputPath, bitrate, logger);
                break;
            default:
                AacEncoder.Encode(wavPath, outputPath, bitrate, logger);
                break;
        }
    }
}
