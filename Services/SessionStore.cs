using System.Text.Json;
using System.Text.Json.Serialization;
using ClassIsland.ClassRecorderPlugin.Models;
using Microsoft.Extensions.Logging;

namespace ClassIsland.ClassRecorderPlugin.Services;

/// <summary>
/// 持久化未完成的录音会话，用于意外退出（含手动退出）后的恢复。
/// </summary>
public class SessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<SessionStore> _logger;
    private readonly string _path;
    private readonly object _gate = new();
    private List<RecordingSession> _sessions = [];

    public SessionStore(RecorderSettingsService settingsService, ILogger<SessionStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(settingsService.ConfigFolder, "PendingSessions.json");
        Load();
    }

    /// <summary>
    /// 读取所有未完成的录音会话。
    /// </summary>
    public IReadOnlyList<RecordingSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.ToList();
            }
        }
    }

    public void Add(RecordingSession session)
    {
        lock (_gate)
        {
            _sessions.RemoveAll(x => x.Id == session.Id);
            _sessions.Add(session);
            SaveInternal();
        }
    }

    public void Remove(RecordingSession session)
    {
        lock (_gate)
        {
            if (_sessions.RemoveAll(x => x.Id == session.Id) > 0)
                SaveInternal();
        }
    }

    public void Update(RecordingSession session)
    {
        lock (_gate)
        {
            var index = _sessions.FindIndex(x => x.Id == session.Id);
            if (index >= 0)
                _sessions[index] = session;
            else
                _sessions.Add(session);

            SaveInternal();
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            SaveInternal();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var json = File.ReadAllText(_path);
            _sessions = JsonSerializer.Deserialize<List<RecordingSession>>(json, SerializerOptions) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取未完成录音会话失败，将忽略这些会话。");
            _sessions = [];
        }
    }

    private void SaveInternal()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_sessions, SerializerOptions));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存未完成录音会话失败。");
        }
    }
}
