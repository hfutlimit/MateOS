namespace MateOS.Core.DTOs;

public class ChannelDto
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public List<string> MemberIds { get; set; }
    public bool IsPrivate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CreateChannelDto
{
    public string Name { get; set; }
    public string Description { get; set; }
    public bool IsPrivate { get; set; }
}
