using Microsoft.AspNetCore.Mvc;
using MateOS.Core.DTOs;
using MateOS.Core.Entities;
using MateOS.Core.Interfaces;

namespace MateOS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ContextsController : ControllerBase
{
    private readonly IContextService _contextService;

    public ContextsController(IContextService contextService)
    {
        _contextService = contextService;
    }

    [HttpPost]
    public async Task<ActionResult<ContextDto>> CreateContext(CreateContextDto dto)
    {
        var context = await _contextService.CreateContextAsync(dto.ChannelId, dto.ParticipantIds, dto.Topic);
        return CreatedAtAction(nameof(GetContext), new { id = context.Id }, MapToDto(context));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ContextDto>> GetContext(string id)
    {
        var context = await _contextService.GetContextAsync(id);
        if (context == null)
            return NotFound();

        return Ok(MapToDto(context));
    }

    [HttpGet("channel/{channelId}")]
    public async Task<ActionResult<ContextDto>> GetChannelContext(string channelId)
    {
        var context = await _contextService.GetChannelContextAsync(channelId);
        if (context == null)
            return NotFound();

        return Ok(MapToDto(context));
    }

    [HttpPost("{id}/memories/{memoryId}")]
    public async Task<IActionResult> AddMemoryToContext(string id, string memoryId)
    {
        await _contextService.AddMemoryToContextAsync(id, memoryId);
        return Ok();
    }

    [HttpDelete("{id}/memories/{memoryId}")]
    public async Task<IActionResult> RemoveMemoryFromContext(string id, string memoryId)
    {
        await _contextService.RemoveMemoryFromContextAsync(id, memoryId);
        return Ok();
    }

    [HttpPut("{id}/state")]
    public async Task<IActionResult> UpdateSharedState(string id, [FromBody] Dictionary<string, object> state)
    {
        await _contextService.UpdateSharedStateAsync(id, state);
        return NoContent();
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateContext(string id, ContextDto dto)
    {
        if (id != dto.Id)
            return BadRequest();

        var context = new Context { Id = id, ChannelId = dto.ChannelId, ParticipantIds = dto.ParticipantIds, RelevantMemoryIds = dto.RelevantMemoryIds, SharedState = dto.SharedState, Topic = dto.Topic, CreatedAt = dto.CreatedAt, UpdatedAt = dto.UpdatedAt };
        await _contextService.UpdateContextAsync(context);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteContext(string id)
    {
        await _contextService.DeleteContextAsync(id);
        return NoContent();
    }

    private ContextDto MapToDto(Context context) => new()
    {
        Id = context.Id,
        ChannelId = context.ChannelId,
        ParticipantIds = context.ParticipantIds,
        RelevantMemoryIds = context.RelevantMemoryIds,
        SharedState = context.SharedState,
        Topic = context.Topic,
        CreatedAt = context.CreatedAt,
        UpdatedAt = context.UpdatedAt
    };
}
