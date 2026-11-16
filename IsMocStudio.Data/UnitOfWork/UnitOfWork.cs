using IsMocStudio.Core.Entities;
using IsMocStudio.Core.Interfaces.Repositories;
using IsMocStudio.Data.Context;
using IsMocStudio.Data.Repositories;

namespace IsMocStudio.Data.UnitOfWork
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly IsMocDbContext _context;
        public IUserRepository Users { get; }
        public IRepository<Project> Projects { get; }

        public UnitOfWork(IsMocDbContext context)
        {
            _context = context;
            Users = new UserRepository(_context);
            Projects = new Repository<Project>(_context);
        }

        public async Task<int> CompleteAsync()
        {
            return await _context.SaveChangesAsync();
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}