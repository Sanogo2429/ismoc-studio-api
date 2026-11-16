using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Interfaces.Repositories;
using IsMocStudio.Core.Interfaces.Services;

namespace IsMocStudio.Business.Services
{
    public class UserService : IUserService
    {
        private readonly IUnitOfWork _unitOfWork;

        public UserService(IUnitOfWork unitOfWork)
        {
            _unitOfWork = unitOfWork;
        }

        public async Task<IEnumerable<UserResponseDto>> GetAllUsersAsync()
        {
            var users = await _unitOfWork.Users.GetAllAsync();

            return users.Select(user => new UserResponseDto
            {
                Id = user.Id,
                Email = user.Email,
                FullName = $"{user.FirstName} {user.LastName}",
                Role = user.Role
            });
        }

        public async Task<UserResponseDto> GetUserByIdAsync(int id)
        {
            var user = await _unitOfWork.Users.GetByIdAsync(id);
            if (user == null)
            {
                throw new Exception("Utilisateur introuvable.");
            }

            return new UserResponseDto
            {
                Id = user.Id,
                Email = user.Email,
                FullName = $"{user.FirstName} {user.LastName}",
                Role = user.Role
            };
        }

        public async Task DeleteUserAsync(int id)
        {
            var user = await _unitOfWork.Users.GetByIdAsync(id);
            if (user == null)
            {
                throw new Exception("Utilisateur introuvable.");
            }

            _unitOfWork.Users.Remove(user);
            await _unitOfWork.CompleteAsync();
        }
    }
}