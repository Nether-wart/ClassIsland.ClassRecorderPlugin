using ClassIsland.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.ClassRecorderPlugin.Services;

/// <summary>
/// 随应用启动/停止录音服务。
/// </summary>
public class RecorderHostedService : IHostedService
{
    private readonly RecorderController _controller;
    private readonly ILogger<RecorderHostedService> _logger;
    private bool _stopped;

    public RecorderHostedService(RecorderController controller, ILogger<RecorderHostedService> logger)
    {
        _controller = controller;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _controller.Initialize();
        AppBase.Current.AppStopping += OnAppStopping;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    private void OnAppStopping(object? sender, EventArgs e) => Stop();

    private void Stop()
    {
        if (_stopped)
            return;

        _stopped = true;
        try
        {
            _controller.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止上课录制服务时发生异常。");
        }
    }
}
