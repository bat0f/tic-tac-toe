using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using tic_tac_toe_api.Data;
using tic_tac_toe_api.Hubs;
using System.Security.Cryptography.X509Certificates;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Listen(IPAddress.Any, 5001, options =>
    {
        options.UseHttps("server.pfx", "ZD0dCR8jvG5dBp1kJ0TJX8cK4O0aCuEKGPNTQ3MsvdpdB4t0ffBbgmECShTmwYYsz");
    });
});
//UseHttps(new X509Certificate2("server.pfx", "ZD0dCR8jvG5dBp1kJ0TJX8cK4O0aCuEKGPNTQ3MsvdpdB4t0ffBbgmECShTmwYYsz"));
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=game.db"));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = "TicTacToe",
            ValidAudience = "TicTacToe",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("ZD0dCR8jvG5dBp1kJ0TJX8cK4O0aCuEKGPNTQ3MsvpdB4t0ffBbgmECShTmwYYsz"))
        };
    });

builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Добавляем логирование
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Debug);
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowClient", policy =>
    {
        policy.WithOrigins("https://26.171.188.146:7056") // Указываем строго клиент
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Важно для SignalR
    });
});



var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHub<GameHub>("/gameHub").RequireCors("AllowClient");
app.MapHub<TicTacToeOriginalHub>("/originalHub").RequireCors("AllowClient");
app.UseCors("AllowClient");
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();



app.Run();