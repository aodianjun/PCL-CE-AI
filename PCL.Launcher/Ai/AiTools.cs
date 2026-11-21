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

        JsonArray Required(params string[] names)
        {
            var array = new JsonArray();
            foreach (var name in names)
                array.Add(name);
            return array;
        }

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
                Required("name")),
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
                Required("file", "enabled")),
            Tool(
                "delete_mod",
                "永久删除当前版本 mods 文件夹中的某个 Mod。必须先得到用户明确确认。",
                new JsonObject
                {
                    ["file"] = Str("要删除的 Mod 文件名")
                },
                Required("file")),
            Tool(
                "open_page",
                "在启动器中打开指定页面。可选值：launch、download、mod、modpack、resourcepack、shader、world、datapack、favorites、tools、settings、instance_settings、instance_select、logs、tasks。",
                new JsonObject
                {
                    ["page"] = Str("页面标识"),
                    ["navigate"] = Bool("是否真的切换页面。只有用户明确要求打开页面时才设为 true；默认 false，只返回页面位置，避免打断用户当前操作")
                },
                Required("page")),
            Tool(
                "search_resource",
                "打开资源下载页并按游戏版本/加载器筛选。",
                new JsonObject
                {
                    ["type"] = Str("资源类型：mod、modpack、resourcepack、shader、world、datapack"),
                    ["game_version"] = Str("可选，目标 Minecraft 版本，例如 1.21.1"),
                    ["loader"] = Str("可选，加载器：forge、neoforge、fabric、quilt、liteloader、cleanroom"),
                    ["navigate"] = Bool("是否真的切换页面。只有用户明确要求打开时才设为 true；默认 false")
                },
                Required("type")),
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
                    ["version"] = Str("可选，目标版本号"),
                    ["navigate"] = Bool("是否真的切换页面。只有用户明确要求打开安装页时才设为 true；默认 false")
                }),
            Tool(
                "get_work_directory",
                "读取 AI 当前的工作目录和允许访问的文件夹。",
                new JsonObject()),
            Tool(
                "list_files",
                "列出工作目录或允许文件夹中的文件。",
                new JsonObject
                {
                    ["path"] = Str("可选，相对工作目录的路径或允许范围内的绝对路径；留空表示工作目录根"),
                    ["pattern"] = Str("可选，文件名通配符，例如 *.json，默认 *"),
                    ["recursive"] = Bool("是否递归子目录，默认 false")
                }),
            Tool(
                "read_file",
                "读取工作目录或允许文件夹中的文本文件。",
                new JsonObject
                {
                    ["path"] = Str("相对工作目录的路径或允许范围内的绝对路径"),
                    ["max_chars"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "最多返回的字符数，默认 20000"
                    }
                },
                Required("path")),
            Tool(
                "search_files",
                "按文件名搜索工作目录或允许文件夹中的文件。",
                new JsonObject
                {
                    ["query"] = Str("文件名关键字，不区分大小写"),
                    ["path"] = Str("可选，搜索起点")
                },
                Required("query")),
            Tool(
                "open_folder",
                "在资源管理器中打开工作目录或允许文件夹中的目录。",
                new JsonObject
                {
                    ["path"] = Str("可选，要打开的目录；留空表示工作目录")
                })
        );
    }

    /// <summary>执行一次工具调用，返回给模型的文本结果。工具统一在线程池执行，不占用 UI 线程。</summary>
    public static Task<string> ExecuteAsync(AiToolCall call, CancellationToken ct)
    {
        return Task.Run(() => ExecuteCoreAsync(call, ct), ct);
    }

    private static async Task<string> ExecuteCoreAsync(AiToolCall call, CancellationToken ct)
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
                "open_page" => OpenPage(
                    GetString(args, "page"),
                    GetBool(args, "navigate")),
                "search_resource" => SearchResource(
                    GetString(args, "type"),
                    GetString(args, "game_version"),
                    GetString(args, "loader"),
                    GetBool(args, "navigate")),
                "get_game_log" => GetGameLog(GetInt(args, "lines", DefaultLogLines)),
                "get_last_crash" => GetLastCrash(),
                "install_version_note" => OpenInstallPage(
                    GetString(args, "version"),
                    GetBool(args, "navigate")),
                "get_work_directory" => GetWorkDirectoryInfo(),
                "list_files" => ListFiles(
                    GetString(args, "path"),
                    GetString(args, "pattern"),
                    GetBool(args, "recursive")),
                "read_file" => ReadTextFile(
                    GetString(args, "path"),
                    GetInt(args, "max_chars", 20000)),
                "search_files" => SearchFiles(
                    GetString(args, "query"),
                    GetString(args, "path")),
                "open_folder" => OpenFolder(GetString(args, "path")),
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
            try
            {
                builder.AppendLine($"- AI 工作目录：{(string.IsNullOrWhiteSpace(Config.Ai.WorkDirectory) ? "未设置" : Config.Ai.WorkDirectory)}");
                if (!string.IsNullOrWhiteSpace(Config.Ai.AllowedDirectories))
                    builder.AppendLine($"- 允许访问的文件夹：{Config.Ai.AllowedDirectories}");
            }
            catch (Exception)
            {
                // 配置未就绪时忽略。
            }
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

        await Dispatcher.UIThread.InvokeAsync(() => GameLaunch.McLaunchStart());

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

    private static string OpenPage(string? page, bool navigate)
    {
        var key = (page ?? "").Trim().ToLowerInvariant();
        string description;
        Action? action;
        switch (key)
        {
            case "launch":
                description = "启动页";
                action = () => PageRouter.Navigate(PageIds.Launch);
                break;
            case "download":
            case "install":
                description = "Minecraft 安装页";
                action = () => PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadInstall);
                break;
            case "mod":
                description = "Mod 下载页";
                action = () => PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadMod);
                break;
            case "modpack":
                description = "整合包下载页";
                action = () => PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadPack);
                break;
            case "resourcepack":
                description = "资源包下载页";
                action = () => PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadResourcePack);
                break;
            case "shader":
                description = "光影下载页";
                action = () => PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadShader);
                break;
            case "world":
                description = "世界下载页";
                action = () => PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadWorld);
                break;
            case "datapack":
                description = "数据包下载页";
                action = () => PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadDataPack);
                break;
            case "favorites":
                description = "收藏页";
                action = () => PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadCompFavorites);
                break;
            case "tools":
                description = "工具页";
                action = () => PageRouter.Navigate(PageIds.Tools);
                break;
            case "settings":
            case "setup":
                description = "设置页";
                action = () => PageRouter.Navigate(PageIds.Setup);
                break;
            case "instance_settings":
            {
                var instance = InstanceList.McMcInstanceSelected;
                if (instance is null)
                    return "当前没有选择版本，无法打开实例设置。";
                description = $"版本“{instance.Name}”的设置页";
                action = () => PageRouter.Navigate(PageIds.InstanceSetup, null, instance);
                break;
            }
            case "instance_select":
                description = "版本选择页";
                action = () => PageRouter.Navigate(PageIds.InstanceSelect);
                break;
            case "logs":
            case "game_log":
                description = "游戏日志页";
                action = () => PageRouter.Navigate(PageIds.GameLog);
                break;
            case "tasks":
                description = "任务管理页";
                action = () => PageRouter.Navigate(PageIds.TaskManager);
                break;
            default:
                return $"未知页面“{page}”。可用值：launch、download、mod、modpack、resourcepack、shader、world、datapack、favorites、tools、settings、instance_settings、instance_select、logs、tasks。";
        }

        if (action is null)
            return $"未知页面“{page}”。";

        if (!navigate)
            return $"页面位置：{description}。为避免打断你当前的操作，没有自动切换页面；需要时从“设置 → 功能入口”进入，或明确告诉我“打开{description}”。";

        action();
        return $"已打开{description}。";
    }

    private static string SearchResource(
        string? type,
        string? gameVersion,
        string? loader,
        bool navigate)
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
        var description =
            $"资源下载页（类型：{key}，游戏版本：{gameVersion ?? "自动"}，加载器：{loaderType}）";
        if (!navigate)
            return $"页面位置：{description}。为避免打断当前页面，没有自动切换。";

        var context = new CompSearchContext(
            string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion.Trim(),
            loaderType);
        PageRouter.Navigate(PageIds.Download, subPage, context);
        return $"已打开{description}。";
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

    private static readonly string[] TextFileExtensions =
    [
        ".txt", ".md", ".json", ".log", ".yml", ".yaml", ".toml", ".ini", ".cfg",
        ".conf", ".properties", ".xml", ".html", ".css", ".js", ".ts", ".cs",
        ".java", ".kt", ".py", ".sh", ".ps1", ".bat", ".csv", ".mcmeta", ".lang"
    ];

    private static IReadOnlyList<string> GetAllowedRoots()
    {
        var roots = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            try
            {
                var full = Path.GetFullPath(value.Trim()).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                if (full.Length > 0 && !roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                    roots.Add(full);
            }
            catch (Exception)
            {
                // 忽略非法路径。
            }
        }

        Add(Config.Ai.WorkDirectory);
        foreach (var item in (Config.Ai.AllowedDirectories ?? "")
                     .Split([';', '|'], StringSplitOptions.RemoveEmptyEntries))
            Add(item);

        // 当前 Minecraft 目录与实例目录默认允许访问，方便读写配置和日志。
        Add(LauncherPaths.MinecraftFolder);
        Add(InstanceList.McMcInstanceSelected?.PathInstance);
        return roots;
    }

    private static string? ResolveAllowedPath(string? path, out string? error)
    {
        error = null;
        var roots = GetAllowedRoots();
        if (roots.Count == 0)
        {
            error = "还没有设置工作目录。请让用户先在“设置 → AI 接入”里选择工作目录。";
            return null;
        }

        var baseRoot = roots[0];
        string candidate;
        try
        {
            candidate = string.IsNullOrWhiteSpace(path)
                ? baseRoot
                : Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(Path.Combine(baseRoot, path));
        }
        catch (Exception ex)
        {
            error = "路径无效：" + ex.Message;
            return null;
        }

        candidate = candidate.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        foreach (var root in roots)
        {
            if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        error = "拒绝访问：该路径不在工作目录或允许访问的文件夹内。";
        return null;
    }

    private static string GetWorkDirectoryInfo()
    {
        var roots = GetAllowedRoots();
        var result = new JsonObject
        {
            ["work_directory"] = Config.Ai.WorkDirectory,
            ["allowed_directories"] = new JsonArray(roots.Select(root => (JsonNode?)root).ToArray())
        };
        return result.ToJsonString();
    }

    private static string ListFiles(string? path, string? pattern, bool recursive)
    {
        var directory = ResolveAllowedPath(path, out var error);
        if (directory is null)
            return error ?? "无法访问该路径。";
        if (!Directory.Exists(directory))
            return "目录不存在：" + directory;

        try
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory
                .EnumerateFileSystemEntries(directory, string.IsNullOrWhiteSpace(pattern) ? "*" : pattern, option)
                .Take(300)
                .Select(item =>
                {
                    var isDirectory = Directory.Exists(item);
                    var info = isDirectory ? null : new FileInfo(item);
                    return (JsonNode)new JsonObject
                    {
                        ["name"] = Path.GetFileName(item),
                        ["type"] = isDirectory ? "dir" : "file",
                        ["size"] = info?.Length ?? 0
                    };
                })
                .ToArray();
            return new JsonObject
            {
                ["directory"] = directory,
                ["count"] = files.Length,
                ["entries"] = new JsonArray(files)
            }.ToJsonString();
        }
        catch (Exception ex)
        {
            return "列出文件失败：" + ex.Message;
        }
    }

    private static string ReadTextFile(string? path, int maxChars)
    {
        var file = ResolveAllowedPath(path, out var error);
        if (file is null)
            return error ?? "无法访问该路径。";
        if (!File.Exists(file))
            return "文件不存在：" + file;

        var extension = Path.GetExtension(file).ToLowerInvariant();
        if (!TextFileExtensions.Contains(extension))
            return $"不支持读取该文件类型：{extension}。只支持文本类文件。";

        try
        {
            var info = new FileInfo(file);
            if (info.Length > 2 * 1024 * 1024)
                return "文件超过 2 MB，拒绝读取。";
            var text = File.ReadAllText(file);
            maxChars = Math.Clamp(maxChars, 1000, 100000);
            if (text.Length > maxChars)
                text = text[..maxChars] + "\n…（已截断）";
            return new JsonObject
            {
                ["path"] = file,
                ["chars"] = text.Length,
                ["content"] = text
            }.ToJsonString();
        }
        catch (Exception ex)
        {
            return "读取文件失败：" + ex.Message;
        }
    }

    private static string SearchFiles(string? query, string? path)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "缺少参数 query。";
        var directory = ResolveAllowedPath(path, out var error);
        if (directory is null)
            return error ?? "无法访问该路径。";
        if (!Directory.Exists(directory))
            return "目录不存在：" + directory;

        try
        {
            var matches = Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(file => Path.GetFileName(file).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
                .Take(200)
                .Select(file => (JsonNode)file)
                .ToArray();
            return new JsonObject
            {
                ["query"] = query,
                ["count"] = matches.Length,
                ["files"] = new JsonArray(matches)
            }.ToJsonString();
        }
        catch (Exception ex)
        {
            return "搜索失败：" + ex.Message;
        }
    }

    private static string OpenFolder(string? path)
    {
        var directory = ResolveAllowedPath(path, out var error);
        if (directory is null)
            return error ?? "无法访问该路径。";
        if (!Directory.Exists(directory))
            return "目录不存在：" + directory;

        Dispatcher.UIThread.Post(() => Basics.OpenPath(directory));
        return "已在资源管理器中打开：" + directory;
    }

    private static string OpenInstallPage(string? version, bool navigate)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            if (!navigate)
                return "页面位置：Minecraft 安装页。为避免打断当前页面，没有自动切换。";
            PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadInstall);
            return "已打开 Minecraft 安装页。安装时需要选择版本、加载器和安装位置，页面里可以直接完成。";
        }

        var name = version.Trim();
        if (!navigate)
            return $"页面位置：Minecraft 安装页（可预选版本“{name}”）。为避免打断当前页面，没有自动切换。";

        // 安装页支持以字符串参数预选版本。
        PageRouter.Navigate(PageIds.Download, SubPageIds.DownloadInstall, name);
        return $"已打开 Minecraft 安装页并尝试选中版本“{name}”。确认加载器和安装位置后点击开始安装即可；装完告诉我，我可以帮你切换过去。";
    }
}
