using MateOS.Core.Entities;
using MateOS.Core.Interfaces;

namespace MateOS.Core.Services;

public class MessageService : IMessageService
{
    private readonly Dictionary<string, Message> _messages = new();

    public async Task<Message> CreateMessageAsync(string channelId, string userId, string content, MessageType type = MessageType.Text)
    {
        var message = new Message { ChannelId = channelId, UserId = userId, Content = content, Type = type };
        _messages[message.Id] = message;
        return await Task.FromResult(message);
    }

    public async Task<Message> GetMessageAsync(string messageId)
    {
        _messages.TryGetValue(messageId, out var message);
        return await Task.FromResult(message);
    }

    public async Task<List<Message>> GetChannelMessagesAsync(string channelId, int limit = 50)
    {
        var messages = _messages.Values
            .Where(m => m.ChannelId == channelId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .ToList();
        return await Task.FromResult(messages);
    }

    public async Task<List<Message>> GetUserMessagesAsync(string userId, int limit = 50)
    {
        var messages = _messages.Values
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .ToList();
        return await Task.FromResult(messages);
    }

    public async Task UpdateMessageAsync(Message message)
    {
        message.UpdatedAt = DateTime.UtcNow;
        _messages[message.Id] = message;
        await Task.CompletedTask;
    }

    public async Task DeleteMessageAsync(string messageId)
    {
        _messages.Remove(messageId);
        await Task.CompletedTask;
    }
}
