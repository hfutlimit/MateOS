using MateOS.Core.Entities;
using MateOS.Core.Interfaces;

namespace MateOS.Core.Services;

public class ChannelService : IChannelService
{
    private readonly Dictionary<string, Channel> _channels = new();

    public async Task<Channel> CreateChannelAsync(string name, string description, bool isPrivate)
    {
        var channel = new Channel { Name = name, Description = description, IsPrivate = isPrivate };
        _channels[channel.Id] = channel;
        return await Task.FromResult(channel);
    }

    public async Task<Channel> GetChannelAsync(string channelId)
    {
        _channels.TryGetValue(channelId, out var channel);
        return await Task.FromResult(channel);
    }

    public async Task<List<Channel>> GetChannelsAsync()
    {
        return await Task.FromResult(_channels.Values.ToList());
    }

    public async Task<bool> AddMemberAsync(string channelId, string userId)
    {
        if (_channels.TryGetValue(channelId, out var channel))
        {
            if (!channel.MemberIds.Contains(userId))
            {
                channel.MemberIds.Add(userId);
                channel.UpdatedAt = DateTime.UtcNow;
            }
            return await Task.FromResult(true);
        }
        return await Task.FromResult(false);
    }

    public async Task<bool> RemoveMemberAsync(string channelId, string userId)
    {
        if (_channels.TryGetValue(channelId, out var channel))
        {
            channel.MemberIds.Remove(userId);
            channel.UpdatedAt = DateTime.UtcNow;
            return await Task.FromResult(true);
        }
        return await Task.FromResult(false);
    }

    public async Task UpdateChannelAsync(Channel channel)
    {
        channel.UpdatedAt = DateTime.UtcNow;
        _channels[channel.Id] = channel;
        await Task.CompletedTask;
    }

    public async Task DeleteChannelAsync(string channelId)
    {
        _channels.Remove(channelId);
        await Task.CompletedTask;
    }
}
