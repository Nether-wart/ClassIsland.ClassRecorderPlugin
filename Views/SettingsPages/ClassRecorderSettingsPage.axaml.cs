using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassIsland.ClassRecorderPlugin.Services;
using ClassIsland.ClassRecorderPlugin.ViewModels;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;

namespace ClassIsland.ClassRecorderPlugin.Views.SettingsPages;

/// <summary>
/// 上课录制设置页。
/// </summary>
[SettingsPageInfo("classisland.classrecorder", "上课录制", "\ue720", "\ue720")]
public partial class ClassRecorderSettingsPage : SettingsPageBase
{
    public ClassRecorderSettingsPage()
    {
        InitializeComponent();

        ViewModel = new ClassRecorderSettingsViewModel(
            IAppHost.GetService<RecorderSettingsService>(),
            IAppHost.GetService<RecorderController>(),
            IAppHost.GetService<MicrophoneManager>(),
            IAppHost.GetService<IProfileService>());

        DataContext = ViewModel;
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public ClassRecorderSettingsViewModel ViewModel { get; }

    private async void BrowseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
                return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择录音保存目录",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                ViewModel.AudioRoot = folders[0].Path.LocalPath;
            }
        }
        catch
        {
            // 用户取消选择或平台不支持目录选择时忽略。
        }
    }
}
