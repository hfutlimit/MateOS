namespace MateOS.Core.DTOs;

public class MemoryDto
{
    public string Id { get; set; }
    public string UserId { get; set; }
    public string ChannelId { get; set; }
    public string Content { get; set; }
    public string Type { get; set; }
    public Dictionary<string, object> Tags { get; set; }
    public int ImportanceScore { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CreateMemoryDto
{
    public string UserId { get; set; }
    public string ChannelId { get; set; }
    public string Content { get; set; }
    public string Type { get; set; }
    public int ImportanceScore { get; set; }
}
