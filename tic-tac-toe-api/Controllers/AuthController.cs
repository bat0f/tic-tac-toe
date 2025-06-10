using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using System.Security.Cryptography;
using tic_tac_toe_api.Models;
using tic_tac_toe_api.Data;

namespace tic_tac_toe_api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _dbContext;
        private readonly IConfiguration _config;

        public AuthController(AppDbContext dbContext, IConfiguration config)
        {
            _dbContext = dbContext;
            _config = config;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest model)
        {
            if (await _dbContext.Users.AnyAsync(u => u.Username == model.Username))
            {
                return BadRequest("Пользователь с таким именем уже существует");
            }

            if (!string.IsNullOrEmpty(model.Email) && await _dbContext.Users.AnyAsync(u => u.Email == model.Email))
            {
                return BadRequest("Пользователь с таким email уже существует");
            }

            var salt = BCrypt.Net.BCrypt.GenerateSalt();
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(model.Password, salt);

            var user = new User
            {
                Username = model.Username,
                Email = model.Email,
                PasswordHash = passwordHash,
                Salt = salt
            };

            try
            {
                _dbContext.Users.Add(user);
                await _dbContext.SaveChangesAsync();
                return Ok(new { Message = "Регистрация успешна" });
            }
            catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 19)
            {
                if (sqliteEx.Message.Contains("Users.Email"))
                {
                    return BadRequest("Пользователь с таким email уже существует");
                }
                return BadRequest("Ошибка: данные уже существуют");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error during registration: {ex.Message}");
                return StatusCode(500, "Произошла ошибка на сервере. Попробуйте позже.");
            }
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest model)
        {
            Console.WriteLine($"Login attempt for user: {model.Username}");
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Username == model.Username);
            if (user == null || !BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash))
            {
                Console.WriteLine("Login failed: Invalid username or password");
                return Unauthorized("Неверное имя пользователя или пароль");
            }

            var token = GenerateJwtToken(user);
            var refreshToken = GenerateRefreshToken();
            user.RefreshToken = refreshToken;
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
            await _dbContext.SaveChangesAsync();

            Console.WriteLine($"Login successful for user: {user.Username}, AccessToken: {token}, RefreshToken: {refreshToken}");
            return Ok(new LoginResponse
            {
                AccessToken = token,
                RefreshToken = refreshToken,
                UserId = user.Id,
                Username = user.Username
            });
        }

        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest model)
        {
            Console.WriteLine($"Refresh token request for AccessToken: {model.AccessToken}");
            var principal = GetPrincipalFromExpiredToken(model.AccessToken);
            if (principal == null)
            {
                Console.WriteLine("Refresh failed: Invalid token");
                return BadRequest("Неверный токен");
            }

            var username = principal.Identity.Name;
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user == null || user.RefreshToken != model.RefreshToken || user.RefreshTokenExpiryTime < DateTime.UtcNow)
            {
                Console.WriteLine($"Refresh failed: Invalid or expired refresh token for user {username}");
                return BadRequest("Неверный или истёкший refresh токен");
            }

            var newToken = GenerateJwtToken(user);
            var newRefreshToken = GenerateRefreshToken();
            user.RefreshToken = newRefreshToken;
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
            await _dbContext.SaveChangesAsync();

            Console.WriteLine($"Refresh successful for user: {username}, New AccessToken: {newToken}");
            return Ok(new RefreshTokenResponse
            {
                AccessToken = newToken,
                RefreshToken = newRefreshToken
            });
        }

        [HttpPost("validate")]
        public async Task<IActionResult> ValidateToken([FromBody] string token)
        {
            Console.WriteLine($"Validating token: {token}");
            var principal = GetPrincipalFromToken(token);
            if (principal == null)
            {
                Console.WriteLine("Token validation failed: Invalid token");
                return Unauthorized("Неверный токен");
            }

            var username = principal.Identity.Name;
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user == null)
            {
                Console.WriteLine($"Token validation failed: User {username} not found");
                return Unauthorized("Пользователь не найден");
            }

            Console.WriteLine($"Token validation successful for user: {username}");
            return Ok(new ValidateTokenResponse
            {
                UserId = user.Id,
                Username = user.Username
            });
        }

        [HttpPost("logout")]
        [Authorize]
        public async Task<IActionResult> Logout()
        {
            var username = User.Identity.Name;
            Console.WriteLine($"Logout request for user: {username}");
            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user != null)
            {
                user.RefreshToken = null;
                user.RefreshTokenExpiryTime = null;
                await _dbContext.SaveChangesAsync();
                Console.WriteLine($"Logout successful for user: {username}");
            }
            else
            {
                Console.WriteLine($"Logout failed: User {username} not found");
            }
            return Ok();
        }

        private string GenerateJwtToken(User user)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var expires = DateTime.UtcNow.AddMinutes(15);
            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: new[] { new Claim(ClaimTypes.Name, user.Username) },
                expires: expires,
                signingCredentials: creds);
            var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
            Console.WriteLine($"Generated JWT token for user {user.Username}, expires at {expires:yyyy-MM-dd HH:mm:ss UTC}");
            return tokenString;
        }

        private string GenerateRefreshToken()
        {
            var randomNumber = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomNumber);
            return Convert.ToBase64String(randomNumber);
        }

        private ClaimsPrincipal GetPrincipalFromToken(string token)
        {
            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = true,
                ValidateIssuer = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _config["Jwt:Issuer"],
                ValidAudience = _config["Jwt:Audience"],
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"])),
                ValidateLifetime = true
            };
            var tokenHandler = new JwtSecurityTokenHandler();
            try
            {
                return tokenHandler.ValidateToken(token, tokenValidationParameters, out var securityToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Token validation error: {ex.Message}");
                return null;
            }
        }

        private ClaimsPrincipal GetPrincipalFromExpiredToken(string token)
        {
            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = false,
                ValidateIssuer = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"])),
                ValidateLifetime = false
            };
            var tokenHandler = new JwtSecurityTokenHandler();
            try
            {
                return tokenHandler.ValidateToken(token, tokenValidationParameters, out var securityToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Expired token validation error: {ex.Message}");
                return null;
            }
        }
    }

    public class RegisterRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string? Email { get; set; }
    }

    public class LoginRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public class LoginResponse
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public int UserId { get; set; }
        public string Username { get; set; }
    }

    public class RefreshTokenRequest
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
    }

    public class RefreshTokenResponse
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
    }

    public class ValidateTokenResponse
    {
        public int UserId { get; set; }
        public string Username { get; set; }
    }
}