using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Entities;
using IsMocStudio.Core.Interfaces.Repositories;
using IsMocStudio.Core.Interfaces.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace IsMocStudio.Business.Services
{
    public class ProjectService : IProjectService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IAiService _aiService;

        public ProjectService(IUnitOfWork unitOfWork, IAiService aiService)
        {
            _unitOfWork = unitOfWork;
            _aiService = aiService;
        }

        public async Task<ProjectResponseDto> CreateProjectAsync(int userId, CreateProjectDto request)
        {
            string generatedJsonConfig = await _aiService.GenerateJsonBriefAsync(request.Type, request.Description ?? string.Empty);

            var project = new Project
            {
                Name = request.Name,
                Type = request.Type,
                Description = request.Description ?? string.Empty,
                JsonConfiguration = generatedJsonConfig,
                UserId = userId,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.Projects.AddAsync(project);
            await _unitOfWork.CompleteAsync();

            return new ProjectResponseDto
            {
                Id = project.Id,
                Name = project.Name,
                Type = project.Type,
                Description = project.Description,
                JsonConfiguration = project.JsonConfiguration, // <-- Mappé ici
                CreatedAt = project.CreatedAt
            };
        }

        public async Task<IEnumerable<ProjectResponseDto>> GetUserProjectsAsync(int userId)
        {
            var projects = await _unitOfWork.Projects.FindAsync(p => p.UserId == userId);

            return projects.Select(p => new ProjectResponseDto
            {
                Id = p.Id,
                Name = p.Name,
                Type = p.Type,
                Description = p.Description,
                JsonConfiguration = p.JsonConfiguration, // <-- Mappé ici aussi
                CreatedAt = p.CreatedAt
            });
        }

        public async Task<ProjectResponseDto> GetProjectByIdAsync(int userId, int projectId)
        {
            var projects = await _unitOfWork.Projects.FindAsync(p => p.Id == projectId && p.UserId == userId);
            var project = projects.FirstOrDefault();

            if (project == null)
            {
                throw new Exception("Projet introuvable ou non autorisé.");
            }

            return new ProjectResponseDto
            {
                Id = project.Id,
                Name = project.Name,
                Type = project.Type,
                Description = project.Description,
                JsonConfiguration = project.JsonConfiguration,
                CreatedAt = project.CreatedAt
            };
        }
    }
}