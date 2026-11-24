using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PCL.Ai;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.UI;
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
    private readonly MyCheckBox _chkEnableWeb;
    private readonly MyCheckBox _chkAutoCrash;
    private readonly TextBlock _labStatus;
    private readonly MyButton _btnSaveTest;
    private readonly MyButton _btnFetchModels;
    private readonly MyComboBox _comboModels;
    private readonly MyTextBox _txtWorkDirectory;
    private readonly MyTextBox _txtAllowedDirectories;
    private readonly MyTextBox _txtContextLimit;
    private readonly MyTextBox _txtMaxOutput;
    private readonly MyTextBox _txtMaxToolRounds;
    private readonly Dictionary<string, (int? Context, int? Output)> _modelMeta =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingModels;

    public PageSetupAi()
    {
        InitializeComponent();
        PanScroll = this.FindControl<MyScrollViewer>("PanBack");
        _txtBaseUrl = this.FindControl<MyTextBox>("TxtBaseUrl")!;
        _txtModel = this.FindControl<MyTextBox>("TxtModel")!;
        _txtApiKey = this.FindControl<MyTextBox>("TxtApiKey")!;
        _chkEnableTools = this.FindControl<MyCheckBox>("ChkEnableTools")!;
        _chkEnableWeb = this.FindControl<MyCheckBox>("ChkEnableWeb")!;
        _chkAutoCrash = this.FindControl<MyCheckBox>("ChkAutoCrash")!;
        _labStatus = this.FindControl<TextBlock>("LabStatus")!;
        _btnSaveTest = this.FindControl<MyButton>("BtnSaveTest")!;
        _btnFetchModels = this.FindControl<MyButton>("BtnFetchModels")!;
        _comboModels = this.FindControl<MyComboBox>("ComboModels")!;
        _txtWorkDirectory = this.FindControl<MyTextBox>("TxtWorkDirectory")!;
        _txtAllowedDirectories = this.FindControl<MyTextBox>("TxtAllowedDirectories")!;
        _txtContextLimit = this.FindControl<MyTextBox>("TxtContextLimit")!;
        _txtMaxOutput = this.FindControl<MyTextBox>("TxtMaxOutput")!;
        _txtMaxToolRounds = this.FindControl<MyTextBox>("TxtMaxToolRounds")!;

        _btnFetchModels.Click += FetchModels_OnClick;
        this.FindControl<MyButton>("BtnPickWorkDirectory")!.Click += PickWorkDirectory_OnClick;
        this.FindControl<MyButton>("BtnAddAllowedDirectory")!.Click += AddAllowedDirectory_OnClick;
        _comboModels.SelectionChanged += Models_OnSelectionChanged;

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
        _chkEnableWeb.Checked = Config.Ai.EnableWeb;
        _chkAutoCrash.Checked = Config.Ai.AutoAnalyzeCrash;
        _txtWorkDirectory.Text = Config.Ai.WorkDirectory;
        _txtAllowedDirectories.Text = Config.Ai.AllowedDirectories;
        _txtContextLimit.Text = Config.Ai.ContextLimit.ToString();
        _txtMaxOutput.Text = Config.Ai.MaxOutputTokens.ToString();
        _txtMaxToolRounds.Text = Config.Ai.MaxToolRounds.ToString();

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
            Config.Ai.EnableWeb = _chkEnableWeb.Checked == true;
            Config.Ai.AutoAnalyzeCrash = _chkAutoCrash.Checked == true;
            Config.Ai.WorkDirectory = _txtWorkDirectory.Text?.Trim() ?? "";
            Config.Ai.AllowedDirectories = _txtAllowedDirectories.Text?.Trim() ?? "";
            Config.Ai.ContextLimit = int.TryParse(_txtContextLimit.Text?.Trim(), out var contextLimit)
                ? Math.Max(4000, contextLimit)
                : 64000;
            Config.Ai.MaxOutputTokens = int.TryParse(_txtMaxOutput.Text?.Trim(), out var maxOutput)
                ? Math.Max(256, maxOutput)
                : 4096;
            Config.Ai.MaxToolRounds = int.TryParse(_txtMaxToolRounds.Text?.Trim(), out var maxToolRounds)
                ? Math.Clamp(maxToolRounds, 8, 500)
                : 100;
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

    private async void PickWorkDirectory_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        var folder = await SystemDialogs.SelectFolderAsync(
            Lang.Text("Setup.Ai.WorkDirectory"));
        if (!string.IsNullOrWhiteSpace(folder))
            _txtWorkDirectory.Text = folder;
    }

    private async void AddAllowedDirectory_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        var folder = await SystemDialogs.SelectFolderAsync(
            Lang.Text("Setup.Ai.AllowedDirectories"));
        if (string.IsNullOrWhiteSpace(folder))
            return;
        var current = _txtAllowedDirectories.Text?.Trim() ?? "";
        if (current.Length == 0)
            _txtAllowedDirectories.Text = folder;
        else if (!current.Split(';').Any(item =>
                     string.Equals(item.Trim(), folder, StringComparison.OrdinalIgnoreCase)))
            _txtAllowedDirectories.Text = current + ";" + folder;
    }

    private async void FetchModels_OnClick(object? sender, PointerReleasedEventArgs? e)
    {
        var baseUrl = _txtBaseUrl.Text?.Trim() ?? "";
        var key = _txtApiKey.Text?.Trim() ?? "";
        if (baseUrl.Length == 0)
        {
            SetStatus(Lang.Text("Setup.Ai.Error.BaseUrl"), true);
            return;
        }

        if (key.Length == 0)
        {
            SetStatus(Lang.Text("Setup.Ai.Error.ApiKey"), true);
            return;
        }

        _btnFetchModels.IsEnabled = false;
        SetStatus(Lang.Text("Setup.Ai.Models.Fetching"), false);
        try
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-CE-AI");
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var response = await http.GetAsync(AiChatService.ResolveModelsEndpoint(baseUrl));
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                SetStatus(
                    Lang.Text("Setup.Ai.Models.Failed", $"{(int)response.StatusCode} {body}"),
                    true);
                return;
            }

            var json = JsonNode.Parse(body);
            var array = json?["data"] as JsonArray ?? json?["models"] as JsonArray;
            if (array is null || array.Count == 0)
            {
                SetStatus(Lang.Text("Setup.Ai.Models.Failed", "empty response"), true);
                return;
            }

            var items = new List<MyComboBoxItem>();
            _modelMeta.Clear();
            foreach (var node in array)
            {
                if (node is not JsonObject model)
                    continue;
                var id = model["id"]?.ToString() ?? model["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var context = ParseInt(model["context_length"])
                              ?? ParseInt(model["context_window"])
                              ?? ParseInt(model["max_context_length"])
                              ?? ParseInt(model["top_provider"]?["context_length"]);
                var output = ParseInt(model["max_completion_tokens"])
                             ?? ParseInt(model["max_output_tokens"])
                             ?? ParseInt(model["top_provider"]?["max_completion_tokens"]);
                _modelMeta[id] = (context, output);
                items.Add(new MyComboBoxItem { Content = id });
            }

            if (items.Count == 0)
            {
                SetStatus(Lang.Text("Setup.Ai.Models.Failed", "empty model list"), true);
                return;
            }

            _loadingModels = true;
            try
            {
                _comboModels.ItemsSource = items;
                _comboModels.IsVisible = true;
                var current = _txtModel.Text?.Trim() ?? "";
                var match = items.FirstOrDefault(item =>
                    string.Equals(item.Content?.ToString(), current, StringComparison.OrdinalIgnoreCase));
                _comboModels.SelectedItem = match ?? items[0];
            }
            finally
            {
                _loadingModels = false;
            }

            var selectedId = _comboModels.SelectedItem is MyComboBoxItem selected
                ? selected.Content?.ToString() ?? ""
                : "";
            if (selectedId.Length > 0)
            {
                _txtModel.Text = selectedId;
                ApplyModelParameters(selectedId);
            }

            SetStatus(Lang.Text("Setup.Ai.Models.Fetched", items.Count), false);
        }
        catch (Exception ex)
        {
            SetStatus(Lang.Text("Setup.Ai.Models.Failed", ex.Message), true);
        }
        finally
        {
            _btnFetchModels.IsEnabled = true;
        }
    }

    private void Models_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingModels)
            return;
        if (_comboModels.SelectedItem is not MyComboBoxItem item)
            return;
        var id = item.Content?.ToString() ?? "";
        if (id.Length == 0)
            return;
        _txtModel.Text = id;
        ApplyModelParameters(id);
    }

    private void ApplyModelParameters(string id)
    {
        _modelMeta.TryGetValue(id, out var meta);
        var context = meta.Context ?? KnownContextLimit(id);
        var output = meta.Output ?? KnownMaxOutput(id);
        _txtContextLimit.Text = context.ToString();
        _txtMaxOutput.Text = output.ToString();
        SetStatus(Lang.Text("Setup.Ai.Params.AutoFilled"), false);
    }

    private static int? ParseInt(JsonNode? node)
    {
        if (node is null)
            return null;
        return int.TryParse(node.ToString(), out var value) ? value : null;
    }

    private static int KnownContextLimit(string id)
    {
        var value = id.ToLowerInvariant();
        if (value.Contains("deepseek")) return 128000;
        if (value.Contains("claude")) return 200000;
        if (value.Contains("gemini")) return 1000000;
        if (value.Contains("gpt-4.1")) return 1000000;
        if (value.Contains("gpt-4")) return 128000;
        if (value.Contains("qwen")) return 131072;
        if (value.Contains("glm")) return 128000;
        return 64000;
    }

    private static int KnownMaxOutput(string id)
    {
        var value = id.ToLowerInvariant();
        if (value.Contains("deepseek")) return 8192;
        if (value.Contains("claude")) return 8192;
        if (value.Contains("gemini")) return 8192;
        if (value.Contains("gpt-4")) return 16384;
        if (value.Contains("qwen") || value.Contains("glm")) return 8192;
        return 4096;
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
