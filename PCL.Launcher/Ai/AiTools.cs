using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.Minecraft.Profile;
using PCL.Navigation;
using PCL.Views.Pages.Download;

namespace PCL.Ai;

/// <summary>
/// AI 可调用的本地工具集合。工具只操作启动器自身的能力：读取状态、切换版本、启动游戏、
/// 管理 Mod、打开页面、读取日志与崩溃报告。
/// </summary>
public static class AiTools
{
    private const int MaxModsInList = 300;
    private const int DefaultLogLines = 200;

    /// <summary>给模型声明的工具清单（OpenAI tools 格式）。</summary>
    public static JsonArray BuildDefinitions()
    {
        JsonArray Array(params JsonObject[] items) => new(items);

        JsonObject Tool(string name, string description, JsonObject properties, JsonArray? required = null)
        {
            var parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (required is { Count: > 0 })
                parameters["required"] = required;
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = parameters
                }
            };
        }

        JsonObject Str(string description) => new()
        {
            ["type"] = "string",
            ["description"] = description
        };

        JsonObject Bool(string description) => new()
        {
            ["type"] = "boolean",
            ["description"] = description
        };

        return Array(
            Tool(
                "get_launcher_status",
                "读取启动器当前状态：当前版本、账号、Mod 数量、游戏目录等。",
                new JsonObject()),
            Tool(
                "list_instances",
                "列出本地已安装的所有 Minecraft 版本，并标出当前选中的版本。",
                new JsonObject()),
            Tool(
                "select_instance",
                "切换当前使用的 Minecraft 版本。",
                new JsonObject
                {
                    ["name"] = Str("要切换到的版本名称，必须与 list_instances 返回的名称完全一致")
                },
                Array(Str("name"))),
            Tool(
                "launch_game",
                "使用当前选中的版本和账号启动 Minecraft。",
                new JsonObject()),
            Tool(
                "list_mods",
                "列出当前版本 mods 文件夹中的 Mod，包含启用状态与文件大小。",
                new JsonObject()),
            Tool(
                "set_mod_enabled",
                "启用或禁用当前版本的某个 Mod（通过重命名 .jar / .jar.disabled 实现）。",
                new JsonObject
                {
                    ["file"] = Str("Mod 文件名，例如 sodium-1.21.1.jar"),
                    ["enabled"] = Bool("true 表示启用，false 表示禁用")
                },
                Array(Str("file"), Str("enabled"))),
            Tool(
                "delete_mod",
                "永久删除当前版本 mods 文件夹中的某个 Mod。必须先得到用户明确确认。",
                new JsonObject
                {
                    ["file"] = Str("要删除的 Mod 文件名")
                },
                Array(Str("file"))),
            Tool(
                "open_page",
                "在启动器中打开指定页面。可选值：launch、download、mod、modpack、resourcepack、shader、world、datapack、favorites、tools、settings、instance_settings、instance_select、logs、tasks。",
                new JsonObject
                {
                    ["page"] = Str("页面标识")
                },
                Array(Str("page"))),
            Tool(
                "search_resource",
                "打开资源下载页并按游戏版本/加载器筛选。",
                new JsonObject
                {
                    ["type"] = Str("资源类型：mod、modpack、resourcepack、shader、world、datapack"),
                    ["game_version"] = Str("可选，目标 Minecraft 版本，例如 1.21.1"),
                    ["loader"] = Str("可选，加载器：forge、neoforge、fabric、quilt、liteloader、cleanroom")
                },
                Array(Str("type"))),
            Tool(
                "get_game_log",
                "读取当前版本最近一次游戏的日志（latest.log 末尾若干行）。",
                new JsonObject
                {
                    ["lines"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "读取的行数，默认 200"
                    }
                }),
            Tool(
                "get_last_crash",
                "读取最近一次崩溃报告，用于分析游戏崩溃原因。",
                new JsonObject()),
            Tool(
                "install_version_note",
                "当用户要下载安装新版本时，返回当前启动器安装流程说明并打开安装页。",
                new JsonObject
                {
                    ["version"] = Str("可选，目标版本号")
                })
        );
    }

    /// <summary>执行一次工具调用，返回给模型的文本结果。</summary>
    public static async Task<string> ExecuteAsync(AiToolCall call, CancellationToken ct)
    {
        try
        {
            var args = ParseArguments(call.Arguments);
            return call.Name switch
            {
                "get_launcher_status" => GetLauncherStatus(),
                "list_instances" => ListInstances(),
                "select_instance" => SelectInstance(GetString(args, "name")),
                "launch_game" => await LaunchGameAsync(),
                "list_mods" => ListMods(),
                "set_mod_enabled" => SetModEnabled(
                    GetString(args, "file"),
                    GetBool(args, "enabled")),
                "delete_mod" => DeleteMod(GetString(args, "file")),
                "open_page" => OpenPage(GetString(args, "page")),
                "search_resource" => SearchResource(
                    GetString(args, "type"),
                    GetString(args, "game_version"),
                    GetString(args, "loader")),
                "get_game_log" => GetGameLog(GetInt(args, "lines", DefaultLogLines)),
                "get_last_crash" => GetLastCrash(),
                "install_version_note" => OpenInstallPage(GetString(args, "version")),
                _ => $"未知工具：{call.Name}"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn("Ai", $"工具 {call.Name} 执行失败：{ex.Message}");
            return "工具执行失败：" + ex.Message;
        }
    }

    /// <summary>拼接给系统提示词使用的环境摘要。</summary>
    public static string GetEnvironmentSummary()
    {
        var builder = new StringBuilder();
        try
        {
            var instance = InstanceList.McMcInstanceSelected;
            builder.AppendLine($"- 启动器：PCL CE-AI {typeof(AiTools).Assembly.GetName().Version}");
            builder.AppendLine($"- 游戏目录：{ResolveMinecraftFolder()}");
            builder.AppendLine($"- 当前版本：{instance?.Name ?? "未选择"}");
            if (instance is not null)
            {
                builder.AppendLine($"- 版本路径：{instance.PathInstance}");
                builder.AppendLine($"- Mod 数量：{CountMods(instance.PathInstance)}");
            }

            var profile = ProfileService.Current;
            builder.AppendLine(profile is null
                ? "- 账号：未登录"
                : $"- 账号：{profile.LoginName ?? "未知"}（{profile.ProfileType}）");
        }
        catch (Exception ex)
        {
            builder.AppendLine("- 环境读取失败：" + ex.Message);
        }

        return builder.ToString();
    }

    private static JsonObject ParseArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(arguments) as JsonObject ?? new JsonObject();
        }
        catch (Exception)
        {
            return new JsonObject();
        }
    }

    private static string? GetString(JsonObject args, string key)
    {
        return args[key]?.ToString();
    }

    private static bool GetBool(JsonObject args, string key)
    {
        var node = args[key];
        if (node is null)
            return false;
        if (node.GetValueKind() == System.Text.Json.JsonValueKind.True)
            return true;
        if (node.GetValueKind() == System.Text.Json.JsonValueKind.False)
            return false;
        return bool.TryParse(node.ToString(), out var value) && value;
    }

    private static int GetInt(JsonObject args, string key, int fallback)
    {
        var node = args[key];
        if (node is null)
            return fallback;
        return int.TryParse(node.ToString(), out var value) ? value : fallback;
    }

    private static string GetLauncherStatus()
    {
        var instance = InstanceList.McMcInstanceSelected;
        var profile = ProfileService.Current;
        var result = new JsonObject
        {
            ["minecraft_folder"] = ResolveMinecraftFolder(),
            ["instance"] = instance?.Name,
            ["instance_path"] = instance?.PathInstance,
            ["mods_enabled"] = instance is null ? 0 : CountMods(instance.PathInstance, true),
            ["mods_disabled"] = instance is null ? 0 : CountMods(instance.PathInstance, false),
            ["account"] = profile?.LoginName,
            ["account_type"] = profile?.ProfileType.ToString(),
            ["is_launching"] = GameLaunch.mcLaunchLoader.State == LoadState.Loading
        };
        return result.ToJsonString();
    }

    private static string ListInstances()
    {
        var selected = InstanceList.McMcInstanceSelected?.Name;
        var list = new JsonArray();
        foreach (var instance in EnumerateInstances().Take(200))
            list.Add(new JsonObject
            {
                ["name"] = instance.Name,
                ["selected"] = string.Equals(instance.Name, selected, StringComparison.OrdinalIgnoreCase),
                ["path"] = instance.PathInstance
            });

        return new JsonObject
        {
            ["count"] = list.Count,
            ["selected"] = selected,
            ["instances"] = list
        }.ToJsonString();
    }

    private static IEnumerable<McInstance> EnumerateInstances()
    {
        foreach (var group in InstanceList.mcInstanceList.Values)
        foreach (var instance in group)
            yield return instance;
    }

    private static string SelectInstance(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "缺少参数 name。";

        var instance = EnumerateInstances().FirstOrDefault(item =>
            string.Equals(item.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (instance is null)
            return $"没有找到名为“{name}”的版本。可先调用 list_instances 查看可用版本。";

        var apply = () =>
        {
            InstanceList.McMcInstanceSelected = instance;
            States.Game.SelectedInstance = instance.Name;
            LaunchUi.RefreshLaunchButtons();
            LaunchUi.RefreshLaunchPage(false);
        };

        if (Dispatcher.UIThread.CheckAccess())
            apply();
        else
            Dispatcher.UIThread.Post(apply);

        return $"已切换到版本“{instance.Name}”。";
    }

    private static async Task<string> LaunchGameAsync()
    {
        var instance = InstanceList.McMcInstanceSelected;
        if (instance is null)
            return "当前没有选择任何版本。请先告诉我要启动哪个版本，或让我打开版本选择页。";
        if (ProfileService.Current is null)
            return "当前没有登录账号。请先在“账号”里登录（点击启动页底部的“账号”按钮），再让我启动游戏。";
        if (File.Exists(instance.PathInstance + ".pclignore"))
            return "这个版本正在安装中，暂时不能启动。";

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            PageRouter.Navigate(PageIds.Launch);
            GameLaunch.McLaunchStart();
        });

        return $"已开始启动 Minecraft {instance.Name}。";
    }

    private static string ModsFolder()
    {
        var instance = InstanceList.McMcInstanceSelected;
        return instance is null
            ? ""
            : Path.Combine(instance.PathInstance, "mods");
    }

    private static int CountMods(string instancePath, bool? enabled = null)
    {
        var folder = Path.Combine(instancePath, "mods");
        if (!Directory.Exists(folder))
            return 0;
        try
        {
            return enabled switch
            {
                true => Directory.GetFiles(folder, "*.jar").Length,
                false => Directory.GetFiles(folder, "*.disabled").Length,
                _ => Directory.GetFiles(folder, "*.jar").Length
                     + Directory.GetFiles(folder, "*.disabled").Length
            };
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string ListMods()
    {
        var folder = ModsFolder();
        if (folder.Length == 0)
            return "当前没有选择版本。";
        if (!Directory.Exists(folder))
            return "当前版本的 mods 文件夹不存在（可能还没有安装过 Mod）。";

        var list = new JsonArray();
        try
        {
            foreach (var file in Directory.GetFiles(folder)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                         .Take(MaxModsInList))
            {
                var name = Path.GetFileName(file);
                var enabled = name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
                if (!enabled && !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(new JsonObject
                {
                    ["file"] = name,
                    ["enabled"] = enabled,
                    ["size_kb"] = Math.Round(new FileInfo(file).Length / 1024d, 1)
                });
            }
        }
        catch (Exception ex)
        {
            return "读取 Mod 列表失败：" + ex.Message;
        }

        return new JsonObject
        {
            ["folder"] = folder,
            ["count"] = list.Count,
            ["mods"] = list
        }.ToJsonString();
    }

    private static string SetModEnabled(string? file, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(file))
            return "缺少参数 file。";

        var folder = ModsFolder();
        if (folder.Length == 0 || !Directory.Exists(folder))
            return "当前没有可用的 mods 文件夹。";

        var safeName = Path.GetFileName(file.Trim());
        var enabledPath = Path.Combine(folder, safeName);
        var disabledPath = enabledPath + ".disabled";

        try
        {
            if (enabled)
            {
                if (File.Exists(disabledPath))
                {
                    File.Move(disabledPath, enabledPath, true);
                    return $"已启用 Mod：{safeName}";
                }

                return File.Exists(enabledPath)
                    ? $"这个 Mod 已经是启用状态：{safeName}"
                    : $"没有找到 Mod：{safeName}";
            }

            if (File.Exists(enabledPath))
            {
                File.Move(enabledPath, disabledPath, true);
                return $"已禁用 Mod：{safeName}";
            }

            return File.Exists(disabledPath)
                ? $"这个 Mod 已经是禁用状态：{safeName}"
                : $"没有找到 Mod：{safeName}";
        }
        catch (Exception ex)
        {
            return "修改 Mod 状态失败：" + ex.Message;
        }
    }

    private static string DeleteMod(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return "缺少参数 file。";

        var folder = ModsFolder();
        if (folder.Length == 0 || !Directory.Exists(folder))
            return "当前没有可用的 mods 文件夹。";

        var safeName = Path.GetFileName(file.Trim());
        var targets = new[]
        {
            Path.Combine(folder, safeName),
            Path.Combine(folder, safeName) + ".disabled"
        };

        try
        {
            var deleted = false;
            foreach (var target in targets)
                if (File.Exists(target))
                {
                    File.Delete(target);
                    deleted = true;
                }

            return deleted ? $"已删除 Mod：{safeName}" : $"没有找到 Mod：{safeName}";
        }
        catch (Exception ex)
        {
            return "删除 Mod 失败：" + ex.Message;
        }
    }

    private static string OpenPage(string? page)
    {
        var key = (page ?? "").Trim().ToLowerInvariant();
        switch (key)
        {
            case "launch":
                PageRouter.Navigate(PageIds.Launch);
                return "已回到启动页。";
            case "download":
            case "install":
                PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadInstall);
                return "已打开 Minecraft 安装页。";
            case "mod":
                PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadMod);
                return "已打开 Mod 下载页。";
            case "modpack":
                PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadPack);
                return "已打开整合包下载页。";
            case "resourcepack":
                PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadResourcePack);
                return "已打开资源包下载页。";
            case "shader":
                PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadShader);
                return "已打开光影下载页。";
            case "world":
                PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadWorld);
                return "已打开世界下载页。";
            case "datapack":
                PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadDataPack);
                return "已打开数据包下载页。";
            case "favorites":
                PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadCompFavorites);
                return "已打开收藏页。";
            case "tools":
                PageRouter.Navigate(PageIds.Tools);
                return "已打开工具页。";
            case "settings":
            case "setup":
                PageRouter.Navigate(PageIds.Setup);
                return "已打开设置页。";
            case "instance_settings":
            {
                var instance = InstanceList.McMcInstanceSelected;
                if (instance is null)
                    return "当前没有选择版本，无法打开实例设置。";
                PageRouter.Navigate(PageIds.InstanceSetup, null, instance);
                return $"已打开版本“{instance.Name}”的设置页。";
            }
            case "instance_select":
                PageRouter.Navigate(PageIds.InstanceSelect);
                return "已打开版本选择页。";
            case "logs":
            case "game_log":
                PageRouter.Navigate(PageIds.GameLog);
                return "已打开游戏日志页。";
            case "tasks":
                PageRouter.Navigate(PageIds.TaskManager);
                return "已打开任务管理页。";
            default:
                return $"未知页面“{page}”。可用值：launch、download、mod、modpack、resourcepack、shader、world、datapack、favorites、tools、settings、instance_settings、instance_select、logs、tasks。";
        }
    }

    private static string SearchResource(string? type, string? gameVersion, string? loader)
    {
        var key = (type ?? "mod").Trim().ToLowerInvariant();
        var subPage = key switch
        {
            "mod" or "mods" => SubPageIds.DownloadMod,
            "modpack" or "modpacks" => SubPageIds.DownloadPack,
            "resourcepack" or "resourcepacks" => SubPageIds.DownloadResourcePack,
            "shader" or "shaders" => SubPageIds.DownloadShader,
            "world" or "worlds" => SubPageIds.DownloadWorld,
            "datapack" or "datapacks" => SubPageIds.DownloadDataPack,
            _ => SubPageIds.DownloadMod
        };

        var loaderType = ResolveLoader(loader);
        var context = new CompSearchContext(
            string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion.Trim(),
            loaderType);
        PageRouter.Navigate(PageIds.Download, subPage, context);
        return $"已打开资源下载页（类型：{key}，游戏版本：{gameVersion ?? "自动"}，加载器：{loaderType}）。";
    }

    private static Comp.CompLoaderType ResolveLoader(string? loader)
    {
        if (string.IsNullOrWhiteSpace(loader))
            return Comp.CompLoaderType.Any;
        return Enum.TryParse<Comp.CompLoaderType>(loader.Trim(), true, out var value)
            ? value
            : Comp.CompLoaderType.Any;
    }

    private static string GetGameLog(int lines)
    {
        lines = Math.Clamp(lines, 20, 2000);
        foreach (var path in EnumerateLogPaths())
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var tail = File.ReadLines(path).TakeLast(lines).ToArray();
                return new JsonObject
                {
                    ["path"] = path,
                    ["lines"] = tail.Length,
                    ["content"] = string.Join(Environment.NewLine, tail)
                }.ToJsonString();
            }
            catch (Exception ex)
            {
                return "读取日志失败：" + ex.Message;
            }
        }

        return "没有找到 latest.log。可以等游戏先启动过一次后再读取。";
    }

    private static string ResolveMinecraftFolder()
    {
        return string.IsNullOrWhiteSpace(LauncherPaths.MinecraftFolder)
            ? LauncherPaths.DefaultMinecraftDirectory
            : LauncherPaths.MinecraftFolder!;
    }

    private static IEnumerable<string> EnumerateLogPaths()
    {
        var instance = InstanceList.McMcInstanceSelected;
        if (instance is not null)
            yield return Path.Combine(instance.PathInstance, "logs", "latest.log");
        yield return Path.Combine(ResolveMinecraftFolder(), "logs", "latest.log");
    }

    private static string GetLastCrash()
    {
        if (!string.IsNullOrWhiteSpace(AiChatService.Instance.LastCrashReport))
            return AiChatService.Instance.LastCrashReport!;

        foreach (var folder in EnumerateCrashFolders())
        {
            if (!Directory.Exists(folder))
                continue;
            try
            {
                var latest = Directory.GetFiles(folder, "*.txt")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (latest is null)
                    continue;
                var content = File.ReadAllText(latest);
                return new JsonObject
                {
                    ["path"] = latest,
                    ["content"] = content.Length > 40000 ? content[..40000] : content
                }.ToJsonString();
            }
            catch (Exception ex)
            {
                return "读取崩溃报告失败：" + ex.Message;
            }
        }

        return "没有找到崩溃报告。如果游戏刚刚崩溃，可以让我读取游戏日志来分析。";
    }

    private static IEnumerable<string> EnumerateCrashFolders()
    {
        var instance = InstanceList.McMcInstanceSelected;
        if (instance is not null)
            yield return Path.Combine(instance.PathInstance, "crash-reports");
        yield return Path.Combine(ResolveMinecraftFolder(), "crash-reports");
    }

    private static string OpenInstallPage(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadInstall);
            return "已打开 Minecraft 安装页。安装时需要选择版本、加载器和安装位置，页面里可以直接完成。";
        }

        var name = version.Trim();
        // 安装页支持以字符串参数预选版本。
        PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadInstall, name);
        return $"已打开 Minecraft 安装页并尝试选中版本“{name}”。确认加载器和安装位置后点击开始安装即可；装完告诉我，我可以帮你切换过去。";
    }
}
