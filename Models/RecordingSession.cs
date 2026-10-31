using ClassIsland.ClassRecorderPlugin.Models;

namespace ClassIsland.ClassRecorderPlugin.Models;

/// <summary>
/// 一段待完成（或被中断）的录音会话。会被持久化，用于意外退出后的恢复。
/// </summary>
public class RecordingSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>课程名称（科目名）。</summary>
    public string SubjectName { get; set; } = "";

    /// <summary>该次录音对应的上课时间。</summary>
    public DateTime LessonStart { get; set; }

    /// <summary>计划停止时间（含延后分钟数）。</summary>
    public DateTime PlannedStop { get; set; }

    /// <summary>当前片段对应的 PCM 临时文件。</summary>
    public string TempFilePath { get; set; } = "";

    /// <summary>是否处于恢复模式（片段写入 recovery 目录）。</summary>
    public bool RecoveryMode { get; set; }

    /// <summary>当前片段编号，从 1 开始。</summary>
    public int SegmentIndex { get; set; } = 1;

    public RecorderCodec Codec { get; set; } = RecorderCodec.Opus;

    public RecorderQuality Quality { get; set; } = RecorderQuality.Medium;

    public RecordingSession Clone() => (RecordingSession)MemberwiseClone();
}

/// <summary>
/// 一个可录制的时间窗口，由课表计算得出。
/// </summary>
/// <param name="SubjectName">课程名称。</param>
/// <param name="LessonStart">上课时间。</param>
/// <param name="Start">开始录制时间（上课时间 - 提前分钟）。</param>
/// <param name="Stop">结束录制时间（下课时间 + 延后分钟）。</param>
/// <param name="RecoveryMode">是否按恢复模式录音。</param>
/// <param name="SegmentIndex">恢复模式下的起始片段编号。</param>
public sealed record RecordingWindow(
    string SubjectName,
    DateTime LessonStart,
    DateTime Start,
    DateTime Stop,
    bool RecoveryMode = false,
    int SegmentIndex = 1)
{
    public string Key => $"{LessonStart:yyyyMMddHHmm}|{SubjectName}";
}
