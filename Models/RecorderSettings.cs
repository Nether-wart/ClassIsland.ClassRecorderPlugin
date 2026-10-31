using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassIsland.ClassRecorderPlugin.Models;

/// <summary>
/// 录音编码格式。
/// </summary>
public enum RecorderCodec
{
    /// <summary>AAC，封装为 MPEG-4（.m4a）。</summary>
    Aac = 0,

    /// <summary>Opus，封装为 Ogg（.opus）。</summary>
    Opus = 1
}

/// <summary>
/// 录音质量挡位。
/// </summary>
public enum RecorderQuality
{
    High = 0,
    Medium = 1,
    Low = 2
}

/// <summary>
/// 插件设置。
/// </summary>
public partial class RecorderSettings : ObservableObject
{
    /// <summary>允许设置的最小录音目录容量（MB）。</summary>
    public const int MinTotalSizeMb = 256;

    /// <summary>录音固定采样率。</summary>
    public const int SampleRate = 48000;

    /// <summary>录音固定声道数（课堂录音使用单声道）。</summary>
    public const int Channels = 1;

    [ObservableProperty]
    private List<Guid> _selectedSubjectIds = [];

    [ObservableProperty]
    private string _audioRoot = "";

    [ObservableProperty]
    private int _advanceMinutes;

    [ObservableProperty]
    private int _delayMinutes;

    [ObservableProperty]
    private int _maxTotalSizeMb = 2048;

    [ObservableProperty]
    private string? _microphoneDeviceId;

    [ObservableProperty]
    private bool _promptBeforeRecording;

    [ObservableProperty]
    private bool _keepMicrophoneOpen = true;

    [ObservableProperty]
    private RecorderCodec _codec = RecorderCodec.Opus;

    [ObservableProperty]
    private RecorderQuality _quality = RecorderQuality.Medium;

    /// <summary>
    /// 获取指定编码与质量挡位的目标比特率（bps）。
    /// </summary>
    public static int GetBitrate(RecorderCodec codec, RecorderQuality quality) => codec switch
    {
        RecorderCodec.Opus => quality switch
        {
            RecorderQuality.High => 64_000,
            RecorderQuality.Medium => 48_000,
            _ => 24_000
        },
        RecorderCodec.Aac => quality switch
        {
            // Windows Media Foundation 的 AAC 编码器在 48 kHz 下仅支持 96/128/160/192 kbps。
            RecorderQuality.High => 160_000,
            RecorderQuality.Medium => 128_000,
            _ => 96_000
        },
        _ => 48_000
    };

    /// <summary>
    /// 获取指定编码对应的文件扩展名。
    /// </summary>
    public static string GetExtension(RecorderCodec codec) => codec == RecorderCodec.Opus ? "opus" : "m4a";

    public static string GetCodecDisplayName(RecorderCodec codec) => codec == RecorderCodec.Opus ? "Opus" : "AAC";

    public static string GetQualityDisplayName(RecorderQuality quality) => quality switch
    {
        RecorderQuality.High => "高",
        RecorderQuality.Medium => "中",
        _ => "低"
    };

    /// <summary>
    /// 按最大占用空间估算可录时长。可变比特率下按高出标称码率 15% 保守估算。
    /// </summary>
    public TimeSpan EstimateCapacity(RecorderCodec codec, RecorderQuality quality)
    {
        var bitrate = GetBitrate(codec, quality) * 1.15;
        var bytes = Math.Max(0, (long)MaxTotalSizeMb * 1024 * 1024);
        var seconds = bytes * 8 / bitrate;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// 按当前设置估算可录时长。
    /// </summary>
    public TimeSpan EstimateCapacity() => EstimateCapacity(Codec, Quality);

    /// <summary>
    /// 修正不符合约束的设置值。
    /// </summary>
    public void Normalize()
    {
        if (MaxTotalSizeMb < MinTotalSizeMb)
            MaxTotalSizeMb = MinTotalSizeMb;

        if (AdvanceMinutes < 0)
            AdvanceMinutes = 0;
        if (DelayMinutes < 0)
            DelayMinutes = 0;

        if (string.IsNullOrWhiteSpace(AudioRoot))
            AudioRoot = DefaultAudioRoot;

        SelectedSubjectIds ??= [];
    }

    // 不用「我的文档」：部分环境下该目录会被重定向到项目仓库等不合适的路径。
    public static string DefaultAudioRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ClassIsland", "Recordings");
}
