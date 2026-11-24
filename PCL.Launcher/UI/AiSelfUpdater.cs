using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.UI.Dialogs;
using PCL.Core.UI.Hints;

namespace PCL.UI;

/// <summary>
/// PCL CE-AI 自更新：从 aodianjun/PCL-CE-AI 的 GitHub Release 读取最新版本，
/// 用户确认一次后下载对应安装包，交给一个临时 PowerShell 脚本等待本进程退出、
/// 覆盖文件并重新启动。
/// </summary>
public static class AiSelfUpdater
{
    private const string Repository = "aodianjun/PCL-CE-AI";
    private const string ReleaseApi =
        "https://api.github.com/repos/" + Repository + "/releases/latest";

    private static readonly HttpClient DirectHttp = CreateHttp(false);
    private static readonly HttpClient ProxyHttp = CreateHttp(true);
    private static int _started;

    private static HttpClient CreateHttp(bool useProxy)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = useProxy,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-CE-AI");
        return client;
    }

    /// <summary>启动后延迟检查一次，不阻塞界面。</summary>
    public static void ScheduleStartupCheck()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                if (Config.Update.UpdateMode == LauncherAutoUpdateBehavior.Disable)
                    return;
                await CheckAndUpdateAsync(false);
            }
            catch (Exception ex)
            {
                LogWrapper.Warn("Update", "启动时检查更新失败：" + ex.Message);
            }
        });
    }

    /// <summary>设置页“安装”按钮：立即检查并走同一条确认/下载/重启流程。</summary>
    public static Task RunManualUpdateAsync()
    {
        return CheckAndUpdateAsync(true);
    }

    private static async Task CheckAndUpdateAsync(bool manual)
    {
        JsonObject? release;
        try
        {
            release = await GetLatestReleaseAsync();
        }
        catch (Exception ex)
        {
            LogWrapper.Warn("Update", "获取最新 Release 失败：" + ex.Message);
            if (manual)
                HintService.Show(Hints.Error(Lang.Text("Update.Ai.Failed", ex.Message)));
            return;
        }

        if (release is null)
        {
            if (manual)
                HintService.Show(Hints.Info(Lang.Text("Setup.Update.NoRelease")));
            return;
        }

        var tag = release["tag_name"]?.ToString() ?? "";
        var body = release["body"]?.ToString() ?? "";
        if (!IsNewerVersion(tag, LauncherCommon.VersionBaseName))
        {
            if (manual)
                HintService.Show(Hints.Info(Lang.Text("Setup.Update.Latest")));
            return;
        }

        var singleFile = string.IsNullOrEmpty(typeof(AiSelfUpdater).Assembly.Location);
        var asset = FindAsset(release["assets"] as JsonArray, singleFile);
        if (asset is null)
        {
            if (manual)
                HintService.Show(Hints.Warning(Lang.Text("Update.Ai.NoAsset")));
            return;
        }

        var versionName = tag.TrimStart('v', 'V');
        var assetName = asset["name"]?.ToString() ?? "";
        var downloadUrl = asset["browser_download_url"]?.ToString();
        var expectedSize = asset["size"]?.GetValue<long>() ?? 0;
        if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(assetName))
            return;

        var confirmed = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var result = await DialogService.Instance.ShowAsync(new TextDialogRequest
            {
                Title = Lang.Text("Update.Ai.Available.Title", versionName),
                Content = Lang.Text(
                              "Update.Ai.Available.Content",
                              LauncherCommon.VersionBaseName,
                              versionName)
                          + (string.IsNullOrWhiteSpace(body)
                              ? ""
                              : Environment.NewLine + Environment.NewLine + Truncate(body, 800)),
                Buttons = DialogButtons.OkCancel
            });
            return result.Button == 1;
        });

        if (!confirmed)
            return;

        HintService.Show(Hints.Info(Lang.Text("Update.Ai.Downloading", versionName)));

        var updateDir = Path.Combine(Path.GetTempPath(), "PCL-CE-AI-Update");
        Directory.CreateDirectory(updateDir);
        var downloadPath = Path.Combine(updateDir, assetName);
        try
        {
            await DownloadFileAsync(downloadUrl, downloadPath);
            var actualSize = new FileInfo(downloadPath).Length;
            if (expectedSize > 0 && actualSize != expectedSize)
                throw new InvalidOperationException(
                    $"下载文件大小不匹配（{actualSize} / {expectedSize} 字节）");
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Update", "下载更新包失败");
            HintService.Show(Hints.Error(Lang.Text("Update.Ai.Failed", ex.Message)));
            return;
        }

        var target = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(target))
        {
            HintService.Show(Hints.Error(Lang.Text("Update.Ai.Failed", "无法定位当前启动器路径")));
            return;
        }

        try
        {
            StartUpdaterScript(target, downloadPath, singleFile);
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Update", "启动更新脚本失败");
            HintService.Show(Hints.Error(Lang.Text("Update.Ai.Failed", ex.Message)));
            return;
        }

        LogWrapper.Info("Update", $"更新 {versionName} 已下载，等待退出后覆盖：{target}");
        Dispatcher.UIThread.Post(UiInteraction.Shutdown);
    }

    private static async Task<JsonObject?> GetLatestReleaseAsync()
    {
        using var response = await GetWithFallbackAsync(ReleaseApi);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return json as JsonObject;
    }

    private static async Task<HttpResponseMessage> GetWithFallbackAsync(string url)
    {
        try
        {
            return await DirectHttp.GetAsync(url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogWrapper.Warn("Update", $"直连失败，改用系统代理重试：{ex.Message}");
            return await ProxyHttp.GetAsync(url);
        }
    }

    private static async Task DownloadFileAsync(string url, string path)
    {
        using var response = await GetWithFallbackAsync(url);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(path);
        await source.CopyToAsync(target);
    }

    private static JsonObject? FindAsset(JsonArray? assets, bool singleFile)
    {
        if (assets is null)
            return null;

        return assets
            .OfType<JsonObject>()
            .FirstOrDefault(asset =>
            {
                var name = asset["name"]?.ToString() ?? "";
                if (name.EndsWith(".sigstore.json", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (singleFile)
                    return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                           && name.Contains("win-x64", StringComparison.OrdinalIgnoreCase);
                return name.EndsWith("-fdd.zip", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static void StartUpdaterScript(string targetExe, string downloadedFile, bool singleFile)
    {
        var temp = Path.GetDirectoryName(downloadedFile) ?? Path.GetTempPath();
        var scriptPath = Path.Combine(temp, "apply-update.ps1");
        var targetDir = Path.GetDirectoryName(targetExe) ?? AppContext.BaseDirectory;
        var pid = Environment.ProcessId;

        string script;
        if (singleFile)
        {
            script = $@"
$ErrorActionPreference = 'Stop'
$pidToWait = {pid}
$target = {PsQuote(targetExe)}
$new = {PsQuote(downloadedFile)}
while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) {{
    Start-Sleep -Milliseconds 400
}}
$staging = $target + '.new'
Copy-Item -LiteralPath $new -Destination $staging -Force
Move-Item -LiteralPath $staging -Destination $target -Force
Start-Process -FilePath $target -WorkingDirectory {PsQuote(targetDir)}
Start-Sleep -Seconds 2
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
";
        }
        else
        {
            script = $@"
$ErrorActionPreference = 'Stop'
$pidToWait = {pid}
$target = {PsQuote(targetExe)}
$targetDir = {PsQuote(targetDir)}
$zip = {PsQuote(downloadedFile)}
$extract = Join-Path $env:TEMP ('PCL-CE-AI-Update-' + [Guid]::NewGuid().ToString('N'))
while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) {{
    Start-Sleep -Milliseconds 400
}}
Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
Copy-Item -Path (Join-Path $extract '*') -Destination $targetDir -Recurse -Force
Start-Process -FilePath $target -WorkingDirectory $targetDir
Start-Sleep -Seconds 2
Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
";
        }

        File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File {PsQuote(scriptPath)}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(startInfo);
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

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

    private static string Truncate(string value, int max)
    {
        value = value.Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }
}
