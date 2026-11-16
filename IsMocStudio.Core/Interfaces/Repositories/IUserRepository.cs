using IsMocStudio.Core.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IsMocStudio.Core.Interfaces.Repositories
{
    public interface IUserRepository : IRepository<User>
    {
        Task<User?> GetByEmailAsync(string email);
        Task<User> GetByPhoneNumberAsync(string phoneNumber);
    }
}
