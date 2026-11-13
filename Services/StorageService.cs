using Microsoft.Extensions.Logging;

namespace ClassIsland.ClassRecorderPlugin.Services;

/// <summary>
/// 滚动存储：当录音目录超过容量上限时，按写入时间从旧到新删除录音文件。
/// </summary>
public class StorageService
{
    // 兼容旧版本生成的 .m4a/.wav 录音；新版本只会产生 .opus。
    private static readonly string[] RecordingExtensions = [".m4a", ".opus", ".wav"];

    public long GetTotalSize(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return 0;

        long total = 0;
        foreach (var file in EnumerateRecordingFiles(root))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
                // 文件可能在扫描期间被删除，忽略。
            }
        }

        return total;
    }

    /// <summary>
    /// 统计录音目录中的文件数量。
    /// </summary>
    public int GetRecordingFileCount(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return 0;

        try
        {
            return EnumerateRecordingFiles(root).Count();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 删除旧录音，直到目录总大小不超过上限。
    /// </summary>
    /// <returns>被删除的文件数。</returns>
    public int Purge(string root, long maxBytes, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || maxBytes <= 0)
            return 0;

        var files = new List<FileInfo>();
        foreach (var path in EnumerateRecordingFiles(root))
        {
            try
            {
                files.Add(new FileInfo(path));
            }
            catch
            {
                // 忽略无法访问的文件。
            }
        }

        var total = files.Sum(f => f.Length);
        if (total <= maxBytes)
            return 0;

        var deleted = 0;
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= maxBytes)
                break;

            try
            {
                var size = file.Length;
                file.Delete();
                total -= size;
                deleted++;
                logger.LogInformation("滚动存储已删除旧录音：{Path}（{Size} 字节）", file.FullName, size);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "删除旧录音 {Path} 失败。", file.FullName);
            }
        }

        return deleted;
    }

    private static IEnumerable<string> EnumerateRecordingFiles(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => RecordingExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    }
}
