using Microsoft.AspNetCore.Mvc;
using MateOS.Core.DTOs;
using MateOS.Core.Entities;
using MateOS.Core.Interfaces;

namespace MateOS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MemoriesController : ControllerBase
{
    private readonly IMemoryService _memoryService;

    public MemoriesController(IMemoryService memoryService)
    {
        _memoryService = memoryService;
    }

    [HttpPost]
    public async Task<ActionResult<MemoryDto>> CreateMemory(CreateMemoryDto dto)
    {
        if (!Enum.TryParse<MemoryType>(dto.Type, true, out var memoryType))
            return BadRequest("Invalid memory type");

        var memory = await _memoryService.CreateMemoryAsync(dto.UserId, dto.ChannelId, dto.Content, memoryType);
        return CreatedAtAction(nameof(GetMemory), new { id = memory.Id }, MapToDto(memory));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<MemoryDto>> GetMemory(string id)
    {
        var memory = await _memoryService.GetMemoryAsync(id);
        if (memory == null)
            return NotFound();

        return Ok(MapToDto(memory));
    }

    [HttpGet("user/{userId}")]
    public async Task<ActionResult<List<MemoryDto>>> GetUserMemories(string userId)
    {
        var memories = await _memoryService.GetUserMemoriesAsync(userId);
        return Ok(memories.Select(MapToDto).ToList());
    }

    [HttpGet("channel/{channelId}")]
    public async Task<ActionResult<List<MemoryDto>>> GetChannelMemories(string channelId)
    {
        var memories = await _memoryService.GetChannelMemoriesAsync(channelId);
        return Ok(memories.Select(MapToDto).ToList());
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<MemoryDto>>> SearchMemories([FromQuery] string query, [FromQuery] string userId = null)
    {
        var memories = await _memoryService.SearchMemoriesAsync(query, userId);
        return Ok(memories.Select(MapToDto).ToList());
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateMemory(string id, MemoryDto dto)
    {
        if (id != dto.Id)
            return BadRequest();

        if (!Enum.TryParse<MemoryType>(dto.Type, true, out var memoryType))
            return BadRequest("Invalid memory type");

        var memory = new Memory { Id = id, UserId = dto.UserId, ChannelId = dto.ChannelId, Content = dto.Content, Type = memoryType, Tags = dto.Tags, ImportanceScore = dto.ImportanceScore, CreatedAt = dto.CreatedAt, UpdatedAt = dto.UpdatedAt };
        await _memoryService.UpdateMemoryAsync(memory);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteMemory(string id)
    {
        await _memoryService.DeleteMemoryAsync(id);
        return NoContent();
    }

    private MemoryDto MapToDto(Memory memory) => new()
    {
        Id = memory.Id,
        UserId = memory.UserId,
        ChannelId = memory.ChannelId,
        Content = memory.Content,
        Type = memory.Type.ToString(),
        Tags = memory.Tags,
        ImportanceScore = memory.ImportanceScore,
        CreatedAt = memory.CreatedAt,
        UpdatedAt = memory.UpdatedAt
    };
}
