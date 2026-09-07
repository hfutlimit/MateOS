using Microsoft.AspNetCore.Mvc;
using MateOS.Core.DTOs;
using MateOS.Core.Entities;
using MateOS.Core.Interfaces;

namespace MateOS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChannelsController : ControllerBase
{
    private readonly IChannelService _channelService;

    public ChannelsController(IChannelService channelService)
    {
        _channelService = channelService;
    }

    [HttpPost]
    public async Task<ActionResult<ChannelDto>> CreateChannel(CreateChannelDto dto)
    {
        var channel = await _channelService.CreateChannelAsync(dto.Name, dto.Description, dto.IsPrivate);
        return CreatedAtAction(nameof(GetChannel), new { id = channel.Id }, MapToDto(channel));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ChannelDto>> GetChannel(string id)
    {
        var channel = await _channelService.GetChannelAsync(id);
        if (channel == null)
            return NotFound();

        return Ok(MapToDto(channel));
    }

    [HttpGet]
    public async Task<ActionResult<List<ChannelDto>>> GetChannels()
    {
        var channels = await _channelService.GetChannelsAsync();
        return Ok(channels.Select(MapToDto).ToList());
    }

    [HttpPost("{id}/members/{userId}")]
    public async Task<IActionResult> AddMember(string id, string userId)
    {
        var result = await _channelService.AddMemberAsync(id, userId);
        return result ? Ok() : NotFound();
    }

    [HttpDelete("{id}/members/{userId}")]
    public async Task<IActionResult> RemoveMember(string id, string userId)
    {
        var result = await _channelService.RemoveMemberAsync(id, userId);
        return result ? Ok() : NotFound();
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateChannel(string id, ChannelDto dto)
    {
        if (id != dto.Id)
            return BadRequest();

        var channel = new Channel { Id = id, Name = dto.Name, Description = dto.Description, MemberIds = dto.MemberIds, IsPrivate = dto.IsPrivate, CreatedAt = dto.CreatedAt, UpdatedAt = dto.UpdatedAt };
        await _channelService.UpdateChannelAsync(channel);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteChannel(string id)
    {
        await _channelService.DeleteChannelAsync(id);
        return NoContent();
    }

    private ChannelDto MapToDto(Channel channel) => new()
    {
        Id = channel.Id,
        Name = channel.Name,
        Description = channel.Description,
        MemberIds = channel.MemberIds,
        IsPrivate = channel.IsPrivate,
        CreatedAt = channel.CreatedAt,
        UpdatedAt = channel.UpdatedAt
    };
}
