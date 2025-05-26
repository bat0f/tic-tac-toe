using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore; // Добавлена для AnyAsync и FirstOrDefaultAsync
using BCrypt.Net;
using System.Text.RegularExpressions;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using tic_tac_toe_api.Data;
using tic_tac_toe_api.Models;

namespace TicTacToeApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _config;

        public AuthController(AppDbContext context, IConfiguration config)
        {
            _context = context;
            _config = config;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterModel model)
        {
            if (!Regex.IsMatch(model.Email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                return BadRequest("Некорректный email.");

            if (model.Password.Length < 5)
                return BadRequest("Пароль должен быть не менее 5 символов.");

            if (await _context.Users.AnyAsync(u => u.Username == model.Username)) // Исправлено
                return BadRequest("Никнейм уже занят.");

            if (await _context.Users.AnyAsync(u => u.Email == model.Email)) // Исправлено
                return BadRequest("Email уже зарегистрирован.");

            var user = new User
            {
                Username = model.Username,
                Email = model.Email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(model.Password)
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();
            return Ok("Регистрация успешна.");
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginModel model)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == model.Username); // Исправлено
            if (user == null || !BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash))
                return Unauthorized("Неверный никнейм или пароль.");

            var token = GenerateJwtToken(user);
            return Ok(new { Token = token });
        }

        [HttpDelete("delete")]
        public async Task<IActionResult> Delete([FromBody] DeleteModel model)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == model.Username); // Исправлено
            if (user == null || !BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash))
                return Unauthorized("Неверный никнейм или пароль.");

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();
            return Ok("Аккаунт удалён.");
        }

        private string GenerateJwtToken(User user)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: claims,
                expires: DateTime.Now.AddDays(7),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    public class RegisterModel
    {
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class LoginModel
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class DeleteModel
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}