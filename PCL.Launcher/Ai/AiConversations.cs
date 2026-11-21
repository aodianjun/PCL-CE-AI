using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Core.App;
using PCL.Core.Logging;

namespace PCL.Ai;

/// <summary>一次 AI 会话。消息只在内存中保留完整结构，落盘时转换为纯文本历史。</summary>
public sealed class AiConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>会话标题；空字符串表示还未命名（显示为「新对话」）。</summary>
    public string Title { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>最近一次上下文压缩生成的摘要。</summary>
    public string? Summary { get; set; }

    [JsonIgnore]
    public List<AiChatMessage> Messages { get; } = [];
}

/// <summary>会话落盘文件的结构。</summary>
internal sealed class AiConversationStoreDto
{
    public List<AiConversationDto> Conversations { get; set; } = [];

    public string? CurrentId { get; set; }
}

internal sealed class AiConversationDto
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? Summary { get; set; }

    public List<AiMessageDto> Messages { get; set; } = [];
}

internal sealed class AiMessageDto
{
    public string Role { get; set; } = "user";

    public string Content { get; set; } = "";

    public bool IsError { get; set; }

    public string? Reasoning { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>会话列表的本地读写。存放在 PCL 数据目录的 AiChat/conversations.json。</summary>
internal static class AiConversationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string DirectoryPath => Path.Combine(Paths.Data, "AiChat");

    private static string FilePath => Path.Combine(DirectoryPath, "conversations.json");

    public static (List<AiConversation> Conversations, string? CurrentId) Load()
    {
        var result = new List<AiConversation>();
        if (!File.Exists(FilePath))
            return (result, null);

        try
        {
            var json = File.ReadAllText(FilePath);
            var dto = JsonSerializer.Deserialize<AiConversationStoreDto>(json, SerializerOptions);
            if (dto is null)
                return (result, null);

            foreach (var item in dto.Conversations)
            {
                var conversation = new AiConversation
                {
                    Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
                    Title = item.Title ?? "",
                    CreatedAt = item.CreatedAt == default ? DateTime.Now : item.CreatedAt,
                    UpdatedAt = item.UpdatedAt == default ? DateTime.Now : item.UpdatedAt,
                    Summary = item.Summary
                };
                if (!string.IsNullOrWhiteSpace(item.Summary))
                    conversation.Messages.Add(new AiChatMessage
                    {
                        Role = AiRole.System,
                        Content = "【历史对话摘要】" + Environment.NewLine + item.Summary.Trim(),
                        IsHidden = true
                    });

                foreach (var message in item.Messages)
                {
                    if (string.IsNullOrWhiteSpace(message.Content))
                        continue;
                    conversation.Messages.Add(new AiChatMessage
                    {
                        Role = ParseRole(message.Role),
                        Content = message.Content,
                        IsError = message.IsError,
                        Reasoning = message.Reasoning,
                        CreatedAt = message.CreatedAt == default ? DateTime.Now : message.CreatedAt
                    });
                }

                result.Add(conversation);
            }

            return (result, dto.CurrentId);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn("Ai", "读取会话列表失败：" + ex.Message);
            return ([], null);
        }
    }

    public static void Save(IEnumerable<AiConversation> conversations, string? currentId)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var dto = new AiConversationStoreDto
            {
                CurrentId = currentId
            };
            foreach (var conversation in conversations)
            {
                var item = new AiConversationDto
                {
                    Id = conversation.Id,
                    Title = conversation.Title,
                    CreatedAt = conversation.CreatedAt,
                    UpdatedAt = conversation.UpdatedAt,
                    Summary = conversation.Summary
                };
                foreach (var message in conversation.Messages)
                {
                    // 工具消息与隐藏上下文不落盘；助手消息里的 tool_calls 也丢掉，
                    // 否则恢复历史后会出现“有 tool_calls 没有 tool 结果”的非法序列。
                    if (message.IsHidden || message.Role == AiRole.Tool)
                        continue;
                    if (string.IsNullOrWhiteSpace(message.Content))
                        continue;
                    item.Messages.Add(new AiMessageDto
                    {
                        Role = message.Role switch
                        {
                            AiRole.User => "user",
                            AiRole.Assistant => "assistant",
                            _ => "system"
                        },
                        Content = message.Content,
                        IsError = message.IsError,
                        Reasoning = message.Reasoning,
                        CreatedAt = message.CreatedAt
                    });
                }

                dto.Conversations.Add(item);
            }

            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(dto, SerializerOptions));
            File.Move(tempPath, FilePath, true);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn("Ai", "保存会话列表失败：" + ex.Message);
        }
    }

    private static AiRole ParseRole(string? role)
    {
        return role?.ToLowerInvariant() switch
        {
            "assistant" => AiRole.Assistant,
            "system" => AiRole.System,
            _ => AiRole.User
        };
    }
}
