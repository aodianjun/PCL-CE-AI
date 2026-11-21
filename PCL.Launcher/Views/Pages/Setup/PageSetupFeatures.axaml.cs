using Avalonia.Controls;
using Avalonia.Input;
using PCL.Core.App.Localization;
using PCL.Core.UI.Hints;
using PCL.Navigation;
using PCL.UI.Controls;

namespace PCL.Views.Pages.Setup;

/// <summary>
/// 设置 - 功能入口：把原启动器里分散在主界面上的下载、实例、工具等入口集中到设置页。
/// 主界面因此只保留启动、账号、版本与 AI 对话。
/// </summary>
public partial class PageSetupFeatures : MyPageRight
{
    public PageSetupFeatures()
    {
        InitializeComponent();
        PanScroll = this.FindControl<MyScrollViewer>("PanBack");

        this.FindControl<MyButton>("BtnDownloadInstall")!.Click += DownloadInstall_OnClick;
        this.FindControl<MyButton>("BtnDownloadMod")!.Click += DownloadMod_OnClick;
        this.FindControl<MyButton>("BtnDownloadPack")!.Click += DownloadPack_OnClick;
        this.FindControl<MyButton>("BtnDownloadResourcePack")!.Click += DownloadResourcePack_OnClick;
        this.FindControl<MyButton>("BtnDownloadShader")!.Click += DownloadShader_OnClick;
        this.FindControl<MyButton>("BtnDownloadWorld")!.Click += DownloadWorld_OnClick;
        this.FindControl<MyButton>("BtnDownloadDataPack")!.Click += DownloadDataPack_OnClick;
        this.FindControl<MyButton>("BtnSelectInstance")!.Click += SelectInstance_OnClick;
        this.FindControl<MyButton>("BtnInstanceSetup")!.Click += InstanceSetup_OnClick;
        this.FindControl<MyButton>("BtnGameLog")!.Click += GameLog_OnClick;
        this.FindControl<MyButton>("BtnTaskManager")!.Click += TaskManager_OnClick;
        this.FindControl<MyButton>("BtnMultiplayer")!.Click += Multiplayer_OnClick;
        this.FindControl<MyButton>("BtnUtilities")!.Click += Utilities_OnClick;
        this.FindControl<MyButton>("BtnAiSetup")!.Click += AiSetup_OnClick;
        this.FindControl<MyButton>("BtnHome")!.Click += Home_OnClick;
    }

    private void DownloadInstall_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadInstall);
    }

    private void DownloadMod_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadMod);
    }

    private void DownloadPack_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadPack);
    }

    private void DownloadResourcePack_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadResourcePack);
    }

    private void DownloadShader_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadShader);
    }

    private void DownloadWorld_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadWorld);
    }

    private void DownloadDataPack_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadDataPack);
    }

    private void SelectInstance_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.InstanceSelect);
    }

    private void InstanceSetup_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        var instance = InstanceList.McMcInstanceSelected;
        if (instance is null)
        {
            HintService.Show(Hints.Error(Lang.Text("Setup.Features.NoInstance")));
            PageRouter.Navigate(PageIds.InstanceSelect);
            return;
        }

        PageRouter.Navigate(PageIds.InstanceSetup, null, instance);
    }

    private void GameLog_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.GameLog);
    }

    private void TaskManager_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.TaskManager);
    }

    private void Multiplayer_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.Tools, SubPageIds.ToolsGameLink);
    }

    private void Utilities_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.Tools, SubPageIds.ToolsTest);
    }

    private void AiSetup_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.Setup, SubPageIds.SetupAi);
    }

    private void Home_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        PageRouter.Navigate(PageIds.Launch);
    }
}
