namespace MateOS.Core.Entities;

public class Message
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ChannelId { get; set; }
    public string UserId { get; set; }
    public string Content { get; set; }
    public MessageType Type { get; set; } = MessageType.Text;
    public Dictionary<string, object> Metadata { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum MessageType
{
    Text,
    System,
    AgentResponse,
    ContextUpdate
}
