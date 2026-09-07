using Microsoft.AspNetCore.Mvc;
using MateOS.Core.DTOs;
using MateOS.Core.Entities;
using MateOS.Core.Interfaces;

namespace MateOS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MessagesController : ControllerBase
{
    private readonly IMessageService _messageService;

    public MessagesController(IMessageService messageService)
    {
        _messageService = messageService;
    }

    [HttpPost]
    public async Task<ActionResult<MessageDto>> CreateMessage(CreateMessageDto dto)
    {
        if (!Enum.TryParse<MessageType>(dto.Type, true, out var messageType))
            messageType = MessageType.Text;

        var message = await _messageService.CreateMessageAsync(dto.ChannelId, dto.UserId, dto.Content, messageType);
        return CreatedAtAction(nameof(GetMessage), new { id = message.Id }, MapToDto(message));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<MessageDto>> GetMessage(string id)
    {
        var message = await _messageService.GetMessageAsync(id);
        if (message == null)
            return NotFound();

        return Ok(MapToDto(message));
    }

    [HttpGet("channel/{channelId}")]
    public async Task<ActionResult<List<MessageDto>>> GetChannelMessages(string channelId, [FromQuery] int limit = 50)
    {
        var messages = await _messageService.GetChannelMessagesAsync(channelId, limit);
        return Ok(messages.Select(MapToDto).ToList());
    }

    [HttpGet("user/{userId}")]
    public async Task<ActionResult<List<MessageDto>>> GetUserMessages(string userId, [FromQuery] int limit = 50)
    {
        var messages = await _messageService.GetUserMessagesAsync(userId, limit);
        return Ok(messages.Select(MapToDto).ToList());
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateMessage(string id, MessageDto dto)
    {
        if (id != dto.Id)
            return BadRequest();

        if (!Enum.TryParse<MessageType>(dto.Type, true, out var messageType))
            return BadRequest("Invalid message type");

        var message = new Message { Id = id, ChannelId = dto.ChannelId, UserId = dto.UserId, Content = dto.Content, Type = messageType, Metadata = dto.Metadata, CreatedAt = dto.CreatedAt, UpdatedAt = dto.UpdatedAt };
        await _messageService.UpdateMessageAsync(message);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteMessage(string id)
    {
        await _messageService.DeleteMessageAsync(id);
        return NoContent();
    }

    private MessageDto MapToDto(Message message) => new()
    {
        Id = message.Id,
        ChannelId = message.ChannelId,
        UserId = message.UserId,
        Content = message.Content,
        Type = message.Type.ToString(),
        Metadata = message.Metadata,
        CreatedAt = message.CreatedAt,
        UpdatedAt = message.UpdatedAt
    };
}
