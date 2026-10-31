using System.Text.Json;
using System.Text.Json.Serialization;
using ClassIsland.ClassRecorderPlugin.Models;
using Microsoft.Extensions.Logging;

namespace ClassIsland.ClassRecorderPlugin.Services;

/// <summary>
/// 负责加载、保存插件设置，并在设置变更后自动持久化。
/// </summary>
public class RecorderSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<RecorderSettingsService> _logger;
    private readonly string _settingsPath;
    private readonly object _saveGate = new();
    private CancellationTokenSource? _pendingSave;
    private bool _isLoaded;

    public RecorderSettingsService(RecorderPluginContext pluginContext,
        ILogger<RecorderSettingsService> logger)
    {
        _logger = logger;
        ConfigFolder = pluginContext.ConfigFolder;
        _settingsPath = Path.Combine(ConfigFolder, "Settings.json");
        var existed = File.Exists(_settingsPath);
        Settings = Load();
        Settings.Normalize();
        _isLoaded = true;
        Settings.PropertyChanged += (_, _) => ScheduleSave();

        // 首次运行时把默认设置落盘，方便用户直接查看/备份配置。
        if (!existed)
            Save();
    }

    /// <summary>插件配置目录。</summary>
    public string ConfigFolder { get; }

    public RecorderSettings Settings { get; }

    /// <summary>录音临时目录（PCM 片段）。</summary>
    public string TempFolder => Path.Combine(ConfigFolder, "Temp");

    private RecorderSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<RecorderSettings>(json, SerializerOptions);
                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载上课录制插件设置失败，将使用默认设置。");
        }

        return new RecorderSettings();
    }

    private void ScheduleSave()
    {
        if (!_isLoaded)
            return;

        lock (_saveGate)
        {
            _pendingSave?.Cancel();
            _pendingSave = new CancellationTokenSource();
            var token = _pendingSave.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300, token).ConfigureAwait(false);
                    Save();
                }
                catch (OperationCanceledException)
                {
                    // 设置仍在变化，等待下一次保存。
                }
            }, token);
        }
    }

    /// <summary>
    /// 立即保存设置。
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigFolder);
            var json = JsonSerializer.Serialize(Settings, SerializerOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存上课录制插件设置失败。");
        }
    }
}
