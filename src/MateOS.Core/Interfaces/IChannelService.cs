using MateOS.Core.Entities;

namespace MateOS.Core.Interfaces;

public interface IChannelService
{
    Task<Channel> CreateChannelAsync(string name, string description, bool isPrivate);
    Task<Channel> GetChannelAsync(string channelId);
    Task<List<Channel>> GetChannelsAsync();
    Task<bool> AddMemberAsync(string channelId, string userId);
    Task<bool> RemoveMemberAsync(string channelId, string userId);
    Task UpdateChannelAsync(Channel channel);
    Task DeleteChannelAsync(string channelId);
}
