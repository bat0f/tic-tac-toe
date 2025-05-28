using Microsoft.AspNetCore.SignalR;
using System.Text.RegularExpressions;

namespace tic_tac_toe_api.Hubs
{
    public class TicTacToeOriginalHub : Hub
    {
        private static readonly List<(string ConnectionId, string Username, string Difficulty, int LineLength)> waitingPlayers = new();
        private static readonly Dictionary<string, Session> activeGames = new();
        private readonly ILogger<TicTacToeOriginalHub> _logger;

        public TicTacToeOriginalHub(ILogger<TicTacToeOriginalHub> logger)
        {
            _logger = logger;
        }

        public async Task JoinGame(string username, string difficulty, int lineLength)
        {
            _logger.LogInformation($"Игрок {username} пытается присоединиться с difficulty={difficulty}, lineLength={lineLength}");

            var opponent = waitingPlayers.FirstOrDefault(p => !string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase) && p.Difficulty == difficulty && p.LineLength == lineLength);

            if (opponent.Username == null)
            {
                _logger.LogInformation($"Оппонент не найден. Игрок {username} добавлен в ожидание.");
                waitingPlayers.Add((Context.ConnectionId, username, difficulty, lineLength));
                await Clients.Caller.SendAsync("WaitingForOpponent");
            }
            else
            {
                _logger.LogInformation($"Найден оппонент: {opponent.Username}");
                waitingPlayers.RemoveAll(p => string.Equals(p.Username, opponent.Username, StringComparison.OrdinalIgnoreCase));

                var gameId = $"{username}-{opponent.Username}";
                int boardSize = lineLength == 3 ? 6 : lineLength == 4 ? 8 : 10;

                var session = new Session
                {
                    GameId = gameId,
                    Player1 = username,
                    Player2 = opponent.Username,
                    CurrentPlayer = username,
                    BoardSize = boardSize,
                    LineLength = lineLength,
                    Board = CreateEmptyBoard(boardSize)
                };
                activeGames[gameId] = session;

                var symbolMap = new Dictionary<string, char>
                {
                    { username, 'X' },
                    { opponent.Username, 'O' }
                };

                await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
                await Groups.AddToGroupAsync(opponent.ConnectionId, gameId);

                _logger.LogInformation($"Игра началась: {gameId}, Player1: {username}, Player2: {opponent.Username}");

                await Clients.Group(gameId).SendAsync("GameStarted", gameId, username, opponent.Username, symbolMap);
                await Clients.Group(gameId).SendAsync("UpdateState", session.Board, session.CurrentPlayer);
            }
        }

        public async Task MakeMove(string gameId, string username, int row, int col)
        {
            _logger.LogInformation($"MakeMove вызван с gameId={gameId}, username={username}, row={row}, col={col}");

            if (!activeGames.TryGetValue(gameId, out var session))
            {
                _logger.LogWarning($"Игра {gameId} не найдена.");
                return;
            }

            _logger.LogInformation($"Текущий игрок в игре: {session.CurrentPlayer}");

            if (!string.Equals(session.CurrentPlayer, username, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning($"Не ваш ход, {username}!");
                await Clients.Caller.SendAsync("NotYourTurn", username);
                return;
            }

            if (row < 0 || col < 0 || row >= session.BoardSize || col >= session.BoardSize)
            {
                _logger.LogWarning($"Ход вне границ доски: ({row}, {col})");
                return;
            }

            if (session.Board[row][col] != '\0')
            {
                _logger.LogWarning($"Ячейка ({row}, {col}) уже занята!");
                await Clients.Caller.SendAsync("CellOccupied", row, col);
                return;
            }

            char symbol = string.Equals(username, session.Player1, StringComparison.OrdinalIgnoreCase) ? 'X' : 'O';
            session.Board[row][col] = symbol;

            var nextPlayer = string.Equals(username, session.Player1, StringComparison.OrdinalIgnoreCase) ? session.Player2 : session.Player1;
            session.CurrentPlayer = nextPlayer;

            _logger.LogInformation($"Ход сделан: {username} ({symbol}) в ({row}, {col}). Следующий игрок: {nextPlayer}");

            await Clients.Group(gameId).SendAsync("MoveMade", username, row, col, symbol);
            await Clients.Group(gameId).SendAsync("UpdateState", session.Board, session.CurrentPlayer);

            if (CheckWin(session, row, col, symbol))
            {
                _logger.LogInformation($"{username} победил!");
                activeGames.Remove(gameId);
                await Clients.Group(gameId).SendAsync("GameOver", username, "win");
            }
            else if (CheckDraw(session))
            {
                _logger.LogInformation("Ничья!");
                activeGames.Remove(gameId);
                await Clients.Group(gameId).SendAsync("GameOver", null, "draw");
            }
        }

        private bool CheckWin(Session session, int row, int col, char symbol)
        {
            int boardSize = session.BoardSize;
            var board = session.Board;
            int lineLength = session.LineLength;

            int count;

            // Горизонталь
            count = 0;
            for (int j = 0; j < boardSize; j++)
            {
                if (board[row][j] == symbol) count++;
                else count = 0;
                if (count >= lineLength) return true;
            }

            // Вертикаль
            count = 0;
            for (int i = 0; i < boardSize; i++)
            {
                if (board[i][col] == symbol) count++;
                else count = 0;
                if (count >= lineLength) return true;
            }

            // Главная диагональ
            count = 0;
            int startRow = row - Math.Min(row, col);
            int startCol = col - Math.Min(row, col);
            for (int i = startRow, j = startCol; i < boardSize && j < boardSize; i++, j++)
            {
                if (board[i][j] == symbol) count++;
                else count = 0;
                if (count >= lineLength) return true;
            }

            // Побочная диагональ
            count = 0;
            startRow = row - Math.Min(row, boardSize - 1 - col);
            startCol = col + Math.Min(row, boardSize - 1 - col);
            for (int i = startRow, j = startCol; i < boardSize && j >= 0; i++, j--)
            {
                if (board[i][j] == symbol) count++;
                else count = 0;
                if (count >= lineLength) return true;
            }

            return false;
        }

        private bool CheckDraw(Session session)
        {
            for (int i = 0; i < session.BoardSize; i++)
            {
                for (int j = 0; j < session.BoardSize; j++)
                {
                    if (session.Board[i][j] == '\0') return false;
                }
            }
            return true;
        }

        private char[][] CreateEmptyBoard(int size)
        {
            var newBoard = new char[size][];
            for (int i = 0; i < size; i++)
            {
                newBoard[i] = new char[size];
            }
            return newBoard;
        }

        // Сохраняем твой чат
        public async Task SendMessage(string user, string message)
        {
            await Clients.All.SendAsync("ReceiveMessage", user, message);
        }
    }

    public class Session
    {
        public string GameId { get; set; }
        public string Player1 { get; set; }
        public string Player2 { get; set; }
        public string CurrentPlayer { get; set; }
        public char[][] Board { get; set; }
        public int BoardSize { get; set; }
        public int LineLength { get; set; }
    }
}