using Microsoft.EntityFrameworkCore;
using IsMocStudio.Core.Entities;
using IsMocStudio.Core.Interfaces.Repositories;
using IsMocStudio.Data.Context;

namespace IsMocStudio.Data.Repositories
{
    public class UserRepository : Repository<User>, IUserRepository
    {
        public UserRepository(IsMocDbContext context) : base(context)
        {
        }

        public async Task<User?> GetByEmailAsync(string email)
        {
            return await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
        }

        public async Task<User> GetByPhoneNumberAsync(string phoneNumber)
        {
            // Assure-toi d'avoir "using Microsoft.EntityFrameworkCore;" tout en haut du fichier
            return await _context.Users.FirstOrDefaultAsync(u => u.PhoneNumber == phoneNumber);
        }
    }
}