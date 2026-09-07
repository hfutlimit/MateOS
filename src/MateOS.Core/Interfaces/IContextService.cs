using MateOS.Core.Entities;

namespace MateOS.Core.Interfaces;

public interface IContextService
{
    Task<Context> CreateContextAsync(string channelId, List<string> participantIds, string topic);
    Task<Context> GetContextAsync(string contextId);
    Task<Context> GetChannelContextAsync(string channelId);
    Task AddMemoryToContextAsync(string contextId, string memoryId);
    Task RemoveMemoryFromContextAsync(string contextId, string memoryId);
    Task UpdateSharedStateAsync(string contextId, Dictionary<string, object> state);
    Task UpdateContextAsync(Context context);
    Task DeleteContextAsync(string contextId);
}
