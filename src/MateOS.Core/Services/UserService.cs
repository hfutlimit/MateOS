using MateOS.Core.Entities;
using MateOS.Core.Interfaces;

namespace MateOS.Core.Services;

public class UserService : IUserService
{
    private readonly Dictionary<string, User> _users = new();

    public async Task<User> CreateUserAsync(string name, string email, UserType type)
    {
        var user = new User { Name = name, Email = email, Type = type };
        _users[user.Id] = user;
        return await Task.FromResult(user);
    }

    public async Task<User> GetUserAsync(string userId)
    {
        _users.TryGetValue(userId, out var user);
        return await Task.FromResult(user);
    }

    public async Task<User> GetUserByEmailAsync(string email)
    {
        var user = _users.Values.FirstOrDefault(u => u.Email == email);
        return await Task.FromResult(user);
    }

    public async Task<List<User>> GetUsersAsync(UserType? type = null)
    {
        var users = type == null
            ? _users.Values.ToList()
            : _users.Values.Where(u => u.Type == type).ToList();
        return await Task.FromResult(users);
    }

    public async Task UpdateUserAsync(User user)
    {
        user.UpdatedAt = DateTime.UtcNow;
        _users[user.Id] = user;
        await Task.CompletedTask;
    }

    public async Task DeleteUserAsync(string userId)
    {
        _users.Remove(userId);
        await Task.CompletedTask;
    }
}
