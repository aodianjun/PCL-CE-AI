using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PCL.Ai;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.UI.Hints;
using PCL.Navigation;
using PCL.UI.Controls;

namespace PCL.Views.Pages.Setup;

/// <summary>
/// 设置 - AI 接入：首次启动的引导页。说明如何获取 API Key，保存并测试 DeepSeek 兼容接口。
/// </summary>
public partial class PageSetupAi : MyPageRight
{
    private readonly MyTextBox _txtBaseUrl;
    private readonly MyTextBox _txtModel;
    private readonly MyTextBox _txtApiKey;
    private readonly MyCheckBox _chkEnableTools;
    private readonly MyCheckBox _chkAutoCrash;
    private readonly TextBlock _labStatus;
    private readonly MyButton _btnSaveTest;

    public PageSetupAi()
    {
        InitializeComponent();
        PanScroll = this.FindControl<MyScrollViewer>("PanBack");
        _txtBaseUrl = this.FindControl<MyTextBox>("TxtBaseUrl")!;
        _txtModel = this.FindControl<MyTextBox>("TxtModel")!;
        _txtApiKey = this.FindControl<MyTextBox>("TxtApiKey")!;
        _chkEnableTools = this.FindControl<MyCheckBox>("ChkEnableTools")!;
        _chkAutoCrash = this.FindControl<MyCheckBox>("ChkAutoCrash")!;
        _labStatus = this.FindControl<TextBlock>("LabStatus")!;
        _btnSaveTest = this.FindControl<MyButton>("BtnSaveTest")!;

        this.FindControl<MyButton>("BtnSaveTest")!.Click += SaveAndTest_OnClick;
        this.FindControl<MyButton>("BtnSave")!.Click += Save_OnClick;
        this.FindControl<MyButton>("BtnGetKey")!.Click += GetKey_OnClick;
        this.FindControl<MyButton>("BtnSkip")!.Click += Skip_OnClick;

        Loaded += (_, _) => LoadConfig();
    }

    private void LoadConfig()
    {
        _txtBaseUrl.Text = string.IsNullOrWhiteSpace(Config.Ai.BaseUrl)
            ? "https://api.deepseek.com"
            : Config.Ai.BaseUrl;
        _txtModel.Text = string.IsNullOrWhiteSpace(Config.Ai.Model)
            ? "deepseek-chat"
            : Config.Ai.Model;
        _txtApiKey.Text = Config.Ai.ApiKey;
        _chkEnableTools.Checked = Config.Ai.EnableTools;
        _chkAutoCrash.Checked = Config.Ai.AutoAnalyzeCrash;

        if (AiChatService.IsConfigured)
            SetStatus(Lang.Text("Setup.Ai.Status.Configured"), false);
        else
            SetStatus(Lang.Text("Setup.Ai.Status.NotConfigured"), false);
    }

    private bool SaveConfig()
    {
        var baseUrl = _txtBaseUrl.Text?.Trim() ?? "";
        var model = _txtModel.Text?.Trim() ?? "";
        var key = _txtApiKey.Text?.Trim() ?? "";

        if (baseUrl.Length == 0)
        {
            SetStatus(Lang.Text("Setup.Ai.Error.BaseUrl"), true);
            return false;
        }

        if (model.Length == 0)
        {
            SetStatus(Lang.Text("Setup.Ai.Error.Model"), true);
            return false;
        }

        try
        {
            Config.Ai.BaseUrl = baseUrl;
            Config.Ai.Model = model;
            Config.Ai.ApiKey = key;
            Config.Ai.EnableTools = _chkEnableTools.Checked == true;
            Config.Ai.AutoAnalyzeCrash = _chkAutoCrash.Checked == true;
            Config.Ai.OnboardingDone = true;
            return true;
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Ai", "保存 AI 配置失败");
            SetStatus(Lang.Text("Setup.Ai.Error.Save") + "：" + ex.Message, true);
            return false;
        }
    }

    private void Save_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        if (!SaveConfig())
            return;

        SetStatus(Lang.Text("Setup.Ai.Status.Saved"), false);
        HintService.Show(Hints.Success(Lang.Text("Setup.Ai.Status.Saved")));
    }

    private async void SaveAndTest_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        if (!SaveConfig())
            return;
        if (string.IsNullOrWhiteSpace(Config.Ai.ApiKey))
        {
            SetStatus(Lang.Text("Setup.Ai.Error.ApiKey"), true);
            return;
        }

        _btnSaveTest.IsEnabled = false;
        SetStatus(Lang.Text("Setup.Ai.Status.Testing"), false);
        try
        {
            var (ok, message) = await AiChatService.TestConnectionAsync(
                Config.Ai.BaseUrl,
                Config.Ai.ApiKey,
                Config.Ai.Model);
            SetStatus(message, !ok);
            if (ok)
                HintService.Show(Hints.Success(Lang.Text("Setup.Ai.Status.Connected")));
        }
        catch (Exception ex)
        {
            SetStatus(Lang.Text("Setup.Ai.Error.Test") + "：" + ex.Message, true);
        }
        finally
        {
            _btnSaveTest.IsEnabled = true;
        }
    }

    private void GetKey_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://platform.deepseek.com/api_keys")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus(Lang.Text("Setup.Ai.Error.OpenBrowser") + "：" + ex.Message, true);
        }
    }

    private void Skip_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        Config.Ai.OnboardingDone = true;
        PageRouter.Navigate(PageIds.Launch);
    }

    private void SetStatus(string text, bool error)
    {
        _labStatus.Text = text;
        _labStatus.Foreground = error
            ? new SolidColorBrush(Color.Parse("#FF4C4C"))
            : null;
    }
}
