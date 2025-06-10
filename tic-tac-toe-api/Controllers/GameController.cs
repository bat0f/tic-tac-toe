using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace tic_tac_toe_api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GameController : ControllerBase
    {
        private static readonly ConcurrentDictionary<string, (string Username, string ConnectionId)> _waitingPlayers = new();
        private static readonly ConcurrentDictionary<string, GameSession> _games = new();
        private readonly ILogger<GameController> _logger;

        public GameController(ILogger<GameController> logger)
        {
            _logger = logger;
        }

        [HttpPost("join")]
        public IActionResult JoinGame([FromBody] JoinRequest request)
        {
            var username = User.Identity?.Name; // Получаем имя пользователя из токена
            if (string.IsNullOrEmpty(username) || username != request.Username)
            {
                return Unauthorized("Неверный токен или имя пользователя.");
            }

            _logger.LogInformation("Игрок {Username} пытается присоединиться", username);

            string? opponentKey = null;
            string? opponentConnectionId = null;
            foreach (var player in _waitingPlayers)
            {
                if (player.Key != username)
                {
                    opponentKey = player.Key;
                    opponentConnectionId = player.Value.ConnectionId;
                    break;
                }
            }

            if (opponentKey != null && opponentConnectionId != null)
            {
                _logger.LogInformation("Найден оппонент: {Opponent}", opponentKey);
                _waitingPlayers.TryRemove(opponentKey, out _);

                var gameId = Guid.NewGuid().ToString();
                var session = new GameSession
                {
                    Player1 = opponentKey,
                    Player2 = username,
                    Board = new char[3, 3],
                    CurrentPlayer = opponentKey,
                    SymbolMap = new Dictionary<string, char> { { opponentKey, 'X' }, { username, 'O' } }
                };
                _games.TryAdd(gameId, session);

                return Ok(new { gameId, player1 = session.Player1, player2 = session.Player2, symbolMap = session.SymbolMap });
            }
            else
            {
                _logger.LogInformation("Оппонент не найден. Игрок {Username} добавлен в ожидание.", username);
                _waitingPlayers.TryAdd(username, (username, Guid.NewGuid().ToString())); // Симулируем ConnectionId
                return Accepted(new { status = "waiting" });
            }
        }

        [HttpPost("move")]
        public IActionResult MakeMove([FromBody] MoveRequest request)
        {
            var username = User.Identity?.Name;
            if (string.IsNullOrEmpty(username) || username != request.Username)
            {
                return Unauthorized("Неверный токен или имя пользователя.");
            }

            if (!_games.TryGetValue(request.GameId, out var session) || session.CurrentPlayer != username)
            {
                return BadRequest("Невалидный ход или не твой ход.");
            }

            if (session.Board[request.Row, request.Col] != '\0')
            {
                return BadRequest("Клетка занята.");
            }

            session.Board[request.Row, request.Col] = session.SymbolMap[username];
            _logger.LogInformation("Ход сделан: {Username} поставил {Symbol} на row={Row}, col={Col}", username, session.SymbolMap[username], request.Row, request.Col);

            // Переключаем игрока
            session.CurrentPlayer = session.Player1 == username ? session.Player2 : session.Player1;

            return Ok(new { player = username, row = request.Row, col = request.Col, symbol = session.SymbolMap[username], nextPlayer = session.CurrentPlayer });
        }

        [HttpGet("state/{gameId}")]
        public IActionResult GetGameState(string gameId)
        {
            var username = User.Identity?.Name;
            if (string.IsNullOrEmpty(username) || (!_games.ContainsKey(gameId) && !_games[gameId].Player1.Equals(username) && !_games[gameId].Player2.Equals(username)))
            {
                return Unauthorized("Недостаточно прав для доступа к игре.");
            }

            if (_games.TryGetValue(gameId, out var session))
            {
                return Ok(new
                {
                    board = session.Board,
                    currentPlayer = session.CurrentPlayer,
                    player1 = session.Player1,
                    player2 = session.Player2
                });
            }
            return NotFound("Игра не найдена.");
        }
    }

    public class JoinRequest
    {
        public string Username { get; set; } = string.Empty;
    }

    public class MoveRequest
    {
        public string GameId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public int Row { get; set; }
        public int Col { get; set; }
    }

    public class GameSession
    {
        public string Player1 { get; set; } = string.Empty;
        public string Player2 { get; set; } = string.Empty;
        public char[,] Board { get; set; }
        public string CurrentPlayer { get; set; } = string.Empty;
        public Dictionary<string, char> SymbolMap { get; set; } = new();
    }
}