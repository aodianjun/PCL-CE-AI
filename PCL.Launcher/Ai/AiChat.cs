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

/// <summary>随用户消息发送的图片或文本附件。</summary>
public sealed class AiAttachment
{
    public string Name { get; init; } = "";

    public bool IsImage { get; init; }

    /// <summary>图片的 data URL（data:image/png;base64,...）。</summary>
    public string? DataUrl { get; init; }

    /// <summary>文本文件的正文。</summary>
    public string? TextContent { get; init; }

    /// <summary>附件已复制到本地工作区的路径；大文件或二进制文件走这条路，由 AI 用文件工具读取。</summary>
    public string? LocalPath { get; init; }
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

    /// <summary>是否为排队等待发送的用户消息（尚未进入模型上下文）。</summary>
    public bool IsQueued { get; set; }

    /// <summary>工具调用结果消息对应的调用 ID。</summary>
    public string? ToolCallId { get; init; }

    /// <summary>工具名称。</summary>
    public string? ToolName { get; init; }

    /// <summary>工具调用参数（原始 JSON）。</summary>
    public string? ToolArguments { get; init; }

    /// <summary>模型的思考过程（reasoning_content）。</summary>
    public string Reasoning { get; set; } = "";

    /// <summary>用户消息携带的图片/文件附件。</summary>
    public List<AiAttachment> Attachments { get; } = [];

    /// <summary>助手消息中请求的工具调用。</summary>
    public List<AiToolCall> ToolCalls { get; } = [];

    public DateTime CreatedAt { get; init; } = DateTime.Now;
}

/// <summary>当前会话的上下文用量快照。</summary>
public sealed record AiContextUsage(
    int MessageCount,
    int Characters,
    int EstimatedTokens,
    int Limit,
    double Percent,
    bool HasSummary);

/// <summary>
/// DeepSeek harness：兼容 OpenAI Chat Completions 的会话服务。
/// 负责流式对话、工具调用循环、历史裁剪，以及把崩溃报告自动交给模型分析。
/// 所有事件都在后台线程触发，界面层需要自行切回 UI 线程。
/// </summary>
public sealed class AiChatService
{
    private const int DefaultMaxToolRounds = 100;
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

    private readonly Queue<AiChatMessage> _pendingSends = new();
    private readonly Lock _queueLock = new();
    private int _pumping;

    /// <summary>排队等待发送的消息数量。</summary>
    public int QueuedCount
    {
        get
        {
            lock (_queueLock)
                return _pendingSends.Count;
        }
    }

    /// <summary>排队消息数量发生变化。</summary>
    public event Action? QueueChanged;

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

    /// <summary>上下文用量发生变化。</summary>
    public event Action? ContextUsageChanged;

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

    /// <summary>
    /// AI 工作目录。用户配置了就用配置值，否则使用启动器数据目录下的 AiWorkspace。
    /// 大文件附件会复制到这里，保证 AI 的文件工具一定能读到。
    /// </summary>
    public static string GetWorkspaceDirectory()
    {
        try
        {
            var configured = Config.Ai.WorkDirectory;
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured.Trim());
        }
        catch (Exception)
        {
            // 配置异常时退回默认目录。
        }

        return Path.Combine(Paths.Data, "AiWorkspace");
    }

    /// <summary>计算当前会话的上下文用量（字符数 + 粗略 token 估算）。</summary>
    public AiContextUsage GetContextUsage()
    {
        List<AiChatMessage> snapshot;
        int chars = 0;
        int ascii = 0;
        int nonAscii = 0;
        bool hasSummary;
        lock (_messageLock)
        {
            snapshot = _messages.Where(message => !message.IsQueued).ToList();
            hasSummary = !string.IsNullOrWhiteSpace(_current.Summary);
        }

        foreach (var message in snapshot)
        {
            var content = message.Content ?? "";
            chars += content.Length;
            foreach (var ch in content)
                if (ch < 128)
                    ascii++;
                else
                    nonAscii++;
        }

        int limit;
        try
        {
            limit = Math.Max(4000, Config.Ai.ContextLimit);
        }
        catch (Exception)
        {
            limit = 64000;
        }

        // 粗略估算：英文约 4 字符 1 token，中文约 1 字符 1 token。
        var tokens = (int)Math.Ceiling(ascii / 4.0 + nonAscii);
        var percent = limit > 0 ? Math.Min(100d, tokens * 100d / limit) : 0d;
        return new AiContextUsage(snapshot.Count, chars, tokens, limit, percent, hasSummary);
    }

    /// <summary>新建会话并切换过去。</summary>
    public void NewConversation()
    {
        if (IsBusy)
        {
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = Lang.Text("Launch.Ai.Busy.Wait"),
                IsError = true
            });
            return;
        }
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
        ContextUsageChanged?.Invoke();
    }

    /// <summary>切换到指定会话。</summary>
    public void SelectConversation(string id)
    {
        if (IsBusy)
        {
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = Lang.Text("Launch.Ai.Busy.Wait"),
                IsError = true
            });
            return;
        }

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
        ContextUsageChanged?.Invoke();
    }

    /// <summary>删除会话；若删除的是当前会话，则切换到最近的其它会话。</summary>
    public void DeleteConversation(string id)
    {
        if (IsBusy)
        {
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = Lang.Text("Launch.Ai.Busy.Wait"),
                IsError = true
            });
            return;
        }

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
        ContextUsageChanged?.Invoke();
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
            var compressed = await Task.Run(
                async () => await CompressCoreAsync(cts.Token, 2),
                cts.Token);
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
            LogWrapper.Warn("Ai", "压缩上下文失败：" + ex.Message);
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
            ContextUsageChanged?.Invoke();
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
                                  && !message.IsQueued
                                  && !string.IsNullOrWhiteSpace(message.Content)
                                  && message.Role is AiRole.User or AiRole.Assistant
                                  && message.ToolCalls.Count == 0)
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
        try
        {
            enabled = Config.Ai.AutoCompress;
        }
        catch (Exception)
        {
            return;
        }

        if (!enabled)
            return;

        var usage = GetContextUsage();
        var tokenThreshold = Math.Max(4000, (int)(usage.Limit * 0.95));
        if (usage.EstimatedTokens < tokenThreshold)
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
        if (IsBusy)
        {
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = Lang.Text("Launch.Ai.Busy.Wait"),
                IsError = true
            });
            return;
        }
        lock (_messageLock)
            _messages.Clear();
        _current.Title = "";
        _current.Summary = null;
        _current.UpdatedAt = DateTime.Now;
        LastError = null;
        SaveConversations();
        ConversationsChanged?.Invoke();
        HistoryCleared?.Invoke();
        ContextUsageChanged?.Invoke();
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
        return SendCoreAsync(text, null, false);
    }

    /// <summary>发送一条带图片/文件附件的用户消息。</summary>
    public Task SendAsync(string text, IReadOnlyList<AiAttachment>? attachments)
    {
        return SendCoreAsync(text, attachments, false);
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

        var note = new AiChatMessage
        {
            Role = AiRole.System,
            Content = "【游戏崩溃报告】\n" + report,
            IsHidden = true
        };
        AddMessage(note);

        _ = SendCoreAsync(
            "游戏刚刚崩溃了。请结合自动提交的崩溃报告，用简洁的中文说明崩溃原因，并给出可以直接执行的修复步骤。",
            null,
            true);
    }

    private async Task SendCoreAsync(
        string text,
        IReadOnlyList<AiAttachment>? attachments,
        bool auto)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0 && (attachments is null || attachments.Count == 0))
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

        var message = new AiChatMessage
        {
            Role = AiRole.User,
            Content = text,
            IsQueued = IsBusy
        };
        if (attachments is { Count: > 0 })
            message.Attachments.AddRange(attachments);

        lock (_queueLock)
            _pendingSends.Enqueue(message);

        AddMessage(message);
        UpdateConversationTitle(
            text.Length > 0
                ? text
                : message.Attachments.FirstOrDefault()?.Name ?? "");
        SaveConversations();
        QueueChanged?.Invoke();

        await PumpQueueAsync();
    }

    /// <summary>
    /// 单消费者发送队列：同一时间只跑一轮回复，用户在当前回复期间输入的消息
    /// 会按顺序排队，当前回复（含工具调用）结束后自动继续发送，不会打断。
    /// </summary>
    private async Task PumpQueueAsync()
    {
        if (Interlocked.CompareExchange(ref _pumping, 1, 0) != 0)
            return;

        try
        {
            await _gate.WaitAsync();
            IsBusy = true;
            BusyChanged?.Invoke(true);
            try
            {
                while (true)
                {
                    AiChatMessage? next;
                    lock (_queueLock)
                        next = _pendingSends.Count > 0 ? _pendingSends.Dequeue() : null;
                    if (next is null)
                        break;

                    if (next.IsQueued)
                    {
                        next.IsQueued = false;
                        MessageUpdated?.Invoke(next);
                        QueueChanged?.Invoke();
                    }

                    await RunTurnAsync(next);
                }
            }
            finally
            {
                IsBusy = false;
                _cts = null;
                StatusChanged?.Invoke(null);
                BusyChanged?.Invoke(false);
                SaveConversations();
                ContextUsageChanged?.Invoke();
                _gate.Release();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _pumping, 0);
            // 兜底：收尾瞬间入队的消息可能没被循环取到，这里再拉起一次。
            if (QueuedCount > 0)
                _ = PumpQueueAsync();
        }
    }

    /// <summary>执行一轮完整的模型请求与工具循环。</summary>
    private async Task RunTurnAsync(AiChatMessage userMessage)
    {
        LastError = null;
        using var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            await AutoCompressIfNeededAsync(cts.Token);
            TrimHistory();

            // 整个工具循环放到线程池，避免文件扫描、日志读取等操作阻塞 UI 线程。
            await Task.Run(async () => await RunToolLoopAsync(cts.Token), cts.Token);
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
            LogWrapper.Warn("Ai", "AI 请求失败：" + ex.Message);
            AddMessage(new AiChatMessage
            {
                Role = AiRole.Assistant,
                Content = "AI 请求失败：" + ex.Message,
                IsError = true
            });
        }
        finally
        {
            SaveConversations();
            ContextUsageChanged?.Invoke();
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
        var maxRounds = DefaultMaxToolRounds;
        try
        {
            maxRounds = Math.Clamp(Config.Ai.MaxToolRounds, 8, 500);
        }
        catch (Exception)
        {
            // 配置不可用时使用默认值。
        }

        string? lastCallSignature = null;
        string? lastToolResult = null;
        var repeatCount = 0;

        for (var round = 0; round < maxRounds; round++)
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

            if (string.IsNullOrWhiteSpace(assistant.Content))
            {
                assistant.Content = Lang.Text("Launch.Ai.ToolCalling");
                NotifyUpdated(assistant);
            }

            foreach (var call in assistant.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                StatusChanged?.Invoke($"正在执行 {DescribeTool(call.Name)}…");
                string result;
                var toolTimeout = AiTools.GetToolTimeout(call);
                using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                toolCts.CancelAfter(toolTimeout);
                AiTools.ProgressReporter = status => StatusChanged?.Invoke(status);
                try
                {
                    var startedAt = DateTime.UtcNow;
                    var execution = AiTools.ExecuteAsync(call, toolCts.Token);
                    while (!execution.IsCompleted)
                    {
                        var remaining = toolTimeout - (DateTime.UtcNow - startedAt);
                        if (remaining <= TimeSpan.Zero)
                            throw new TimeoutException();

                        var slice = remaining < TimeSpan.FromSeconds(3)
                            ? remaining
                            : TimeSpan.FromSeconds(3);
                        var finished = await Task.WhenAny(
                            execution,
                            Task.Delay(slice, toolCts.Token));
                        if (ReferenceEquals(finished, execution))
                            break;

                        var elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
                        StatusChanged?.Invoke(
                            $"正在执行 {DescribeTool(call.Name)}… {elapsed:0} 秒");
                    }

                    result = await execution;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 工具自身的超时 CTS 到点，不是用户取消：交给模型自行换方案。
                    result = $"工具执行超时（{toolTimeout.TotalSeconds:0} 秒），已跳过。";
                }
                catch (TimeoutException)
                {
                    result = $"工具执行超时（{toolTimeout.TotalSeconds:0} 秒），已跳过。";
                }
                finally
                {
                    AiTools.ProgressReporter = null;
                }
                AddMessage(new AiChatMessage
                {
                    Role = AiRole.Tool,
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    ToolArguments = call.Arguments,
                    Content = result
                });

                var signature = call.Name + "\n" + call.Arguments;
                if (signature == lastCallSignature
                    && string.Equals(result, lastToolResult, StringComparison.Ordinal))
                {
                    repeatCount++;
                    if (repeatCount >= 4)
                    {
                        AddMessage(new AiChatMessage
                        {
                            Role = AiRole.Assistant,
                            Content = "检测到连续重复相同的工具调用且结果没有变化，已停止本轮，避免死循环。",
                            IsError = true
                        });
                        return;
                    }
                }
                else
                {
                    lastCallSignature = signature;
                    lastToolResult = result;
                    repeatCount = 1;
                }
            }
        }

        AddMessage(new AiChatMessage
        {
            Role = AiRole.Assistant,
            Content = $"工具调用次数达到上限（{maxRounds} 次），已暂停本轮。回复“继续”可以接着处理。",
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

        using var firstByteCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        firstByteCts.CancelAfter(TimeSpan.FromSeconds(60));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                firstByteCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("模型 60 秒内没有响应，已中断本次请求。");
        }

        using var responseScope = response;

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
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(TimeSpan.FromSeconds(90));
        var accumulators = new Dictionary<int, AiToolCall>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line;
            try
            {
                line = await reader.ReadLineAsync(idleCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("模型 90 秒没有返回新数据，已中断本次回复。");
            }

            idleCts.CancelAfter(TimeSpan.FromSeconds(90));
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

            var reasoning = delta["reasoning_content"]?.ToString()
                            ?? delta["reasoning"]?.ToString();
            if (!string.IsNullOrEmpty(reasoning))
            {
                assistant.Reasoning += reasoning;
                NotifyUpdated(assistant);
            }

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

        var history = Messages.ToList();
        HashSet<string>? pendingToolIds = null;

        for (var index = 0; index < history.Count; index++)
        {
            var message = history[index];
            if (message.IsError)
                continue;
            if (message.IsQueued)
                continue;
            if (message.Role == AiRole.System && !message.IsHidden)
                continue;

            if (message.Role == AiRole.Tool)
            {
                // 只保留紧跟在对应 tool_calls 后面、且 id 能对上的工具结果，
                // 否则接口会以 400 拒绝整个请求。
                if (pendingToolIds is null
                    || !pendingToolIds.Remove(message.ToolCallId ?? ""))
                    continue;

                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = message.ToolCallId ?? "",
                    ["content"] = message.Content
                });
                continue;
            }

            pendingToolIds = null;

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
                    _ => "system"
                }
            };

            if (message.Role == AiRole.Assistant && message.ToolCalls.Count > 0)
            {
                if (!HasCompleteToolResults(history, index))
                {
                    if (string.IsNullOrWhiteSpace(message.Content))
                        continue;
                    item["content"] = message.Content;
                    messages.Add(item);
                    continue;
                }

                pendingToolIds = message.ToolCalls
                    .Select(call => call.Id)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToHashSet();
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
            else if (message.Role == AiRole.User && message.Attachments.Count > 0)
            {
                var parts = new JsonArray();
                var textParts = new System.Text.StringBuilder(message.Content ?? "");
                foreach (var attachment in message.Attachments)
                {
                    if (!string.IsNullOrWhiteSpace(attachment.LocalPath))
                    {
                        textParts.Append("\n\n【附件：").Append(attachment.Name)
                            .Append("，已复制到工作区】\n路径：").Append(attachment.LocalPath)
                            .Append("\n需要内容时用 read_file / search_files 读取；文件较大时用 max_chars 分段读取。");
                        continue;
                    }

                    if (attachment.IsImage || string.IsNullOrWhiteSpace(attachment.TextContent))
                        continue;
                    textParts.Append("\n\n【附件：").Append(attachment.Name).Append("】\n")
                        .Append(attachment.TextContent);
                }

                var hasImages = message.Attachments.Any(a => a.IsImage && !string.IsNullOrWhiteSpace(a.DataUrl));
                if (textParts.Length > 0 || !hasImages)
                    parts.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = textParts.ToString()
                    });

                foreach (var attachment in message.Attachments)
                {
                    if (!attachment.IsImage || string.IsNullOrWhiteSpace(attachment.DataUrl))
                        continue;
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = attachment.DataUrl
                        }
                    });
                }

                item["content"] = parts;
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

        try
        {
            var maxOutput = Config.Ai.MaxOutputTokens;
            if (maxOutput > 0)
                payload["max_tokens"] = maxOutput;
        }
        catch (Exception)
        {
            // 配置未就绪时忽略。
        }

        if (Config.Ai.EnableTools)
            payload["tools"] = AiTools.BuildDefinitions();

        return payload;
    }

    /// <summary>检查该助手消息的工具调用是否都有紧跟着的结果消息。</summary>
    private static bool HasCompleteToolResults(
        IReadOnlyList<AiChatMessage> history,
        int index)
    {
        var ids = history[index].ToolCalls
            .Select(call => call.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet();
        if (ids.Count == 0)
            return true;

        for (var i = index + 1; i < history.Count; i++)
        {
            if (history[i].IsQueued)
                continue;
            if (history[i].Role != AiRole.Tool)
                break;
            ids.Remove(history[i].ToolCallId ?? "");
        }
        return ids.Count == 0;
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
        builder.AppendLine("5. 不要主动切换用户正在看的页面。只有用户明确说“打开/跳转某个页面”时，才把 open_page、search_resource、install_version_note 的 navigate 参数设为 true；其余情况保持 false，只告诉用户页面位置。");
        builder.AppendLine("6. 用户只说“启动”“开游戏”时，直接调用 launch_game。");
        builder.AppendLine("7. 需要看文件时先调用 list_files，再 read_file；只能访问工作目录或允许访问的文件夹，路径越界会被拒绝。");
        builder.AppendLine("8. download_minecraft 与 launch_game 默认 wait=true，会等到安装或启动完成后再返回结果；拿到结果后直接继续下一步，不需要轮询 get_install_status。");
        builder.AppendLine("9. 用户消息里带“已复制到工作区”的附件时，直接用 read_file（大文件配合 max_chars 分段）或 search_files 读取路径，不要要求用户重新上传。");
        builder.AppendLine("10. 你可以在工作目录、启动器数据目录（PCL/）或允许访问的文件夹内用 write_file、replace_in_file、create_directory、move_path、delete_path 创建和修改文件。执行覆盖、移动或删除前先确认目标路径；不要删除或覆盖 conversations.json、config.v1.yml、日志等启动器运行文件，除非用户明确要求。");
        builder.AppendLine("11. 需要最新信息或查资料时用 web_search 搜索，再用 web_fetch 读取网页正文；联网功能可在“设置 → AI 接入”里关闭。");
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
        ContextUsageChanged?.Invoke();
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

    /// <summary>把用户填写的基址补全为 /models 完整地址。</summary>
    public static string ResolveModelsEndpoint(string baseUrl)
    {
        var value = (baseUrl ?? "").Trim().TrimEnd('/');
        if (value.Length == 0)
            value = "https://api.deepseek.com";
        const string completions = "/chat/completions";
        if (value.EndsWith(completions, StringComparison.OrdinalIgnoreCase))
            value = value[..^completions.Length];
        return value + "/models";
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
