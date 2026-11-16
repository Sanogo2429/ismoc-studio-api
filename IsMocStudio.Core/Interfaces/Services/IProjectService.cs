using IsMocStudio.Core.DTOs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IsMocStudio.Core.Interfaces.Services
{
    public interface IProjectService
    {
        Task<ProjectResponseDto> CreateProjectAsync(int userId, CreateProjectDto request);
        Task<IEnumerable<ProjectResponseDto>> GetUserProjectsAsync(int userId);
        Task<ProjectResponseDto> GetProjectByIdAsync(int userId, int projectId); // <-- AJOUTÉ ICI
    }
}
