using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Navigation;

namespace PCL.Ai;

/// <summary>聊天消息角色，与 OpenAI 兼容接口保持一致。</summary>
public enum AiRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>一次工具调用请求。</summary>
public sealed class AiToolCall
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Arguments { get; set; } = "";
}

/// <summary>AI 对话中的一条消息。内容在流式返回时会被原地追加。</summary>
public sealed class AiChatMessage
{
    public AiRole Role { get; init; }

    public string Content { get; set; } = "";

    /// <summary>该消息是否只提交给模型、不在聊天界面显示（例如完整的崩溃报告）。</summary>
    public bool IsHidden { get; init; }

    /// <summary>是否为错误提示气泡。</summary>
    public bool IsError { get; set; }

    /// <summary>工具调用结果消息对应的调用 ID。</summary>
    public string? ToolCallId { get; init; }

    /// <summary>工具名称。</summary>
    public string? ToolName { get; init; }

    /// <summary>助手消息中请求的工具调用。</summary>
    public List<AiToolCall> ToolCalls { get; } = [];

    public DateTime CreatedAt { get; init; } = DateTime.Now;
}

/// <summary>
/// DeepSeek harness：兼容 OpenAI Chat Completions 的会话服务。
/// 负责流式对话、工具调用循环、历史裁剪，以及把崩溃报告自动交给模型分析。
/// 所有事件都在后台线程触发，界面层需要自行切回 UI 线程。
/// </summary>
public sealed class AiChatService
{
    private const int MaxToolRounds = 8;
    private const int MaxCrashReportChars = 12000;

    private static readonly Lazy<AiChatService> LazyInstance = new(() => new AiChatService());

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _messageLock = new();
    private readonly List<AiConversation> _conversations = [];
    private List<AiChatMessage> _messages = [];
    private AiConversation _current = new();
    private bool _conversationsLoaded;

    private CancellationTokenSource? _cts;

    private AiChatService()
    {
        LoadConversations();
    }

    public static AiChatService Instance => LazyInstance.Value;

    public event Action<AiChatMessage>? MessageAdded;

    public event Action<AiChatMessage>? MessageUpdated;

    public event Action? HistoryCleared;

    public event Action<bool>? BusyChanged;

    public event Action<string?>? StatusChanged;

    /// <summary>会话列表发生变化（新建、删除、标题更新）。</summary>
    public event Action? ConversationsChanged;

    /// <summary>当前会话被切换或重建，界面需要重新渲染消息。</summary>
    public event Action? ConversationSwitched;

    public bool IsBusy { get; private set; }

    /// <summary>当前对话快照，可在任意线程安全读取。</summary>
    public IReadOnlyList<AiChatMessage> Messages
    {
        get
        {
            lock (_messageLock)
                return _messages.ToArray();
        }
    }

    /// <summary>全部会话快照，按最近使用时间倒序。</summary>
    public IReadOnlyList<AiConversation> Conversations
    {
        get
        {
            lock (_messageLock)
                return _conversations.OrderByDescending(item => item.UpdatedAt).ToArray();
        }
    }

    public AiConversation CurrentConversation => _current;

    /// <summary>最近一次崩溃报告，可被工具读取。</summary>
    public string? LastCrashReport { get; private set; }

    /// <summary>当前是否已配置 API Key。</summary>
    public static bool IsConfigured
    {
        get
        {
            try
            {
                return !string.IsNullOrWhiteSpace(Config.Ai.ApiKey);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public string? LastError { get; private set; }

    /// <summary>新建会话并切换过去。</summary>
    public void NewConversation()
    {
        if (IsBusy) Cancel();
        SaveConversations();

        var conversation = new AiConversation();
        lock (_messageLock)
        {
            _conversations.Insert(0, conversation);
            _current = conversation;
            _messages = conversation.Messages;
        }

        TrimConversations();
        SaveConversations();
        ConversationsChanged?.Invoke();
        ConversationSwitched?.Invoke();
    }

    /// <summary>切换到指定会话。</summary>
    public void SelectConversation(string id)
    {
        if (IsBusy) Cancel();

        AiConversation? target;
        lock (_messageLock)
        {
            target = _conversations.FirstOrDefault(item => item.Id == id);
            if (target is null || ReferenceEquals(target, _current))
                return;
            _current = target;
            _messages = target.Messages;
        }

        _current.UpdatedAt = DateTime.Now;
        SaveConversations();
        ConversationsChanged?.Invoke();
        ConversationSwitched?.Invoke();
    }

    /// <summary>删除会话；若删除的是当前会话，则切换到最近的其它会话。</summary>
    public void DeleteConversation(string id)
    {
        if (IsBusy) Cancel();

        lock (_messageLock)
        {
            var target = _conversations.FirstOrDefault(item => item.Id == id);
            if (target is null)
                return;
            _conversations.Remove(target);
            if (ReferenceEquals(target, _current))
            {
                if (_conversations.Count == 0)
                    _conversations.Add(new AiConversation());
                _current = _conversations.OrderByDescending(item => item.UpdatedAt).First();
                _messages = _current.Messages;
            }
        }

        SaveConversations();
        ConversationsChanged?.Invoke();
        ConversationSwitched?.Invoke();
    }

    /// <summary>手动把当前会话压缩成摘要，只保留最近两条消息。</summary>
    public async Task CompressAsync()
    {
        if (!IsConfigured)
        {
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = "还没有配置 API Key。请点击左下角的“设置 → AI 接入”完成接入。",
                IsError = true
            });
            return;
        }

        if (IsBusy)
        {
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = Lang.Text("Launch.Ai.Compress.Busy"),
                IsError = true
            });
            return;
        }

        await _gate.WaitAsync();
        try
        {
            IsBusy = true;
            BusyChanged?.Invoke(true);
            LastError = null;
            using var cts = new CancellationTokenSource();
            _cts = cts;
            StatusChanged?.Invoke("正在压缩上下文…");
            var compressed = await CompressCoreAsync(cts.Token, 2);
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = compressed
                    ? Lang.Text("Launch.Ai.Compressed")
                    : Lang.Text("Launch.Ai.Compress.TooShort")
            });
        }
        catch (OperationCanceledException)
        {
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = "已取消本次压缩。",
                IsError = true
            });
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LogWrapper.Error(ex, "Ai", "压缩上下文失败");
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = "压缩上下文失败：" + ex.Message,
                IsError = true
            });
        }
        finally
        {
            _cts = null;
            IsBusy = false;
            StatusChanged?.Invoke(null);
            BusyChanged?.Invoke(false);
            SaveConversations();
            _gate.Release();
        }
    }

    /// <summary>把较早的消息交给模型生成摘要，替换成一条隐藏的系统消息。</summary>
    private async Task<bool> CompressCoreAsync(CancellationToken ct, int keepRecent)
    {
        List<AiChatMessage> snapshot;
        lock (_messageLock)
            snapshot = _messages
                .Where(message => !message.IsHidden
                                  && !string.IsNullOrWhiteSpace(message.Content)
                                  && message.Role is AiRole.User or AiRole.Assistant)
                .ToList();

        if (snapshot.Count < 4)
            return false;

        var compressible = snapshot.Take(snapshot.Count - keepRecent).ToList();
        if (compressible.Count < 2)
            return false;

        var transcript = string.Join(
            "\n",
            compressible.Select(message =>
                $"{(message.Role == AiRole.User ? "用户" : "助手")}：{Truncate(message.Content, 4000)}"));
        var summary = await CompleteOnceAsync(
            "你是上下文压缩器。把下面的对话压缩成不超过 600 字的中文摘要，保留用户目标、关键决定、已执行的操作、结果与未完成事项。只输出摘要，不要寒暄。",
            transcript,
            ct);
        if (string.IsNullOrWhiteSpace(summary))
            return false;

        lock (_messageLock)
        {
            var recent = snapshot
                .Skip(Math.Max(0, snapshot.Count - keepRecent))
                .ToList();
            _messages.Clear();
            _messages.Add(new AiChatMessage
            {
                Role = AiRole.System,
                Content = "【历史对话摘要】\n" + summary.Trim(),
                IsHidden = true
            });
            foreach (var message in recent)
                _messages.Add(message);
        }

        _current.Summary = summary.Trim();
        _current.UpdatedAt = DateTime.Now;
        SaveConversations();
        HistoryCleared?.Invoke();
        return true;
    }

    /// <summary>上下文超过阈值时自动压缩。</summary>
    private async Task AutoCompressIfNeededAsync(CancellationToken ct)
    {
        bool enabled;
        int threshold;
        try
        {
            enabled = Config.Ai.AutoCompress;
            threshold = Math.Max(4000, Config.Ai.CompressThreshold);
        }
        catch (Exception)
        {
            return;
        }

        if (!enabled)
            return;

        int chars;
        lock (_messageLock)
            chars = _messages
                .Where(message => !message.IsHidden)
                .Sum(message => message.Content.Length);
        if (chars < threshold)
            return;

        StatusChanged?.Invoke("上下文较长，正在压缩…");
        var compressed = await CompressCoreAsync(ct, 4);
        if (compressed)
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = Lang.Text("Launch.Ai.Compressed")
            });
    }

    /// <summary>一次性、非流式地完成一次请求，供上下文压缩使用。</summary>
    private async Task<string> CompleteOnceAsync(
        string systemPrompt,
        string userContent,
        CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(Config.Ai.Model)
                ? "deepseek-chat"
                : Config.Ai.Model.Trim(),
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = systemPrompt
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = userContent
                }
            },
            ["stream"] = false,
            ["temperature"] = 0.2,
            ["max_tokens"] = 900
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            ResolveEndpoint(Config.Ai.BaseUrl))
        {
            Content = new StringContent(
                payload.ToJsonString(),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Config.Ai.ApiKey.Trim());
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"接口返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var node = JsonNode.Parse(json);
        return node?["choices"]?[0]?["message"]?["content"]?.ToString()?.Trim() ?? "";
    }

    public void Clear()
    {
        if (IsBusy) Cancel();
        lock (_messageLock)
            _messages.Clear();
        _current.Title = "";
        _current.Summary = null;
        _current.UpdatedAt = DateTime.Now;
        LastError = null;
        SaveConversations();
        ConversationsChanged?.Invoke();
        HistoryCleared?.Invoke();
    }

    public void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (Exception ex)
        {
            LogWrapper.Debug(ex, "Ai", "取消 AI 请求失败");
        }
    }

    /// <summary>从聊天界面发送一条用户消息。</summary>
    public Task SendAsync(string text)
    {
        return SendCoreAsync(text, false);
    }

    /// <summary>把游戏崩溃报告转交给 AI 分析。报告正文隐藏提交，聊天里只显示一条提示。</summary>
    public void SubmitCrashReport(string report)
    {
        if (string.IsNullOrWhiteSpace(report))
            return;

        report = Truncate(report, MaxCrashReportChars);
        LastCrashReport = report;

        // 没有接入 API 时只保留报告，等用户接好 Key 再让 AI 读取。
        if (!IsConfigured)
            return;

        // 崩溃可能发生在任意页面，先把用户带回启动页的对话界面。
        PageRouter.Navigate(PageIds.Launch);

        var note = new AiChatMessage
        {
            Role = AiRole.System,
            Content = "【游戏崩溃报告】\n" + report,
            IsHidden = true
        };
        AddMessage(note);

        _ = SendCoreAsync(
            "游戏刚刚崩溃了。请结合自动提交的崩溃报告，用简洁的中文说明崩溃原因，并给出可以直接执行的修复步骤。",
            true);
    }

    private async Task SendCoreAsync(string text, bool auto)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0)
            return;
        if (!IsConfigured)
        {
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = "还没有配置 API Key。请点击左下角的“设置 → AI 接入”完成接入。",
                IsError = true
            });
            return;
        }

        if (IsBusy)
        {
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = "上一个请求还在处理中，请稍候或先取消。",
                IsError = true
            });
            return;
        }

        await _gate.WaitAsync();
        try
        {
            IsBusy = true;
            BusyChanged?.Invoke(true);
            LastError = null;

            using var cts = new CancellationTokenSource();
            _cts = cts;

            await AutoCompressIfNeededAsync(cts.Token);

            AddMessage(new AiChatMessage
            {
                Role = AiRole.User,
                Content = text
            });
            UpdateConversationTitle(text);
            TrimHistory();

            await RunToolLoopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = "已取消本次回复。",
                IsError = true
            });
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LogWrapper.Error(ex, "Ai", "AI 请求失败");
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = "AI 请求失败：" + ex.Message,
                IsError = true
            });
        }
        finally
        {
            _cts = null;
            IsBusy = false;
            StatusChanged?.Invoke(null);
            BusyChanged?.Invoke(false);
            SaveConversations();
            _gate.Release();
        }
    }

    private void UpdateConversationTitle(string text)
    {
        _current.UpdatedAt = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(_current.Title))
            return;

        var title = text.ReplaceLineEndings(" ").Trim();
        if (title.Length > 24)
            title = title[..24] + "…";
        _current.Title = title;
        ConversationsChanged?.Invoke();
    }

    private async Task RunToolLoopAsync(CancellationToken ct)
    {
        for (var round = 0; round < MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            StatusChanged?.Invoke(round == 0 ? "AI 正在思考…" : "AI 正在继续处理…");

            var assistant = new AiChatMessage
            {
                Role = AiRole.Assistant
            };
            AddMessage(assistant);

            await StreamCompletionAsync(assistant, ct);

            if (assistant.ToolCalls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(assistant.Content))
                    assistant.Content = "（模型没有返回内容）";
                NotifyUpdated(assistant);
                return;
            }

            if (!Config.Ai.EnableTools)
            {
                assistant.Content += "\n\n（工具调用已被设置禁用，未执行）";
                NotifyUpdated(assistant);
                return;
            }

            foreach (var call in assistant.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                StatusChanged?.Invoke($"正在执行 {DescribeTool(call.Name)}…");
                var result = await AiTools.ExecuteAsync(call, ct);
                AddMessage(new AiChatMessage
                {
                    Role = AiRole.Tool,
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    Content = result
                });
            }
        }

        AddMessage(new AiChatMessage
        {
            Role = AiRole.Assistant,
            Content = "工具调用次数达到上限，已停止本轮处理。你可以直接问我要结果。",
            IsError = true
        });
    }

    private async Task StreamCompletionAsync(AiChatMessage assistant, CancellationToken ct)
    {
        var payload = BuildRequestPayload();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            ResolveEndpoint(Config.Ai.BaseUrl))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Config.Ai.ApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"接口返回 {(int)response.StatusCode} {response.ReasonPhrase}：{Truncate(body, 500)}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // 某些中转会忽略 stream=true，直接返回完整 JSON。
            var json = await response.Content.ReadAsStringAsync(ct);
            ApplyFullResponse(assistant, json);
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var accumulators = new Dictionary<int, AiToolCall>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line[5..].Trim();
            if (data.Length == 0)
                continue;
            if (data == "[DONE]")
                break;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }

            var delta = node?["choices"]?[0]?["delta"];
            if (delta is null)
                continue;

            var content = delta["content"]?.ToString();
            if (!string.IsNullOrEmpty(content))
            {
                assistant.Content += content;
                NotifyUpdated(assistant);
            }

            if (delta["tool_calls"] is JsonArray toolCalls)
                ApplyToolCallDeltas(toolCalls, accumulators, assistant);
        }

        assistant.ToolCalls.Clear();
        assistant.ToolCalls.AddRange(accumulators.OrderBy(pair => pair.Key).Select(pair => pair.Value));
    }

    private void ApplyToolCallDeltas(
        JsonArray deltas,
        Dictionary<int, AiToolCall> accumulators,
        AiChatMessage assistant)
    {
        foreach (var item in deltas.OfType<JsonObject>())
        {
            var index = item["index"]?.GetValue<int>() ?? 0;
            if (!accumulators.TryGetValue(index, out var call))
            {
                call = new AiToolCall();
                accumulators[index] = call;
            }

            var id = item["id"]?.ToString();
            if (!string.IsNullOrEmpty(id))
                call.Id = id;

            var function = item["function"];
            if (function is null)
                continue;

            var name = function["name"]?.ToString();
            if (!string.IsNullOrEmpty(name))
                call.Name = name;

            var arguments = function["arguments"]?.ToString();
            if (!string.IsNullOrEmpty(arguments))
                call.Arguments += arguments;
        }

        if (assistant.ToolCalls.Count == 0 && accumulators.Count > 0)
            NotifyUpdated(assistant);
    }

    private static void ApplyFullResponse(AiChatMessage assistant, string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("接口返回了无法解析的内容：" + Truncate(json, 300));
        }

        var message = node?["choices"]?[0]?["message"];
        if (message is null)
            throw new InvalidOperationException("接口返回中没有 choices[0].message");

        assistant.Content = message["content"]?.ToString() ?? "";
        if (message["tool_calls"] is JsonArray toolCalls)
            foreach (var item in toolCalls.OfType<JsonObject>())
            {
                var function = item["function"];
                assistant.ToolCalls.Add(new AiToolCall
                {
                    Id = item["id"]?.ToString() ?? "",
                    Name = function?["name"]?.ToString() ?? "",
                    Arguments = function?["arguments"]?.ToString() ?? ""
                });
            }
    }

    private JsonObject BuildRequestPayload()
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = BuildSystemPrompt()
            }
        };

        foreach (var message in Messages)
        {
            if (message.IsError)
                continue;
            if (message.Role == AiRole.System && !message.IsHidden)
                continue;
            if (message.Role == AiRole.Assistant
                && message.ToolCalls.Count == 0
                && string.IsNullOrWhiteSpace(message.Content))
                continue;

            var item = new JsonObject
            {
                ["role"] = message.Role switch
                {
                    AiRole.User => "user",
                    AiRole.Assistant => "assistant",
                    AiRole.Tool => "tool",
                    _ => "system"
                }
            };

            if (message.Role == AiRole.Tool)
            {
                item["tool_call_id"] = message.ToolCallId ?? "";
                item["content"] = message.Content;
            }
            else if (message.Role == AiRole.Assistant && message.ToolCalls.Count > 0)
            {
                item["content"] = string.IsNullOrEmpty(message.Content)
                    ? null
                    : message.Content;
                var calls = new JsonArray();
                foreach (var call in message.ToolCalls)
                    calls.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.Arguments
                        }
                    });
                item["tool_calls"] = calls;
            }
            else
            {
                item["content"] = message.Content;
            }

            messages.Add(item);
        }

        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(Config.Ai.Model)
                ? "deepseek-chat"
                : Config.Ai.Model.Trim(),
            ["messages"] = messages,
            ["stream"] = true,
            ["temperature"] = 0.3
        };

        if (Config.Ai.EnableTools)
            payload["tools"] = AiTools.BuildDefinitions();

        return payload;
    }

    private static string BuildSystemPrompt()
    {
        var minecraft = AiTools.GetEnvironmentSummary();
        var extra = Config.Ai.SystemPromptExtra;
        var builder = new StringBuilder();
        builder.AppendLine("你是 PCL CE-AI 启动器内置的 AI 助手，运行在「纯 AI 启动页」里。");
        builder.AppendLine("你的职责：用中文帮用户完成与 Minecraft 启动器有关的一切事情，包括选择版本、启动游戏、下载与安装游戏和加载器、管理 Mod 与整合包、分析崩溃和报错、调整启动器设置。");
        builder.AppendLine("规则：");
        builder.AppendLine("1. 需要真实数据时先调用工具，不要凭空编造版本名、路径、Mod 列表或崩溃原因。");
        builder.AppendLine("2. 能直接执行的操作（启动游戏、切换版本、启用/禁用 Mod、打开页面等）直接调用工具执行，然后告诉用户结果。");
        builder.AppendLine("3. 涉及删除 Mod、删除版本等不可逆操作时，先向用户确认。");
        builder.AppendLine("4. 回答简洁，先给结论，再给步骤。");
        builder.AppendLine("5. 用户只说“启动”“开游戏”时，直接调用 launch_game。");
        builder.AppendLine();
        builder.AppendLine("当前启动器环境：");
        builder.AppendLine(minecraft);
        if (!string.IsNullOrWhiteSpace(extra))
        {
            builder.AppendLine();
            builder.AppendLine("用户附加要求：");
            builder.AppendLine(extra.Trim());
        }

        return builder.ToString();
    }

    private void AddMessage(AiChatMessage message)
    {
        lock (_messageLock)
            _messages.Add(message);
        MessageAdded?.Invoke(message);
    }

    private void NotifyUpdated(AiChatMessage message)
    {
        MessageUpdated?.Invoke(message);
    }

    private void LoadConversations()
    {
        if (_conversationsLoaded)
            return;
        _conversationsLoaded = true;

        try
        {
            var (list, currentId) = AiConversationStore.Load();
            _conversations.AddRange(list);
            if (_conversations.Count == 0)
                _conversations.Add(new AiConversation());
            _current = _conversations.FirstOrDefault(item => item.Id == currentId)
                       ?? _conversations.OrderByDescending(item => item.UpdatedAt).First();
        }
        catch (Exception ex)
        {
            LogWrapper.Warn("Ai", "初始化会话列表失败：" + ex.Message);
            _conversations.Clear();
            _conversations.Add(new AiConversation());
            _current = _conversations[0];
        }

        lock (_messageLock)
            _messages = _current.Messages;
    }

    private void SaveConversations()
    {
        List<AiConversation> snapshot;
        string currentId;
        lock (_messageLock)
        {
            snapshot = _conversations.ToList();
            currentId = _current.Id;
        }

        AiConversationStore.Save(snapshot, currentId);
    }

    private void TrimConversations()
    {
        lock (_messageLock)
        {
            while (_conversations.Count > 40)
            {
                var removable = _conversations
                    .Where(item => !ReferenceEquals(item, _current))
                    .OrderBy(item => item.UpdatedAt)
                    .FirstOrDefault();
                if (removable is null)
                    break;
                _conversations.Remove(removable);
            }
        }
    }

    private void TrimHistory()
    {
        var limit = Math.Max(120, Config.Ai.HistoryLimit);
        lock (_messageLock)
        {
            while (_messages.Count > limit)
            {
                _messages.RemoveAt(0);
                // 避免把工具结果与它的助手消息拆散。
                while (_messages.Count > 0
                       && (_messages[0].Role == AiRole.Tool
                           || (_messages[0].Role == AiRole.Assistant
                               && _messages[0].ToolCalls.Count > 0)))
                    _messages.RemoveAt(0);
            }
        }
    }

    /// <summary>把用户填写的基址补全为 chat/completions 完整地址。</summary>
    public static string ResolveEndpoint(string baseUrl)
    {
        var value = (baseUrl ?? "").Trim().TrimEnd('/');
        if (value.Length == 0)
            value = "https://api.deepseek.com";
        if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return value;
        return value + "/chat/completions";
    }

    /// <summary>设置页用于测试连通性：发送一条极短的请求。</summary>
    public static async Task<(bool Ok, string Message)> TestConnectionAsync(
        string baseUrl,
        string apiKey,
        string model,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "API Key 为空。");

        try
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            var payload = new JsonObject
            {
                ["model"] = string.IsNullOrWhiteSpace(model) ? "deepseek-chat" : model.Trim(),
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = "ping"
                    }
                },
                ["max_tokens"] = 8,
                ["stream"] = false
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint(baseUrl))
            {
                Content = new StringContent(
                    payload.ToJsonString(),
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return (false, $"接口返回 {(int)response.StatusCode}：{Truncate(body, 300)}");

            return (true, "连接成功，模型可以正常响应。");
        }
        catch (OperationCanceledException)
        {
            return (false, "请求超时或被取消。");
        }
        catch (Exception ex)
        {
            return (false, "连接失败：" + ex.Message);
        }
    }

    private static string DescribeTool(string name)
    {
        return name switch
        {
            "launch_game" => "启动游戏",
            "select_instance" => "切换版本",
            "list_instances" => "读取版本列表",
            "list_mods" => "读取 Mod 列表",
            "set_mod_enabled" => "调整 Mod 状态",
            "delete_mod" => "删除 Mod",
            "open_page" => "打开页面",
            "search_resource" => "打开资源下载",
            "get_game_log" => "读取游戏日志",
            "get_last_crash" => "读取崩溃报告",
            _ => name
        };
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value ?? "";
        return value[..max] + "\n…（内容过长已截断）";
    }
}
