using Avalonia.Controls;
using PCL.Navigation;

namespace PCL.Views.Pages.Launch;

/// <summary>
/// 启动页宿主：同一块内容区在“主页内容”和“AI 对话”之间切换。
/// 左栏（<see cref="PageLaunchLeft" />）仍由组合根创建，但只作为账号/启动状态控制器。
/// </summary>
public sealed class PageLaunchHost : IPageHost
{
    private readonly PageLaunchAi _ai;

    public PageLaunchHost(PageLaunchLeft left, PageLaunchRight right, PageLaunchAi ai)
    {
        Left = left;
        _ai = ai;
        ai.AttachHomePage(right);
    }

    /// <summary>未被挂载的左栏控制器，仅供需要访问启动状态的地方使用。</summary>
    public PageLaunchLeft Left { get; }

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
        return _ai;
    }
}