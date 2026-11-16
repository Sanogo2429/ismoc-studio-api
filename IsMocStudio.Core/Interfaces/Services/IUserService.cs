using IsMocStudio.Core.DTOs;

namespace IsMocStudio.Core.Interfaces.Services
{
    // C'est bien une INTERFACE et elle est PUBLIC
    public interface IUserService
    {
        Task<IEnumerable<UserResponseDto>> GetAllUsersAsync();
        Task<UserResponseDto> GetUserByIdAsync(int id);
        Task DeleteUserAsync(int id);
    }
}