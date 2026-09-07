using MateOS.Core.Entities;

namespace MateOS.Core.Interfaces;

public interface IUserService
{
    Task<User> CreateUserAsync(string name, string email, UserType type);
    Task<User> GetUserAsync(string userId);
    Task<User> GetUserByEmailAsync(string email);
    Task<List<User>> GetUsersAsync(UserType? type = null);
    Task UpdateUserAsync(User user);
    Task DeleteUserAsync(string userId);
}
