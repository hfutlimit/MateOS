namespace MateOS.Core.Entities;

public class Memory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; }
    public string ChannelId { get; set; }
    public string Content { get; set; }
    public MemoryType Type { get; set; }
    public Dictionary<string, object> Tags { get; set; } = new();
    public int ImportanceScore { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum MemoryType
{
    Conversation,
    Fact,
    Preference,
    Context,
    Decision
}
