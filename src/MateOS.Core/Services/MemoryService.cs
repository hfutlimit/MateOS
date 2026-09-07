using MateOS.Core.Entities;
using MateOS.Core.Interfaces;

namespace MateOS.Core.Services;

public class MemoryService : IMemoryService
{
    private readonly Dictionary<string, Memory> _memories = new();

    public async Task<Memory> CreateMemoryAsync(string userId, string channelId, string content, MemoryType type)
    {
        var memory = new Memory { UserId = userId, ChannelId = channelId, Content = content, Type = type };
        _memories[memory.Id] = memory;
        return await Task.FromResult(memory);
    }

    public async Task<Memory> GetMemoryAsync(string memoryId)
    {
        _memories.TryGetValue(memoryId, out var memory);
        return await Task.FromResult(memory);
    }

    public async Task<List<Memory>> GetUserMemoriesAsync(string userId)
    {
        var memories = _memories.Values
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.ImportanceScore)
            .ToList();
        return await Task.FromResult(memories);
    }

    public async Task<List<Memory>> GetChannelMemoriesAsync(string channelId)
    {
        var memories = _memories.Values
            .Where(m => m.ChannelId == channelId)
            .OrderByDescending(m => m.ImportanceScore)
            .ToList();
        return await Task.FromResult(memories);
    }

    public async Task<List<Memory>> SearchMemoriesAsync(string query, string userId = null)
    {
        var memories = _memories.Values
            .Where(m => (string.IsNullOrEmpty(userId) || m.UserId == userId) &&
                        m.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.ImportanceScore)
            .ToList();
        return await Task.FromResult(memories);
    }

    public async Task UpdateMemoryAsync(Memory memory)
    {
        memory.UpdatedAt = DateTime.UtcNow;
        _memories[memory.Id] = memory;
        await Task.CompletedTask;
    }

    public async Task DeleteMemoryAsync(string memoryId)
    {
        _memories.Remove(memoryId);
        await Task.CompletedTask;
    }
}
