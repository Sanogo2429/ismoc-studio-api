using Microsoft.EntityFrameworkCore;
using IsMocStudio.Core.Interfaces.Repositories;
using IsMocStudio.Data.Context;
using System.Linq.Expressions;

namespace IsMocStudio.Data.Repositories
{
    public class Repository<T> : IRepository<T> where T : class
    {
        protected readonly IsMocDbContext _context;

        public Repository(IsMocDbContext context)
        {
            _context = context;
        }

        public async Task<T?> GetByIdAsync(int id) => await _context.Set<T>().FindAsync(id);

        public async Task<IEnumerable<T>> GetAllAsync() => await _context.Set<T>().ToListAsync();

        public async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate) =>
            await _context.Set<T>().Where(predicate).ToListAsync();

        public async Task AddAsync(T entity) => await _context.Set<T>().AddAsync(entity);

        public void Update(T entity) => _context.Set<T>().Update(entity);

        public void Remove(T entity) => _context.Set<T>().Remove(entity);
    }
}