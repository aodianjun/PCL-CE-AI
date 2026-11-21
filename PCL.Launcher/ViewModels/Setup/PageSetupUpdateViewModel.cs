using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.Dialogs;
using PCL.Core.UI.Hints;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;
using PCL.Core.ViewModel;
using PCL.Text;

namespace PCL;

/// <summary>设置页面中「更新」页的视图模型。</summary>
public partial class PageSetupUpdateViewModel : ViewModelBase
{
    private const string GitHubRepo = "aodianjun/PCL-CE-AI";
    private const string GitHubReleasesApi = "https://api.github.com/repos/" + GitHubRepo + "/releases/latest";
    private const string GitHubReleasesPage = "https://github.com/" + GitHubRepo + "/releases";

    private string _changelogSummary = string.Empty;

    private bool _checking;
    private string? _releaseUrl;

    private object?[] _descArgs = [];
    private string _descKey = "Setup.Update.Checking";
    private object?[] _externalUpdateButtonArgs = [];
    private string? _externalUpdateButtonKey;
    private bool _hasStatusText;

    private VersionDataModel? _latestVersion;
    private UpdateButtonState _updateButtonState = UpdateButtonState.Install;

    private double _updateProgress;

    [ObservableProperty] public partial bool ChangelogDetailEnabled { get; set; } = true;

    [ObservableProperty] public partial bool ChannelEnabled { get; set; } = true;

    [ObservableProperty] public partial bool CheckAgainEnabled { get; set; } = true;

    [ObservableProperty] public partial string CurrentVersionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MirrorChyanKey { get; set; } = Config.Update.MirrorChyanKey;

    [ObservableProperty] public partial bool ShowCheckCard { get; set; }

    [ObservableProperty] public partial bool ShowUpdateCard { get; set; }

    [ObservableProperty] public partial bool UpdateButtonEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int UpdateModeIndex { get; set; } = Math.Clamp(
        (int)Config.Update.UpdateMode,
        0,
        3);

    [ObservableProperty] public partial string UpdateNameText { get; set; } = string.Empty;

    public string ChangelogText =>
        !ShowUpdateCard
            ? Lang.Text("Setup.Update.Changelog.Placeholder")
            : string.IsNullOrWhiteSpace(_changelogSummary)
                ? Lang.Text("Setup.Update.Changelog.Empty")
                : _changelogSummary;

    public string CurrentDescText => _hasStatusText ? Lang.Text(_descKey, _descArgs) : string.Empty;

    public string? LatestChangelog => _latestVersion?.Changelog;

    public string UpdateButtonText =>
        _externalUpdateButtonKey is { } externalKey
            ? Lang.Text(externalKey, _externalUpdateButtonArgs)
            : _updateButtonState switch
            {
                UpdateButtonState.Progress => Lang.Number(_updateProgress, "P2"),
                UpdateButtonState.RestartInstall => Lang.Text("Setup.Update.RestartInstall"),
                UpdateButtonState.Flatpak => Lang.Text("Setup.Update.Flatpak"),
                UpdateButtonState.OpenDownload => Lang.Text("Setup.Update.OpenDownload"),
                _ => Lang.Text("Setup.Update.Install")
            };

    /// <summary>Flatpak 包由软件中心更新，不能展示假的安装入口。</summary>
    public static bool IsFlatpak =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLATPAK_ID"));

    private static UpdateButtonState InstallButtonState =>
        IsFlatpak
            ? UpdateButtonState.Flatpak
            : UpdateManager.SupportsInAppUpdate
                ? UpdateButtonState.Install
                : UpdateButtonState.OpenDownload;

    partial void OnMirrorChyanKeyChanged(string value)
    {
        Config.Update.MirrorChyanKey = value ?? string.Empty;
    }

    partial void OnShowUpdateCardChanged(bool value)
    {
        OnPropertyChanged(nameof(ChangelogText));
    }

    partial void OnUpdateModeIndexChanged(int value)
    {
        if (value >= 0) Config.Update.UpdateMode = (LauncherAutoUpdateBehavior)value;
    }

    public void Reload()
    {
        CurrentVersionText = "PCL CE-AI " + VersionNameFormat(LauncherCommon.VersionBaseName);
        SetStatusText("Setup.Update.Source.GitHub");
        MirrorChyanKey = Config.Update.MirrorChyanKey;
        UpdateModeIndex = Math.Clamp((int)Config.Update.UpdateMode, 0, 3);
        SetUpdateButtonState(InstallButtonState);
        UpdateButtonEnabled = !IsFlatpak;
    }

    public async Task CheckUpdateAsync()
    {
        if (_checking) return;
        _checking = true;
        ShowUpdateCard = false;
        ShowCheckCard = true;
        SetStatusText("Setup.Update.Checking");
        CheckAgainEnabled = false;
        _releaseUrl = null;
        try
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-CE-AI");
            using var response = await http.GetAsync(GitHubReleasesApi);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                SetStatusText("Setup.Update.NoRelease");
                return;
            }

            response.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var tag = json?["tag_name"]?.ToString() ?? "";
            var body = json?["body"]?.ToString() ?? "";
            var url = json?["html_url"]?.ToString() ?? GitHubReleasesPage;

            if (string.IsNullOrWhiteSpace(tag) || !IsNewerVersion(tag, LauncherCommon.VersionBaseName))
            {
                SetStatusText("Setup.Update.Latest");
                return;
            }

            _releaseUrl = url;
            _latestVersion = new VersionDataModel
            {
                Changelog = body,
                VersionName = tag.TrimStart('v', 'V'),
                Source = "GitHub"
            };
            UpdateNameText = "PCL CE-AI " + VersionNameFormat(tag);
            _changelogSummary = BuildGithubSummary(body);
            OnPropertyChanged(nameof(ChangelogText));
            SetExternalUpdateButtonText("Setup.Update.OpenDownload");
            UpdateButtonEnabled = true;
            ShowUpdateCard = true;
            ShowCheckCard = false;
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(
                ex,
                Lang.Text("Setup.Update.Error.NetworkFailed"),
                LauncherLogLevel.Hint,
                userSummary: Lang.Text("Setup.Update.Error.NetworkFailed"));
            SetStatusText("Setup.Update.CheckFailed");
        }
        finally
        {
            _checking = false;
            CheckAgainEnabled = true;
        }
    }

    private static bool IsNewerVersion(string tag, string currentVersion)
    {
        static Version? Parse(string value)
        {
            value = value.Trim().TrimStart('v', 'V');
            var core = value.Split('-', '+')[0];
            return Version.TryParse(core, out var version) ? version : null;
        }

        var remote = Parse(tag);
        var local = Parse(currentVersion);
        return remote is not null && local is not null && remote > local;
    }

    private static string BuildGithubSummary(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;
        var text = body.Trim();
        return text.Length <= 400 ? text : text[..400] + "…";
    }

    /// <summary>点击安装：等待重启时直接重启安装，否则启动更新并轮询下载进度。</summary>
    public async Task StartUpdateAsync()
    {
        if (IsFlatpak)
            return;

        if (!string.IsNullOrEmpty(_releaseUrl))
        {
            Basics.OpenPath(_releaseUrl);
            return;
        }

        if (!UpdatePolicies.Current.SupportsInAppUpdate)
        {
            OpenReleasesPage();
            return;
        }

        if (UpdateManager.isUpdateWaitingRestart)
        {
            UpdateManager.UpdateRestart(true);
            return;
        }

        UpdateButtonEnabled = false;
        UpdateManager.UpdateStart(UpdateEnums.UpdateType.UpdateNow);
        await MonitorAndRestoreAsync();
    }

    private async Task MonitorAndRestoreAsync()
    {
        await MonitorUpdateProgressAsync();
        SetUpdateButtonState(
            UpdateManager.isUpdateWaitingRestart
                ? UpdateButtonState.RestartInstall
                : InstallButtonState);
        UpdateButtonEnabled = !IsFlatpak;
    }

    /// <summary>页面重新进入时同步更新按钮状态，若下载仍在进行则恢复进度显示。</summary>
    public async Task SyncUpdateButtonStateAsync()
    {
        if (UpdateManager.updateLoader is { State: LoadState.Loading })
        {
            UpdateButtonEnabled = false;
            SetUpdateButtonState(InstallButtonState);
            await MonitorAndRestoreAsync();
        }
        else if (UpdateManager.isUpdateWaitingRestart)
        {
            SetUpdateButtonState(UpdateButtonState.RestartInstall);
            UpdateButtonEnabled = !IsFlatpak;
        }
        else
        {
            SetUpdateButtonState(InstallButtonState);
            UpdateButtonEnabled = !IsFlatpak;
        }
    }

    private async Task MonitorUpdateProgressAsync()
    {
        // UpdateStart 在后台线程准备任务，先等待 updateLoader 出现，避免错过进度。
        for (var i = 0; i < 150; i++)
        {
            if (UpdateManager.updateLoader is not null || UpdateManager.isUpdateWaitingRestart)
                break;
            await Task.Delay(200);
        }

        while (UpdateManager.updateLoader is { State: LoadState.Loading } loader)
        {
            var progress = loader.Progress;
            LauncherCommon.RunInUi(() => SetUpdateProgress(progress));
            await Task.Delay(200);
        }
    }

    public async Task ShowChangelogAsync()
    {
        ChangelogDetailEnabled = false;
        try
        {
            var content = _latestVersion?.Changelog;
            if (string.IsNullOrWhiteSpace(content))
            {
                await Dialog.ShowAsync(
                    new TextDialogRequest
                    {
                        Title = Lang.Text("Setup.Update.Changelog.Title"),
                        Content = Lang.Text("Setup.Update.Changelog.Unavailable"),
                        Buttons = DialogButtons.Ok
                    });
                return;
            }

            await Dialog.ShowAsync(
                new MarkdownDialogRequest
                {
                    Title = Lang.Text("Setup.Update.Changelog.Title"),
                    Content = content,
                    Buttons = DialogButtons.Ok
                });
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(
                ex,
                "查看更新日志失败",
                LauncherLogLevel.Feedback,
                userSummary: Lang.Text("Setup.Error.OperationFailed"));
        }
        finally
        {
            ChangelogDetailEnabled = true;
        }
    }

    public static void OpenReleasesPage()
    {
        Basics.OpenPath(GitHubReleasesPage);
    }

    /// <summary>切换更新通道；返回 false 表示用户取消，调用方需要把下拉恢复原值。</summary>
    public async Task<bool> ChangeChannelAsync(int index)
    {
        ChannelEnabled = false;
        try
        {
            switch (index)
            {
                case 1:
                {
                    var result = await Dialog.ShowAsync(
                        new TextDialogRequest
                        {
                            Title = Lang.Text("Setup.Update.Channel.Common.Warning.Title"),
                            Content = Lang.Text("Setup.Update.Channel.Beta.Warning.Message"),
                            Buttons = DialogButtons.From(
                                DialogButton.Create(
                                    Lang.Text("Setup.Update.Channel.Common.Warning.Confirm")),
                                DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
                            IsWarn = true
                        });
                    if (result.Button != 1) return false;
                    break;
                }
                case 2:
                {
                    var warn = await Dialog.ShowAsync(
                        new TextDialogRequest
                        {
                            Title = Lang.Text("Setup.Update.Channel.Common.Warning.Title"),
                            Content = Lang.Text("Setup.Update.Channel.Dev.Warning.Message"),
                            Buttons = DialogButtons.From(
                                DialogButton.Create(
                                    Lang.Text("Setup.Update.Channel.Common.Warning.Confirm")),
                                DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
                            IsWarn = true
                        });
                    if (warn.Button != 1) return false;

                    var confirmText = Lang.Text(
                        "Setup.Update.Channel.Dev.FinalConfirm.ExpectedInput");
                    var input = await Dialog.ShowAsync(
                        new InputDialogRequest
                        {
                            Title = Lang.Text("Setup.Update.Channel.Dev.FinalConfirm.Title"),
                            Content = Lang.Text(
                                "Setup.Update.Channel.Dev.FinalConfirm.Message",
                                confirmText),
                            Buttons = DialogButtons.From(
                                DialogButton.Create(
                                    Lang.Text("Setup.Update.Channel.Dev.FinalConfirm.Submit")),
                                DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
                            IsWarn = true,
                            HighLight = false
                        });
                    if (!input.IsConfirmed || input.InputText != confirmText)
                    {
                        if (input.IsConfirmed)
                            HintService.Show(
                                Hints.Info(
                                    Lang.Text("Setup.Update.Channel.Dev.FinalConfirm.WrongInput")));
                        return false;
                    }

                    break;
                }
            }

            Config.Update.UpdateChannel = (UpdateChannel)index;
            return true;
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(
                ex,
                "切换更新通道失败",
                LauncherLogLevel.Feedback,
                userSummary: Lang.Text("Setup.Error.OperationFailed"));
            return false;
        }
        finally
        {
            ChannelEnabled = true;
        }
    }

    private void SetStatusText(string key, params object?[] args)
    {
        _descKey = key;
        _descArgs = args;
        _hasStatusText = true;
        OnPropertyChanged(nameof(CurrentDescText));
    }

    public void SetExternalUpdateButtonText(string textKey, params object?[] args)
    {
        _externalUpdateButtonKey = textKey;
        _externalUpdateButtonArgs = args;
        OnPropertyChanged(nameof(UpdateButtonText));
    }

    private void SetUpdateButtonState(UpdateButtonState state)
    {
        _externalUpdateButtonKey = null;
        _updateButtonState = state;
        OnPropertyChanged(nameof(UpdateButtonText));
    }

    private void SetUpdateProgress(double progress)
    {
        _externalUpdateButtonKey = null;
        _updateButtonState = UpdateButtonState.Progress;
        _updateProgress = progress;
        OnPropertyChanged(nameof(UpdateButtonText));
    }

    public static string VersionNameFormat(string str)
    {
        str = str.Replace("v", "");
        if (!str.Contains('-'))
            return str;
        var add = str.AfterLast("-");
        str = str.BeforeLast("-");
        return $"{str} {add.Replace(".", " ").Replace("beta", "Beta").Replace("rc", "RC")}";
    }

    private static string ExtractSummary(string? changelog)
    {
        if (string.IsNullOrEmpty(changelog)) return string.Empty;
        const string startTag = "<summary>";
        const string endTag = "</summary>";
        var start = changelog.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;
        start += startTag.Length;
        var end = changelog.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? string.Empty : changelog[start..end].Trim();
    }

    private enum UpdateButtonState
    {
        Install,
        OpenDownload,
        Flatpak,
        RestartInstall,
        Progress
    }
}