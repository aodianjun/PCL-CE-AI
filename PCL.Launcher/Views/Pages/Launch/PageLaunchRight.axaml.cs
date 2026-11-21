using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PCL.Ai;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Minecraft.Profile;
using PCL.Core.UI;
using PCL.Core.UI.Dialogs;
using PCL.Core.UI.Hints;
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

    private readonly Dictionary<AiChatMessage, BubbleView> _bubbles = [];
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
    private readonly TextBlock _labContextUsage;
    private readonly StackPanel _panThinking;
    private readonly DispatcherTimer _thinkingTimer;
    private readonly RotateTransform[] _thinkingRotates = [new(), new(), new()];
    private readonly double[] _thinkingAngles = [0d, 0d, 0d];
    private readonly TextBlock _labLaunchingStage;
    private readonly ProgressBar _barLaunch;
    private readonly Grid _panDrawerLayer;
    private readonly ContentControl _panDrawerHost;
    private readonly Grid _panConvLayer;
    private readonly StackPanel _panConversations;
    private readonly WrapPanel _panPendingAttachments;
    private readonly List<AiAttachment> _pendingAttachments = [];

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
        _labContextUsage = this.FindControl<TextBlock>("LabContextUsage")!;
        _panThinking = this.FindControl<StackPanel>("PanThinking")!;
        var thinkingImages = _panThinking.Children.OfType<Image>().ToArray();
        for (var i = 0; i < thinkingImages.Length && i < _thinkingRotates.Length; i++)
            thinkingImages[i].RenderTransform = _thinkingRotates[i];

        _thinkingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _thinkingTimer.Tick += (_, _) =>
        {
            for (var i = 0; i < _thinkingRotates.Length; i++)
            {
                _thinkingAngles[i] = (_thinkingAngles[i] + 9 + i * 3) % 360;
                _thinkingRotates[i].Angle = _thinkingAngles[i];
            }
        };
        _labLaunchingStage = this.FindControl<TextBlock>("LabLaunchingStage")!;
        _barLaunch = this.FindControl<ProgressBar>("BarLaunch")!;
        _panDrawerLayer = this.FindControl<Grid>("PanDrawerLayer")!;
        _panDrawerHost = this.FindControl<ContentControl>("PanDrawerHost")!;
        _panConvLayer = this.FindControl<Grid>("PanConvLayer")!;
        _panConversations = this.FindControl<StackPanel>("PanConversations")!;
        _panPendingAttachments = this.FindControl<WrapPanel>("PanPendingAttachments")!;

        _txtInput.AddHandler(
            InputElement.KeyDownEvent,
            Input_OnKeyDown,
            RoutingStrategies.Tunnel);
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
        this.FindControl<MyIconButton>("BtnAttachFiles")!.Click += AttachFiles_OnClick;
        _txtInput.PastingFromClipboard += OnPastingFromClipboard;
        this.FindControl<MyIconButton>("BtnSettings")!.Click += Settings_OnClick;
        this.FindControl<MyButton>("BtnCancelLaunch")!.Click += CancelLaunch_OnClick;
        this.FindControl<MyIconButton>("BtnCloseDrawer")!.Click += CloseDrawer_OnClick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        RefreshModelLabel(null);
        RefreshContextUsage();
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
            AiChatService.Instance.ContextUsageChanged += OnContextUsageChanged;
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
            AiChatService.Instance.ContextUsageChanged -= OnContextUsageChanged;
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
        if (text.Length == 0 && _pendingAttachments.Count == 0)
            return;

        if (!AiChatService.IsConfigured)
        {
            AddLocalBubble(Lang.Text("Launch.Ai.NotConfigured"), true);
            PageRouter.Navigate(PageIds.Setup, SubPageIds.SetupAi);
            return;
        }

        if (AiChatService.Instance.IsBusy)
        {
            AddLocalBubble(Lang.Text("Launch.Ai.Compress.Busy"), true);
            return;
        }

        var attachments = _pendingAttachments.ToList();
        _txtInput.Text = "";
        _pendingAttachments.Clear();
        RebuildPendingAttachments();
        _ = AiChatService.Instance.SendAsync(
            text.Length > 0 ? text : Lang.Text("Launch.Ai.Attachment.Placeholder"),
            attachments);
    }

    private void AttachFiles_OnClick(object? sender, EventArgs e)
    {
        _ = AttachFilesAsync();
    }

    private async Task AttachFilesAsync()
    {
        const int maxAttachments = 6;
        if (_pendingAttachments.Count >= maxAttachments)
        {
            HintService.Show(Hints.Warning(Lang.Text("Launch.Ai.Attachment.Limit")));
            return;
        }

        var files = await SystemDialogs.SelectFilesAsync(
            "图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|文本文件|*.txt;*.md;*.json;*.log;*.yml;*.yaml;*.toml;*.ini;*.cfg;*.conf;*.xml;*.html;*.css;*.js;*.ts;*.cs;*.java;*.kt;*.py;*.sh;*.ps1;*.bat;*.csv;*.mcmeta;*.lang|所有文件|*.*",
            Lang.Text("Launch.Ai.Attach"));
        if (files.Length == 0)
            return;

        foreach (var file in files)
        {
            if (_pendingAttachments.Count >= maxAttachments)
            {
                HintService.Show(Hints.Warning(Lang.Text("Launch.Ai.Attachment.Limit")));
                break;
            }

            AddAttachmentFromPath(file);
        }

        RebuildPendingAttachments();
    }

    private void OnPastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        _ = PasteFromClipboardAsync(textBox, clipboard, e);
    }

    private async Task PasteFromClipboardAsync(
        TextBox textBox,
        IClipboard clipboard,
        RoutedEventArgs e)
    {
        try
        {
            var files = await clipboard.TryGetFilesAsync();
            var paths = files?.Select(item => item?.Path is { IsAbsoluteUri: true, Scheme: "file" } uri
                    ? uri.LocalPath
                    : null)
                .OfType<string>()
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray() ?? [];
            if (paths.Length > 0)
            {
                e.Handled = true;
                foreach (var path in paths)
                {
                    if (_pendingAttachments.Count >= 6)
                    {
                        HintService.Show(Hints.Warning(Lang.Text("Launch.Ai.Attachment.Limit")));
                        break;
                    }

                    AddAttachmentFromPath(path);
                }

                RebuildPendingAttachments();
                return;
            }

            var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is not null)
            {
                e.Handled = true;
                AddBitmapAttachment(bitmap);
                return;
            }

            // 没有文件或图片时保持默认文本粘贴行为。
        }
        catch (Exception ex)
        {
            LogWrapper.Debug(ex, "UI", "读取剪贴板附件失败");
        }
    }

    private void AddBitmapAttachment(Bitmap bitmap)
    {
        if (_pendingAttachments.Count >= 6)
        {
            HintService.Show(Hints.Warning(Lang.Text("Launch.Ai.Attachment.Limit")));
            return;
        }

        try
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            _pendingAttachments.Add(new AiAttachment
            {
                Name = Lang.Text("Launch.Ai.Attachment.PastedImage")
                       + " " + DateTime.Now.ToString("HHmmss") + ".png",
                IsImage = true,
                DataUrl = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray())
            });
            RebuildPendingAttachments();
        }
        catch (Exception ex)
        {
            HintService.Show(Hints.Warning(Lang.Text("Launch.Ai.Attachment.Failed", ex.Message)));
        }
    }

    private bool AddAttachmentFromPath(string file)
    {
        try
        {
            if (Directory.Exists(file))
                return false;

            var info = new FileInfo(file);
            if (!info.Exists)
                return false;

            var extension = Path.GetExtension(file).ToLowerInvariant();
            var imageExtensions = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
            if (imageExtensions.Contains(extension))
            {
                if (info.Length > 8 * 1024 * 1024)
                {
                    HintService.Show(Hints.Warning(
                        Lang.Text("Launch.Ai.Attachment.TooLarge", info.Name)));
                    return false;
                }

                var mime = extension switch
                {
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".bmp" => "image/bmp",
                    _ => "image/jpeg"
                };
                var data = Convert.ToBase64String(File.ReadAllBytes(file));
                _pendingAttachments.Add(new AiAttachment
                {
                    Name = info.Name,
                    IsImage = true,
                    DataUrl = $"data:{mime};base64,{data}"
                });
                return true;
            }

            if (info.Length > 1024 * 1024)
            {
                HintService.Show(Hints.Warning(
                    Lang.Text("Launch.Ai.Attachment.TooLarge", info.Name)));
                return false;
            }

            var text = File.ReadAllText(file);
            if (text.Length > 200000)
                text = text[..200000] + Environment.NewLine + "…（已截断）";
            _pendingAttachments.Add(new AiAttachment
            {
                Name = info.Name,
                TextContent = text
            });
            return true;
        }
        catch (Exception ex)
        {
            HintService.Show(Hints.Warning(
                Lang.Text("Launch.Ai.Attachment.Failed", ex.Message)));
            return false;
        }
    }

    private void RebuildPendingAttachments()
    {
        _panPendingAttachments.Children.Clear();
        _panPendingAttachments.IsVisible = _pendingAttachments.Count > 0;
        foreach (var attachment in _pendingAttachments.ToList())
        {
            var label = new TextBlock
            {
                Text = (attachment.IsImage ? "🖼 " : "📎 ") + attachment.Name,
                FontSize = 11,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var remove = new MyIconButton
            {
                Width = 18,
                Height = 18,
                LogoScale = 0.7,
                SvgIcon = "lucide/x"
            };
            remove.Click += (_, _) =>
            {
                _pendingAttachments.Remove(attachment);
                RebuildPendingAttachments();
            };
            var chipPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4
            };
            chipPanel.Children.Add(label);
            chipPanel.Children.Add(remove);
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = chipPanel
            };
            _panPendingAttachments.Children.Add(chip);
        }
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
            _panMessages.Children.Clear();
            if (AiChatService.Instance.Messages.Count == 0)
                ShowWelcome();
            else
                RenderExistingHistory();
        });
    }

    private void OnContextUsageChanged()
    {
        Dispatcher.UIThread.Post(RefreshContextUsage);
    }

    private void RefreshContextUsage()
    {
        try
        {
            var usage = AiChatService.Instance.GetContextUsage();
            _labContextUsage.Text = Lang.Text(
                "Launch.Ai.Context.Short",
                FormatTokenCount(usage.EstimatedTokens),
                FormatTokenCount(usage.Limit),
                usage.Percent.ToString("0"));
        }
        catch (Exception)
        {
            _labContextUsage.Text = "";
        }
    }

    private static string FormatTokenCount(int value)
    {
        return value >= 1000
            ? (value / 1000d).ToString("0.#") + "k"
            : value.ToString();
    }

    private void ContextUsage_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var usage = AiChatService.Instance.GetContextUsage();
        var threshold = Math.Max(4000, (int)(usage.Limit * 0.6));

        var content = string.Join(
            Environment.NewLine,
            Lang.Text("Launch.Ai.Context.MessageCount", usage.MessageCount),
            Lang.Text("Launch.Ai.Context.Characters", usage.Characters),
            Lang.Text("Launch.Ai.Context.EstimatedTokens", usage.EstimatedTokens),
            Lang.Text("Launch.Ai.Context.Limit", usage.Limit),
            Lang.Text("Launch.Ai.Context.Percent", usage.Percent.ToString("0.0")),
            Lang.Text("Launch.Ai.Context.Threshold", threshold),
            Lang.Text(
                "Launch.Ai.Context.HasSummary",
                usage.HasSummary ? Lang.Text("Common.Option.Yes") : Lang.Text("Common.Option.No")));

        _ = DialogService.Instance.ShowAsync(new TextDialogRequest
        {
            Title = Lang.Text("Launch.Ai.Context.Title"),
            Content = content,
            Buttons = DialogButtons.Ok
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
            if (!_bubbles.TryGetValue(message, out var bubble))
                return;

            if (message.Role == AiRole.Tool)
            {
                bubble.Content.Text = ComposeToolBody(message);
            }
            else if (message.Role == AiRole.Assistant)
            {
                bubble.Content.Text = string.IsNullOrWhiteSpace(message.Content)
                    ? Lang.Text("Launch.Ai.Thinking")
                    : message.Content;
                if (!string.IsNullOrWhiteSpace(message.Reasoning))
                    EnsureReasoningBlock(bubble, message);
            }
            else
            {
                bubble.Content.Text = message.Content;
            }

            ScrollToEnd();
        });
    }

    private void OnAiHistoryCleared()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _bubbles.Clear();
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
        Dispatcher.UIThread.Post(() =>
        {
            UpdateSendState();
            _panThinking.IsVisible = busy;
            if (busy)
            {
                _thinkingTimer.Start();
            }
            else
            {
                _thinkingTimer.Stop();
                for (var i = 0; i < _thinkingRotates.Length; i++)
                {
                    _thinkingAngles[i] = 0d;
                    _thinkingRotates[i].Angle = 0d;
                }
            }
        });
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
        if (_bubbles.ContainsKey(message))
            return;

        var panel = new StackPanel
        {
            Spacing = 4
        };
        var content = new TextBlock
        {
            Classes = { "AiBubbleText" },
            MaxWidth = 620
        };
        var border = new Border
        {
            Classes = { "AiBubble" },
            Child = panel
        };
        var bubble = new BubbleView
        {
            Border = border,
            Panel = panel,
            Content = content
        };

        if (message.Role == AiRole.User)
        {
            var display = message.Content ?? "";
            if (message.Attachments.Count > 0)
            {
                var names = string.Join(
                    Environment.NewLine,
                    message.Attachments.Select(attachment =>
                        (attachment.IsImage ? "🖼 " : "📎 ") + attachment.Name));
                display = string.IsNullOrWhiteSpace(display)
                    ? names
                    : display + Environment.NewLine + names;
            }

            content.Text = display;
            content.Classes.Add("AiBubbleTextUser");
            border.Classes.Add("AiBubbleUser");
            panel.Children.Add(content);
        }
        else if (message.Role == AiRole.Tool)
        {
            var label = Lang.Text("Launch.Ai.ToolExecution", message.ToolName ?? "tool");
            var header = new TextBlock
            {
                Text = "▶ " + label,
                FontSize = 11.5,
                Opacity = 0.85
            };
            var body = new TextBlock
            {
                Text = ComposeToolBody(message),
                Classes = { "AiBubbleText", "AiBubbleToolText" },
                MaxWidth = 620,
                IsVisible = false
            };
            header.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    return;
                body.IsVisible = !body.IsVisible;
                header.Text = (body.IsVisible ? "▼ " : "▶ ") + label;
                e.Handled = true;
            };
            panel.Children.Add(header);
            panel.Children.Add(body);
            border.Classes.Add("AiBubbleTool");
            bubble.Content = body;
        }
        else
        {
            border.Classes.Add(message.IsError ? "AiBubbleError" : "AiBubbleAssistant");
            content.Text = string.IsNullOrWhiteSpace(message.Content)
                ? Lang.Text("Launch.Ai.Thinking")
                : message.Content;
            panel.Children.Add(content);
        }

        _bubbles[message] = bubble;
        _panMessages.Children.Add(border);

        if (message.Role == AiRole.Assistant && !string.IsNullOrWhiteSpace(message.Reasoning))
            EnsureReasoningBlock(bubble, message);

        if (scroll)
            ScrollToEnd();
    }

    private void EnsureReasoningBlock(BubbleView bubble, AiChatMessage message)
    {
        if (bubble.ReasoningBody is null)
        {
            var header = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.65
            };
            var body = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 600
            };
            header.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    return;
                ToggleReasoning(bubble);
                e.Handled = true;
            };

            bubble.Panel.Children.Insert(0, body);
            bubble.Panel.Children.Insert(0, header);
            bubble.ReasoningHeader = header;
            bubble.ReasoningBody = body;
        }

        bubble.ReasoningBody.Text = message.Reasoning;
        if (!bubble.ReasoningManuallyToggled)
        {
            var expanded = string.IsNullOrWhiteSpace(message.Content);
            bubble.ReasoningBody.IsVisible = expanded;
            if (bubble.ReasoningHeader is not null)
                bubble.ReasoningHeader.Text =
                    (expanded ? "▼ " : "▶ ") + Lang.Text("Launch.Ai.Reasoning");
        }
    }

    private void ToggleReasoning(BubbleView bubble)
    {
        if (bubble.ReasoningBody is null)
            return;

        var expanded = !bubble.ReasoningBody.IsVisible;
        bubble.ReasoningBody.IsVisible = expanded;
        bubble.ReasoningManuallyToggled = true;
        if (bubble.ReasoningHeader is not null)
            bubble.ReasoningHeader.Text =
                (expanded ? "▼ " : "▶ ") + Lang.Text("Launch.Ai.Reasoning");
    }

    private static string ComposeToolBody(AiChatMessage message)
    {
        var nl = Environment.NewLine;
        var result = message.Content ?? "";
        if (result.Length > 8000)
            result = result[..8000] + nl + "…（已截断）";

        return string.IsNullOrWhiteSpace(message.ToolArguments)
            ? result
            : message.ToolArguments + nl + "→" + nl + result;
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

    private sealed class BubbleView
    {
        public required Border Border { get; init; }

        public required StackPanel Panel { get; init; }

        public required TextBlock Content { get; set; }

        public TextBlock? ReasoningHeader { get; set; }

        public TextBlock? ReasoningBody { get; set; }

        public bool ReasoningManuallyToggled { get; set; }
    }
}
