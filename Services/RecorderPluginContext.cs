namespace ClassIsland.ClassRecorderPlugin.Services;

/// <summary>
/// 本插件的上下文信息。
/// </summary>
/// <remarks>
/// 不能通过依赖注入解析 <see cref="ClassIsland.Core.Abstractions.PluginBase"/>：每个插件都会把
/// 自己的实例注册为 <c>PluginBase</c>，最终解析到的实例取决于插件加载顺序，可能是别的插件，
/// 从而导致配置目录张冠李戴。因此插件在自己的 <c>Initialize</c> 中显式注册本类型。
/// </remarks>
public sealed class RecorderPluginContext
{
    public RecorderPluginContext(string configFolder, string pluginId)
    {
        ConfigFolder = configFolder;
        PluginId = pluginId;
    }

    /// <summary>本插件的配置目录。</summary>
    public string ConfigFolder { get; }

    /// <summary>本插件的 ID。</summary>
    public string PluginId { get; }
}
