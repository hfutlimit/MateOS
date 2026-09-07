using MateOS.Core.Entities;

namespace MateOS.Core.Interfaces;

public interface IMessageService
{
    Task<Message> CreateMessageAsync(string channelId, string userId, string content, MessageType type = MessageType.Text);
    Task<Message> GetMessageAsync(string messageId);
    Task<List<Message>> GetChannelMessagesAsync(string channelId, int limit = 50);
    Task<List<Message>> GetUserMessagesAsync(string userId, int limit = 50);
    Task UpdateMessageAsync(Message message);
    Task DeleteMessageAsync(string messageId);
}
