using Microsoft.AspNetCore.Mvc;
using MateOS.Core.DTOs;
using MateOS.Core.Entities;
using MateOS.Core.Interfaces;

namespace MateOS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;

    public UsersController(IUserService userService)
    {
        _userService = userService;
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> CreateUser(CreateUserDto dto)
    {
        if (!Enum.TryParse<UserType>(dto.Type, true, out var userType))
            return BadRequest("Invalid user type");

        var user = await _userService.CreateUserAsync(dto.Name, dto.Email, userType);
        return CreatedAtAction(nameof(GetUser), new { id = user.Id }, MapToDto(user));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UserDto>> GetUser(string id)
    {
        var user = await _userService.GetUserAsync(id);
        if (user == null)
            return NotFound();

        return Ok(MapToDto(user));
    }

    [HttpGet]
    public async Task<ActionResult<List<UserDto>>> GetUsers([FromQuery] string type = null)
    {
        UserType? userType = null;
        if (!string.IsNullOrEmpty(type) && Enum.TryParse<UserType>(type, true, out var ut))
            userType = ut;

        var users = await _userService.GetUsersAsync(userType);
        return Ok(users.Select(MapToDto).ToList());
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateUser(string id, UserDto dto)
    {
        if (id != dto.Id)
            return BadRequest();

        if (!Enum.TryParse<UserType>(dto.Type, true, out var userType))
            return BadRequest("Invalid user type");

        var user = new User { Id = id, Name = dto.Name, Email = dto.Email, Type = userType, CreatedAt = dto.CreatedAt, UpdatedAt = dto.UpdatedAt };
        await _userService.UpdateUserAsync(user);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUser(string id)
    {
        await _userService.DeleteUserAsync(id);
        return NoContent();
    }

    private UserDto MapToDto(User user) => new()
    {
        Id = user.Id,
        Name = user.Name,
        Email = user.Email,
        Type = user.Type.ToString(),
        CreatedAt = user.CreatedAt,
        UpdatedAt = user.UpdatedAt
    };
}
