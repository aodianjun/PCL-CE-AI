using Avalonia.Controls;
using PCL.Navigation;

namespace PCL.Views.Pages.Launch;

/// <summary>
/// 启动页宿主：右栏是 AI 对话主界面。
/// 原左栏（<see cref="PageLaunchLeft" />）仍然由组合根创建，但不再挂载到窗口左侧，
/// 而是作为账号/启动状态控制器被右栏收进账号抽屉。
/// </summary>
public sealed class PageLaunchHost(
    PageLaunchLeft left,
    PageLaunchRight right) : IPageHost
{
    /// <summary>未被挂载的左栏控制器，仅供需要访问启动状态的地方使用。</summary>
    public PageLaunchLeft Left { get; } = left;

    public PageId Page => PageIds.Launch;

    public MainTab? Tab => MainTab.Launch;

    public double LeftWidth => 0d;

    public string GetTitle(PageRoute stack)
    {
        return string.Empty;
    }

    public Control? GetLeft(PageRoute stack)
    {
        return null;
    }

    public Control GetRight(PageRoute stack)
    {
        return right;
    }
}
