using MateOS.Core.Entities;

namespace MateOS.Core.Interfaces;

public interface IMemoryService
{
    Task<Memory> CreateMemoryAsync(string userId, string channelId, string content, MemoryType type);
    Task<Memory> GetMemoryAsync(string memoryId);
    Task<List<Memory>> GetUserMemoriesAsync(string userId);
    Task<List<Memory>> GetChannelMemoriesAsync(string channelId);
    Task<List<Memory>> SearchMemoriesAsync(string query, string userId = null);
    Task UpdateMemoryAsync(Memory memory);
    Task DeleteMemoryAsync(string memoryId);
}
