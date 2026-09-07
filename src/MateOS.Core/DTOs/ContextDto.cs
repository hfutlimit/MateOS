namespace MateOS.Core.DTOs;

public class ContextDto
{
    public string Id { get; set; }
    public string ChannelId { get; set; }
    public List<string> ParticipantIds { get; set; }
    public List<string> RelevantMemoryIds { get; set; }
    public Dictionary<string, object> SharedState { get; set; }
    public string Topic { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CreateContextDto
{
    public string ChannelId { get; set; }
    public List<string> ParticipantIds { get; set; }
    public string Topic { get; set; }
}
