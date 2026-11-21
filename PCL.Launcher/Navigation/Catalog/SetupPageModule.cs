using Microsoft.Extensions.DependencyInjection;
using PCL.Core.App;
using PCL.Views.Pages.Setup;
using static PCL.Navigation.PageDescriptorFactory;

namespace PCL.Navigation.Catalog;

public sealed class SetupPageModule : IPageModule
{
    private static SubPageDescriptor[] SetupSubPages { get; } =
    [
        Sub(
            PageIds.Setup,
            SubPageIds.SetupFeatures,
            "Setup.Left.Item.Features",
            "lucide/layout-dashboard",
            "features",
            0,
            (sp, _) => ActivatorUtilities.CreateInstance<PageSetupFeatures>(sp)),
        Sub(
            PageIds.Setup,
            SubPageIds.SetupAi,
            "Setup.Left.Item.Ai",
            "lucide/sparkles",
            "features",
            1,
            (sp, _) => ActivatorUtilities.CreateInstance<PageSetupAi>(sp)),
        Sub(
            PageIds.Setup,
            SubPageIds.SetupLaunch,
            "Setup.Left.Item.Launch",
            "lucide/rocket",
            "game",
            2,
            (sp, _) => ActivatorUtilities.CreateInstance<PageSetupLaunch>(sp),
            () => !Config.Preference.Hide.SetupLaunch,
            reset: "Setup.Left.Reset.Launch.Message"),
        Sub(
            PageIds.Setup,
            SubPageIds.SetupJava,
            "Setup.Left.Item.Java",
            "lucide/coffee",
            "game",
            3,
            (sp, _) => ActivatorUtilities.CreateInstance<PageSetupJava>(sp),
            () => !Config.Preference.Hide.SetupJava,
            true),
        Sub(
            PageIds.Setup,
            SubPageIds.SetupGameManage,
            "Setup.Left.Item.GameManage",
            "lucide/book-marked",
            "game",
            4,
            (sp, _) => ActivatorUtilities.CreateInstance<PageSetupGameManage>(sp),
            () => !Config.Preference.Hide.SetupGameManage,
            reset: "Setup.Left.Reset.GameManage.Message"),
        Sub(
            PageIds.Setup,
            SubPageIds.SetupGameLink,
            "Setup.Left.Item.GameLink",
            "lucide/bubbles",
            "tools",
            5,
            (sp, _) => ActivatorUtilities.CreateInstance<PageSetupGameLink>(sp),
            () => !Config.Preference.Hide.SetupGameLink,
            reset: "Setup.Left.Reset.GameLink.Message"),
        Sub(
            PageIds.Setup,
            SubPageIds.SetupUi,
            "Setup.Left.Item.Ui",
            "lucide/palette",
            "launcher",
            6,
            (sp, _) => ActivatorUtilities.CreateInstance<PageSetupUI>(sp),
            () => !Config.Preference.Hide.SetupUi,
            reset: "Setup.Left.Reset.Ui.Message"),
        Sub(
            PageIds.Setup,
            SubPageIds.SetupLauncherLanguage,
            "Setup.Language.Title",
            "lucide/earth",
            "launcher",
            7,
            (sp, _) => ActivatorUtilities.CreateInstance<PageSetupLauncherLanguage>(sp),
            () => !Config.Preference.Hide.SetupLauncherLanguage,
            reset: "Setup.Left.Reset.Language.Message"),
        Sub(
            PageIds.Setup,
            SubPageIds.SetupLauncherMisc,
            "Setup.Left.Item.Misc",
            "lucide/monitor-cog",
            "launcher",
            8,
            (sp, _) => ActivatorUtilities.CreateInstance<PageSetupLauncherMisc>(sp),
            () => !Config.Preference.Hide.SetupLauncherMisc,
            reset: "Setup.Left.Reset.Misc.Message"),
        Sub(
            PageIds.Setup,
            SubPageIds.SetupAbout,
            "Setup.Left.Item.About",
            "lucide/info",
            "about",
            9,
            (sp, _) => ActivatorUtilities.CreateInstance<PageSetupAbout>(sp),
            () => !Config.Preference.Hide.SetupAbout),
        Sub(
            PageIds.Setup,
            SubPageIds.SetupUpdate,
            "Setup.Left.Item.Update",
            "lucide/refresh-cw",
            "about",
            10,
            (sp, _) => ActivatorUtilities.CreateInstance<PageSetupUpdate>(sp),
            () => !Config.Preference.Hide.SetupUpdate),
        Sub(
            PageIds.Setup,
            SubPageIds.SetupFeedback,
            "Setup.Left.Item.Feedback",
            "lucide/message-circle",
            "about",
            11,
            (sp, _) => ActivatorUtilities.CreateInstance<PageSetupFeedback>(sp),
            () => !Config.Preference.Hide.SetupFeedback,
            true),
        Sub(
            PageIds.Setup,
            SubPageIds.SetupLog,
            "Setup.Left.Item.Log",
            "lucide/scroll-text",
            "about",
            12,
            (sp, _) => ActivatorUtilities.CreateInstance<PageSetupLog>(sp),
            () => !Config.Preference.Hide.SetupLog)
    ];

    public IReadOnlyList<PageDescriptor> Pages { get; } =
    [
        new(
            PageIds.Setup,
            "Setup.Left.Category.Features",
            "lucide/settings",
            MainTab.Setup,
            152d,
            SubPageIds.SetupFeatures,
            [
                new PageGroupDescriptor("features", "Setup.Left.Category.Features", 0),
                new PageGroupDescriptor("game", "Setup.Left.Category.Game", 1),
                new PageGroupDescriptor("tools", "Setup.Left.Category.Tools", 2),
                new PageGroupDescriptor("launcher", "Setup.Left.Category.Launcher", 3),
                new PageGroupDescriptor("about", "Setup.Left.Category.About", 4)
            ],
            SetupSubPages,
            (sp, descriptor) => ActivatorUtilities.CreateInstance<PageSetupHost>(sp, descriptor))
    ];

    /// <summary>设置不再占据标题栏主入口，统一从启动页左下角的设置按钮进入。</summary>
    public IReadOnlyList<MainTabDescriptor> MainTabs { get; } = [];
}
