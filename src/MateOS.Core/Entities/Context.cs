namespace MateOS.Core.Entities;

public class Context
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ChannelId { get; set; }
    public List<string> ParticipantIds { get; set; } = new();
    public List<string> RelevantMemoryIds { get; set; } = new();
    public Dictionary<string, object> SharedState { get; set; } = new();
    public string Topic { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
