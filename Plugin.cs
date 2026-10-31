using ClassIsland.ClassRecorderPlugin.Services;
using ClassIsland.ClassRecorderPlugin.Views.SettingsPages;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.ClassRecorderPlugin;

/// <summary>
/// 上课录制插件入口。
/// </summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton(new RecorderPluginContext(PluginConfigFolder, Info.Manifest.Id));
        services.AddSingleton<RecorderSettingsService>();
        services.AddSingleton<MicrophoneManager>();
        services.AddSingleton<StorageService>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<RecorderController>();
        services.AddHostedService<RecorderHostedService>();
        services.AddSettingsPage<ClassRecorderSettingsPage>();
    }
}
