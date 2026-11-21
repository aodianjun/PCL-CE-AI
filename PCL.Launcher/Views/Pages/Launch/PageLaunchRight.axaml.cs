using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.Ai;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Minecraft.Profile;
using PCL.Navigation;
using PCL.UI.Controls;

namespace PCL.Views.Pages.Launch;

/// <summary>
/// 启动页右栏：以 AI 对话为主界面，底部只保留左下角设置、账号、版本选择与启动按钮。
/// 原启动页左栏的实例/账号控制器仍然存在，但被收进账号抽屉，由本页面统一驱动。
/// </summary>
public partial class PageLaunchRight : MyPageRight, IRefreshable
{
    private static bool _onboardingShownThisRun;

    private readonly Dictionary<AiChatMessage, TextBlock> _bubbles = [];
    private readonly Dictionary<AiChatMessage, Border> _bubbleBorders = [];
    private readonly List<string> _launchLogBuffer = [];

    private readonly Panel _panInput;
    private readonly Panel _panLaunching;
    private readonly MyScrollViewer _sclChat;
    private readonly StackPanel _panMessages;
    private readonly MyTextBox _txtInput;
    private readonly MyButton _btnLaunch;
    private readonly MyButton _btnAccount;
    private readonly MyButton _btnVersion;
    private readonly MyIconButton _btnSend;
    private readonly MyIconButton _btnCancelReply;
    private readonly TextBlock _labModel;
    private readonly TextBlock _labLaunchingStage;
    private readonly ProgressBar _barLaunch;
    private readonly Grid _panDrawerLayer;
    private readonly ContentControl _panDrawerHost;
    private readonly Grid _panConvLayer;
    private readonly StackPanel _panConversations;

    private PageLaunchLeftViewModel? _viewModel;
    private PageLaunchLeft? _leftPanel;
    private bool _welcomeShown;
    private bool _eventsAttached;

    public PageLaunchRight()
    {
        InitializeComponent();
        Current = this;

        _panInput = this.FindControl<Panel>("PanInput")!;
        _panLaunching = this.FindControl<Panel>("PanLaunching")!;
        _sclChat = this.FindControl<MyScrollViewer>("SclChat")!;
        _panMessages = this.FindControl<StackPanel>("PanMessages")!;
        _txtInput = this.FindControl<MyTextBox>("TxtInput")!;
        _btnLaunch = this.FindControl<MyButton>("BtnLaunch")!;
        _btnAccount = this.FindControl<MyButton>("BtnAccount")!;
        _btnVersion = this.FindControl<MyButton>("BtnVersion")!;
        _btnSend = this.FindControl<MyIconButton>("BtnSend")!;
        _btnCancelReply = this.FindControl<MyIconButton>("BtnCancelReply")!;
        _labModel = this.FindControl<TextBlock>("LabModel")!;
        _labLaunchingStage = this.FindControl<TextBlock>("LabLaunchingStage")!;
        _barLaunch = this.FindControl<ProgressBar>("BarLaunch")!;
        _panDrawerLayer = this.FindControl<Grid>("PanDrawerLayer")!;
        _panDrawerHost = this.FindControl<ContentControl>("PanDrawerHost")!;
        _panConvLayer = this.FindControl<Grid>("PanConvLayer")!;
        _panConversations = this.FindControl<StackPanel>("PanConversations")!;

        _txtInput.KeyDown += Input_OnKeyDown;
        PanScroll = _sclChat;

        _btnLaunch.Click += Launch_OnClick;
        _btnAccount.Click += Account_OnClick;
        _btnVersion.Click += Version_OnClick;
        _btnSend.Click += Send_OnClick;
        _btnCancelReply.Click += CancelReply_OnClick;
        this.FindControl<MyIconButton>("BtnClearChat")!.Click += ClearChat_OnClick;
        this.FindControl<MyIconButton>("BtnNewChat")!.Click += NewChat_OnClick;
        this.FindControl<MyIconButton>("BtnConversations")!.Click += Conversations_OnClick;
        this.FindControl<MyIconButton>("BtnCompress")!.Click += Compress_OnClick;
        this.FindControl<MyIconButton>("BtnNewChatInList")!.Click += NewChat_OnClick;
        this.FindControl<MyIconButton>("BtnCloseConv")!.Click += CloseConv_OnClick;
        this.FindControl<MyIconButton>("BtnSettings")!.Click += Settings_OnClick;
        this.FindControl<MyButton>("BtnCancelLaunch")!.Click += CancelLaunch_OnClick;
        this.FindControl<MyIconButton>("BtnCloseDrawer")!.Click += CloseDrawer_OnClick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        RefreshModelLabel(null);
        RenderExistingHistory();
        UpdateSendState();
    }

    public static PageLaunchRight? Current { get; private set; }

    /// <inheritdoc />
    public void Refresh()
    {
        RefreshLaunchUi();
    }

    /// <summary>兼容旧的启动日志追加接口：日志只写入本地日志，不再占用主界面。</summary>
    public void AppendLaunchLog(string text)
    {
        lock (_launchLogBuffer)
        {
            _launchLogBuffer.Add(text);
            if (_launchLogBuffer.Count > 500)
                _launchLogBuffer.RemoveRange(0, _launchLogBuffer.Count - 500);
        }
    }

    public void ClearLaunchLog()
    {
        lock (_launchLogBuffer)
        {
            _launchLogBuffer.Clear();
        }
    }

    /// <summary>兼容旧主页的强制刷新入口。</summary>
    public void ForceRefresh()
    {
        RefreshLaunchUi();
    }

    /// <inheritdoc />
    public override void OnNavigatedTo(PageParameter? parameter)
    {
        AttachLaunchController();
        RefreshLaunchUi();
        UpdateAccountText();
    }

    /// <inheritdoc />
    public override void OnNavigatedFrom()
    {
        CloseDrawer();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (!_eventsAttached)
        {
            _eventsAttached = true;
            AiChatService.Instance.MessageAdded += OnAiMessageAdded;
            AiChatService.Instance.MessageUpdated += OnAiMessageUpdated;
            AiChatService.Instance.HistoryCleared += OnAiHistoryCleared;
            AiChatService.Instance.BusyChanged += OnAiBusyChanged;
            AiChatService.Instance.StatusChanged += OnAiStatusChanged;
            AiChatService.Instance.ConversationsChanged += OnConversationsChanged;
            AiChatService.Instance.ConversationSwitched += OnConversationSwitched;
        }

        AttachLaunchController();
        RefreshLaunchUi();
        UpdateAccountText();
        SyncHistory();
        if (!_welcomeShown)
        {
            _welcomeShown = true;
            ShowWelcome();
        }

        CheckOnboarding();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_eventsAttached)
        {
            _eventsAttached = false;
            AiChatService.Instance.MessageAdded -= OnAiMessageAdded;
            AiChatService.Instance.MessageUpdated -= OnAiMessageUpdated;
            AiChatService.Instance.HistoryCleared -= OnAiHistoryCleared;
            AiChatService.Instance.BusyChanged -= OnAiBusyChanged;
            AiChatService.Instance.StatusChanged -= OnAiStatusChanged;
            AiChatService.Instance.ConversationsChanged -= OnConversationsChanged;
            AiChatService.Instance.ConversationSwitched -= OnConversationSwitched;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnLaunchViewModelChanged;
            _viewModel.RefreshPageRequested -= OnProfileRefreshRequested;
            // 下次挂载时重新订阅，避免页面离开后丢失状态更新。
            _viewModel = null;
        }

    }

    #region 启动控制器与底部操作栏

    private void AttachLaunchController()
    {
        try
        {
            var left = PageLaunchLeft.Current;
            if (left is not null && _leftPanel is null)
            {
                // 左栏只保留账号区域，启动与实例按钮由底部操作栏接管。
                if (left.FindControl<Grid>("PanLaunch") is { } panLaunch)
                    panLaunch.IsVisible = false;
                if (left.FindControl<MyButton>("BtnInstance") is { } btnInstance)
                    btnInstance.IsVisible = false;
                if (left.FindControl<MyButton>("BtnMore") is { } btnMore)
                    btnMore.IsVisible = false;
                _panDrawerHost.Content = left;
                _leftPanel = left;
            }

            if (left is not null && _viewModel is null)
            {
                _viewModel = left.ViewModel;
                _viewModel.PropertyChanged += OnLaunchViewModelChanged;
                _viewModel.RefreshPageRequested += OnProfileRefreshRequested;
            }

            _viewModel?.InitializePage();
        }
        catch (Exception ex)
        {
            LogWrapper.Warn("UI", $"初始化启动控制器失败：{ex.Message}");
        }
    }

    private void OnLaunchViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshLaunchUi);
            return;
        }

        RefreshLaunchUi();
    }

    private void OnProfileRefreshRequested()
    {
        Dispatcher.UIThread.Post(() =>
        {
            RefreshLaunchUi();
            UpdateAccountText();
        });
    }

    private void RefreshLaunchUi()
    {
        if (_viewModel is null)
            return;

        try
        {
            _btnLaunch.Text = _viewModel.LaunchButtonText;
            _btnLaunch.IsEnabled = _viewModel.IsLaunchButtonEnabled && !_viewModel.IsShowingLaunching;
            _btnVersion.Text = _viewModel.VersionNameText;

            var launching = _viewModel.IsShowingLaunching;
            _panLaunching.IsVisible = launching;
            _panInput.IsVisible = !launching;
            if (launching)
            {
                _labLaunchingStage.Text =
                    _viewModel.LaunchingStageText + " · " + _viewModel.LaunchingProgressText;
                _barLaunch.Value = Math.Clamp(_viewModel.ShowProgress, 0d, 1d);
            }
        }
        catch (Exception ex)
        {
            LogWrapper.Debug(ex, "UI", "刷新启动按钮失败");
        }
    }

    private void UpdateAccountText()
    {
        try
        {
            var profile = ProfileService.Current;
            _btnAccount.Text = profile?.LoginName is { Length: > 0 } name
                ? name
                : Lang.Text("Launch.Ai.Account.Login");
        }
        catch (Exception)
        {
            _btnAccount.Text = Lang.Text("Launch.Ai.Account.Login");
        }
    }

    private void Launch_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        _viewModel?.LaunchCommand.Execute(null);
    }

    private void Version_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        _viewModel?.SelectInstanceCommand.Execute(null);
    }

    private void Settings_OnClick(object? sender, EventArgs e)
    {
        PageRouter.Navigate(PageIds.Setup);
    }

    private void Account_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        AttachLaunchController();
        _panDrawerLayer.IsVisible = true;
    }

    private void CloseDrawer_OnClick(object? sender, EventArgs e)
    {
        CloseDrawer();
        UpdateAccountText();
    }

    private void DrawerMask_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseDrawer();
        UpdateAccountText();
    }

    private void CloseDrawer()
    {
        _panDrawerLayer.IsVisible = false;
    }

    private void CancelLaunch_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        _viewModel?.CancelLaunchCommand.Execute(null);
    }

    #endregion

    #region AI 对话

    private void UpdateSendState()
    {
        var busy = AiChatService.Instance.IsBusy;
        _btnSend.IsEnabled = !busy;
        _btnCancelReply.IsVisible = busy;
    }

    private void Send_OnClick(object? sender, EventArgs e)
    {
        SendCurrentInput();
    }

    private void Input_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;

        SendCurrentInput();
        e.Handled = true;
    }

    private void SendCurrentInput()
    {
        var text = _txtInput.Text?.Trim() ?? "";
        if (text.Length == 0)
            return;

        if (!AiChatService.IsConfigured)
        {
            AddLocalBubble(Lang.Text("Launch.Ai.NotConfigured"), true);
            PageRouter.Navigate(PageIds.Setup, SubPageIds.SetupAi);
            return;
        }

        _txtInput.Text = "";
        _ = AiChatService.Instance.SendAsync(text);
    }

    private void CancelReply_OnClick(object? sender, EventArgs e)
    {
        AiChatService.Instance.Cancel();
    }

    private void ClearChat_OnClick(object? sender, EventArgs e)
    {
        AiChatService.Instance.Clear();
    }

    private void NewChat_OnClick(object? sender, EventArgs e)
    {
        AiChatService.Instance.NewConversation();
        CloseConversationList();
    }

    private void Conversations_OnClick(object? sender, EventArgs e)
    {
        if (_panConvLayer.IsVisible)
        {
            CloseConversationList();
            return;
        }

        BuildConversationList();
        _panConvLayer.IsVisible = true;
    }

    private void Compress_OnClick(object? sender, EventArgs e)
    {
        _ = AiChatService.Instance.CompressAsync();
    }

    private void CloseConv_OnClick(object? sender, EventArgs e)
    {
        CloseConversationList();
    }

    private void ConvMask_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseConversationList();
    }

    private void CloseConversationList()
    {
        _panConvLayer.IsVisible = false;
    }

    private void BuildConversationList()
    {
        _panConversations.Children.Clear();
        var conversations = AiChatService.Instance.Conversations;
        if (conversations.Count == 0)
        {
            _panConversations.Children.Add(new TextBlock
            {
                Text = Lang.Text("Launch.Ai.Conversations.Empty"),
                FontSize = 12,
                Opacity = 0.6,
                Margin = new Thickness(10, 12, 10, 0)
            });
            return;
        }

        var currentId = AiChatService.Instance.CurrentConversation.Id;
        foreach (var conversation in conversations)
        {
            var title = string.IsNullOrWhiteSpace(conversation.Title)
                ? Lang.Text("Launch.Ai.NewChat.DefaultTitle")
                : conversation.Title;
            var item = new MyListItem
            {
                Title = title,
                Info = conversation.UpdatedAt.ToString("MM-dd HH:mm"),
                Tag = conversation,
                Type = MyListItem.CheckType.Clickable,
                Checked = conversation.Id == currentId
            };
            item.Click += (_, _) =>
            {
                AiChatService.Instance.SelectConversation(conversation.Id);
                CloseConversationList();
            };
            _panConversations.Children.Add(item);
        }
    }

    private void OnConversationsChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_panConvLayer.IsVisible)
                BuildConversationList();
        });
    }

    private void OnConversationSwitched()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _bubbles.Clear();
            _bubbleBorders.Clear();
            _panMessages.Children.Clear();
            if (AiChatService.Instance.Messages.Count == 0)
                ShowWelcome();
            else
                RenderExistingHistory();
        });
    }

    private void OnAiMessageAdded(AiChatMessage message)
    {
        Dispatcher.UIThread.Post(() => AddBubble(message));
    }

    private void OnAiMessageUpdated(AiChatMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_bubbles.TryGetValue(message, out var text))
            {
                text.Text = message.Content;
                ScrollToEnd();
            }
        });
    }

    private void OnAiHistoryCleared()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _bubbles.Clear();
            _bubbleBorders.Clear();
            _panMessages.Children.Clear();
            if (AiChatService.Instance.Messages.Count == 0)
            {
                ShowWelcome();
                return;
            }

            RenderExistingHistory();
        });
    }

    private void OnAiBusyChanged(bool busy)
    {
        Dispatcher.UIThread.Post(UpdateSendState);
    }

    private void OnAiStatusChanged(string? status)
    {
        Dispatcher.UIThread.Post(() => RefreshModelLabel(status));
    }

    private void RefreshModelLabel(string? status)
    {
        string model;
        try
        {
            model = string.IsNullOrWhiteSpace(Config.Ai.Model)
                ? "deepseek-chat"
                : Config.Ai.Model.Trim();
        }
        catch (Exception)
        {
            model = "deepseek-chat";
        }

        _labModel.Text = status ?? "模型 · " + model;
    }

    private void RenderExistingHistory()
    {
        foreach (var message in AiChatService.Instance.Messages.ToArray())
            if (!message.IsHidden)
                AddBubble(message, false);
    }

    /// <summary>页面重新挂载时补齐离开期间新增的消息。</summary>
    private void SyncHistory()
    {
        foreach (var message in AiChatService.Instance.Messages.ToArray())
            if (!message.IsHidden && !_bubbles.ContainsKey(message))
                AddBubble(message, false);
    }

    private void AddBubble(AiChatMessage message, bool scroll = true)
    {
        if (message.IsHidden)
            return;
        if (message.Role == AiRole.Tool)
            return;
        if (_bubbles.ContainsKey(message))
            return;

        var text = new TextBlock
        {
            Text = message.Content,
            Classes = { "AiBubbleText" },
            MaxWidth = 620
        };

        var border = new Border
        {
            Classes = { "AiBubble" },
            Child = text
        };

        if (message.Role == AiRole.User)
        {
            border.Classes.Add("AiBubbleUser");
            text.Classes.Add("AiBubbleTextUser");
        }
        else
        {
            border.Classes.Add(message.IsError ? "AiBubbleError" : "AiBubbleAssistant");
        }

        _bubbles[message] = text;
        _bubbleBorders[message] = border;
        _panMessages.Children.Add(border);
        if (scroll)
            ScrollToEnd();
    }

    private void AddLocalBubble(string text, bool error)
    {
        var message = new AiChatMessage
        {
            Role = AiRole.Assistant,
            Content = text,
            IsError = error
        };
        AddBubble(message);
    }

    private void ShowWelcome()
    {
        var welcome = new AiChatMessage
        {
            Role = AiRole.Assistant,
            Content = Lang.Text("Launch.Ai.Welcome")
        };
        AddBubble(welcome, false);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(2, 2, 0, 4)
        };
        AddQuickAction(actions, Lang.Text("Launch.Ai.Quick.Launch"), "启动游戏");
        AddQuickAction(actions, Lang.Text("Launch.Ai.Quick.Download"), "帮我下载安装 Minecraft 1.21.1");
        AddQuickAction(actions, Lang.Text("Launch.Ai.Quick.Mods"), "列出当前版本的 Mod，并告诉我哪些可能不兼容");

        if (!AiChatService.IsConfigured)
        {
            var button = new MyTextButton
            {
                Text = Lang.Text("Launch.Ai.Quick.Configure")
            };
            button.Click += (_, _) => PageRouter.Navigate(PageIds.Setup, SubPageIds.SetupAi);
            actions.Children.Add(button);
        }

        _panMessages.Children.Add(actions);
        ScrollToEnd();
    }

    private static void AddQuickAction(Panel host, string label, string prompt)
    {
        var button = new MyTextButton
        {
            Text = label
        };
        button.Click += (_, _) =>
        {
            if (!AiChatService.IsConfigured)
                return;
            _ = AiChatService.Instance.SendAsync(prompt);
        };
        host.Children.Add(button);
    }

    private void ScrollToEnd()
    {
        try
        {
            Dispatcher.UIThread.Post(() => _sclChat.ScrollToEnd(), DispatcherPriority.Background);
        }
        catch (Exception)
        {
            // 忽略滚动失败。
        }
    }

    private void CheckOnboarding()
    {
        if (_onboardingShownThisRun)
            return;

        if (AiChatService.IsConfigured)
        {
            Config.Ai.OnboardingDone = true;
            return;
        }

        if (Config.Ai.OnboardingDone)
            return;

        _onboardingShownThisRun = true;
        Dispatcher.UIThread.Post(
            () => PageRouter.Navigate(PageIds.Setup, SubPageIds.SetupAi),
            DispatcherPriority.Background);
    }

    #endregion
}
