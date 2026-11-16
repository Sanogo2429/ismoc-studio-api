using IsMocStudio.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization; // <-- NOUVEAU
using Microsoft.AspNetCore.Mvc;

namespace IsMocStudio.Api.Controllers // ou IsMocStudio.api.Controllers selon ton dossier

{
    [Authorize(Roles = "Admin")] // <-- LE FAMEUX CADENAS EST ICI
    [Route("api/[controller]")] // L'URL sera : http://localhost:port/api/users
    [ApiController]
    public class UsersController : ControllerBase
    {
        private readonly IUserService _userService;

        // On demande (injecte) notre IUserService ici
        public UsersController(IUserService userService)
        {
            _userService = userService;
        }

        // GET: api/users
        // Permet de récupérer la liste de tous les clients
        [HttpGet]
        public async Task<IActionResult> GetAllUsers()
        {
            var users = await _userService.GetAllUsersAsync();
            return Ok(users); // 200 OK avec la liste
        }

        // GET: api/users/{id}
        // Permet de récupérer un client spécifique par son ID
        [HttpGet("{id}")]
        public async Task<IActionResult> GetUserById(int id)
        {
            try
            {
                var user = await _userService.GetUserByIdAsync(id);
                return Ok(user);
            }
            catch (Exception ex)
            {
                return NotFound(new { Message = ex.Message }); // Erreur 404 (Introuvable)
            }
        }

        // DELETE: api/users/{id}
        // Permet de supprimer un client
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteUser(int id)
        {
            try
            {
                await _userService.DeleteUserAsync(id);
                return NoContent(); // 204 No Content : Action réussie, pas de données à renvoyer
            }
            catch (Exception ex)
            {
                return NotFound(new { Message = ex.Message });
            }
        }
    }
}