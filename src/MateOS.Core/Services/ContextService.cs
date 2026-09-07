using MateOS.Core.Entities;
using MateOS.Core.Interfaces;

namespace MateOS.Core.Services;

public class ContextService : IContextService
{
    private readonly Dictionary<string, Context> _contexts = new();

    public async Task<Context> CreateContextAsync(string channelId, List<string> participantIds, string topic)
    {
        var context = new Context { ChannelId = channelId, ParticipantIds = participantIds, Topic = topic };
        _contexts[context.Id] = context;
        return await Task.FromResult(context);
    }

    public async Task<Context> GetContextAsync(string contextId)
    {
        _contexts.TryGetValue(contextId, out var context);
        return await Task.FromResult(context);
    }

    public async Task<Context> GetChannelContextAsync(string channelId)
    {
        var context = _contexts.Values.FirstOrDefault(c => c.ChannelId == channelId);
        return await Task.FromResult(context);
    }

    public async Task AddMemoryToContextAsync(string contextId, string memoryId)
    {
        if (_contexts.TryGetValue(contextId, out var context))
        {
            if (!context.RelevantMemoryIds.Contains(memoryId))
            {
                context.RelevantMemoryIds.Add(memoryId);
                context.UpdatedAt = DateTime.UtcNow;
            }
        }
        await Task.CompletedTask;
    }

    public async Task RemoveMemoryFromContextAsync(string contextId, string memoryId)
    {
        if (_contexts.TryGetValue(contextId, out var context))
        {
            context.RelevantMemoryIds.Remove(memoryId);
            context.UpdatedAt = DateTime.UtcNow;
        }
        await Task.CompletedTask;
    }

    public async Task UpdateSharedStateAsync(string contextId, Dictionary<string, object> state)
    {
        if (_contexts.TryGetValue(contextId, out var context))
        {
            context.SharedState = state;
            context.UpdatedAt = DateTime.UtcNow;
        }
        await Task.CompletedTask;
    }

    public async Task UpdateContextAsync(Context context)
    {
        context.UpdatedAt = DateTime.UtcNow;
        _contexts[context.Id] = context;
        await Task.CompletedTask;
    }

    public async Task DeleteContextAsync(string contextId)
    {
        _contexts.Remove(contextId);
        await Task.CompletedTask;
    }
}
