using Microsoft.Extensions.Logging;

namespace ClassIsland.ClassRecorderPlugin.Audio;

/// <summary>
/// WAV 文件辅助方法。主要用于修复因进程意外退出而没有写回长度的 PCM 临时文件。
/// </summary>
public static class WavFileHelper
{
    /// <summary>
    /// 重写 RIFF/data 块长度，并把不完整的尾帧截断，使文件可被正常读取。
    /// </summary>
    /// <returns>修复后的音频数据字节数。</returns>
    public static long FixHeader(string path, ILogger? logger = null)
    {
        if (!File.Exists(path))
            return 0;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            if (stream.Length < 44)
                return 0;

            using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            var riff = new string(reader.ReadChars(4));
            if (riff != "RIFF")
            {
                logger?.LogWarning("临时录音文件 {Path} 不是有效的 WAV 文件。", path);
                return stream.Length;
            }

            reader.ReadUInt32(); // RIFF 长度，稍后重写
            var wave = new string(reader.ReadChars(4));
            if (wave != "WAVE")
            {
                logger?.LogWarning("临时录音文件 {Path} 不是有效的 WAV 文件。", path);
                return stream.Length;
            }

            long dataOffset = -1;
            long dataSize = 0;
            var blockAlign = 2;
            var riffSizeOffset = 4L;
            long dataSizeOffset = -1;

            while (stream.Position + 8 <= stream.Length)
            {
                var chunkId = new string(reader.ReadChars(4));
                var chunkSize = reader.ReadUInt32();
                var chunkDataOffset = stream.Position;

                if (chunkId == "fmt ")
                {
                    reader.ReadUInt16(); // format
                    reader.ReadUInt16(); // channels
                    reader.ReadUInt32(); // sample rate
                    reader.ReadUInt32(); // byte rate
                    blockAlign = reader.ReadUInt16();
                    if (blockAlign <= 0)
                        blockAlign = 2;
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkDataOffset;
                    dataSizeOffset = chunkDataOffset - 4;
                    // data 块长度不可信（可能因崩溃而未写回），按文件实际长度计算。
                    dataSize = stream.Length - chunkDataOffset;
                    break;
                }

                stream.Position = chunkDataOffset + chunkSize + (chunkSize % 2);
            }

            if (dataOffset < 0 || dataSizeOffset < 0)
            {
                logger?.LogWarning("临时录音文件 {Path} 中未找到 data 块。", path);
                return 0;
            }

            dataSize = dataSize / blockAlign * blockAlign;
            var newLength = dataOffset + dataSize;
            if (stream.Length > newLength)
                stream.SetLength(newLength);

            stream.Position = riffSizeOffset;
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                writer.Write((uint)(newLength - 8));
                writer.Flush();
            }

            stream.Position = dataSizeOffset;
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                writer.Write((uint)dataSize);
                writer.Flush();
            }

            return dataSize;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "修复临时录音文件 {Path} 失败。", path);
            return 0;
        }
    }
}
