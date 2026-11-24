using Avalonia.Controls;
using PCL.UI.Controls;

namespace PCL.Views.Pages.Launch;

/// <summary>
/// 启动页工作区：默认展示原版主页内容；点左下角“AI 对话”后主页自动收起，
/// 由 AI 面板占满内容区；AI 面板左上角的返回按钮切回主页。
/// </summary>
public partial class PageLaunchWorkspace : UserControl
{
    private readonly ContentControl _panAi;
    private readonly ContentControl _panHome;
    private readonly MyButton _btnOpenAi;

    public PageLaunchWorkspace(PageLaunchAi ai, PageLaunchRight home)
    {
        InitializeComponent();

        _panAi = this.FindControl<ContentControl>("PanAi")!;
        _panHome = this.FindControl<ContentControl>("PanHome")!;
        _btnOpenAi = this.FindControl<MyButton>("BtnOpenAi")!;

        _panAi.Content = ai;
        _panHome.Content = home;

        _btnOpenAi.Click += (_, _) => ShowAi();
        ai.ShowHomeRequested += ShowHome;

        ShowHome();
    }

    public void ShowAi()
    {
        _panAi.IsVisible = true;
        _panHome.IsVisible = false;
        _btnOpenAi.IsVisible = false;
    }

    public void ShowHome()
    {
        _panAi.IsVisible = false;
        _panHome.IsVisible = true;
        _btnOpenAi.IsVisible = true;
    }
}
