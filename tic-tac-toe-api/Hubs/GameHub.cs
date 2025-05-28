using Microsoft.AspNetCore.SignalR;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace tic_tac_toe_api.Hubs
{
    public class GameHub : Hub
    {
        private static readonly List<(string ConnectionId, string Username, string Difficulty, int LineLength)> waitingPlayers = new();
        private static readonly Dictionary<string, GameSession> activeGames = new();
        private readonly ILogger<GameHub> _logger;
        private readonly Random random = new Random();

        public GameHub(ILogger<GameHub> logger)
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

                var session = new GameSession
                {
                    GameId = gameId,
                    Player1 = username,
                    Player2 = opponent.Username,
                    CurrentPlayer = username,
                    BoardSize = boardSize,
                    LineLength = lineLength,
                    Difficulty = difficulty,
                    Board = CreateEmptyBoard(boardSize),
                    UsedTasks = new HashSet<string>()
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
                await Clients.Group(gameId).SendAsync("UpdateState", session.Board, session.CurrentPlayer, 8, "");
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

            session.SelectedRow = row;
            session.SelectedCol = col;
            session.RoundTask = GenerateUniqueTask(session.Difficulty, session.UsedTasks);
            session.RoundAnswer = EvaluateTask(session.RoundTask);
            _logger.LogInformation($"Новая задача раунда: {session.RoundTask}, Ответ: {session.RoundAnswer}");
            await Clients.Group(gameId).SendAsync("UpdateState", session.Board, session.CurrentPlayer, 15, session.RoundTask);
        }

        public async Task SubmitAnswer(string gameId, string username, string userAnswer)
        {
            _logger.LogInformation($"SubmitAnswer вызван с gameId={gameId}, username={username}, answer={userAnswer}");

            if (!activeGames.TryGetValue(gameId, out var session))
            {
                _logger.LogWarning($"Игра {gameId} не найдена.");
                return;
            }

            if (!string.Equals(session.CurrentPlayer, username, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning($"Не ваш ход, {username}!");
                return;
            }

            bool isCorrect = int.TryParse(userAnswer, out int answer) && answer == session.RoundAnswer;
            int row = session.SelectedRow;
            int col = session.SelectedCol;

            if (isCorrect)
            {
                _logger.LogInformation($"{username} решил задачу правильно!");
                char symbol = string.Equals(username, session.Player1, StringComparison.OrdinalIgnoreCase) ? 'X' : 'O';
                session.Board[row][col] = symbol;
                await Clients.Group(gameId).SendAsync("MoveMade", username, row, col, symbol);
                await Clients.Group(gameId).SendAsync("TaskFeedback", "Правильно!");

                if (CheckWin(session, row, col, symbol) || CheckDraw(session))
                {
                    if (CheckWin(session, row, col, symbol))
                    {
                        _logger.LogInformation($"{username} победил!");
                        activeGames.Remove(gameId);
                        await Clients.Group(gameId).SendAsync("GameOver", username, "win");
                    }
                    else
                    {
                        _logger.LogInformation("Ничья!");
                        activeGames.Remove(gameId);
                        await Clients.Group(gameId).SendAsync("GameOver", null, "draw");
                    }
                }
                else
                {
                    var nextPlayer = string.Equals(username, session.Player1, StringComparison.OrdinalIgnoreCase) ? session.Player2 : session.Player1;
                    session.CurrentPlayer = nextPlayer;
                    session.SelectedRow = -1;
                    session.SelectedCol = -1;
                    session.RoundMoves++;
                    session.RoundTask = "";
                    session.RoundAnswer = 0;
                    _logger.LogInformation($"Передаём ход: {nextPlayer}, выбор клетки");
                    await Clients.Group(gameId).SendAsync("UpdateState", session.Board, session.CurrentPlayer, 8, "");
                }
            }
            else
            {
                _logger.LogInformation($"{username} решил задачу неправильно.");
                await Clients.Group(gameId).SendAsync("TaskFeedback", "Неверно, ход передан");
                var nextPlayer = string.Equals(username, session.Player1, StringComparison.OrdinalIgnoreCase) ? session.Player2 : session.Player1;
                session.CurrentPlayer = nextPlayer;
                session.SelectedRow = -1;
                session.SelectedCol = -1;
                session.RoundMoves++;
                session.RoundTask = "";
                session.RoundAnswer = 0;
                _logger.LogInformation($"Передаём ход: {nextPlayer}, выбор клетки");
                await Clients.Group(gameId).SendAsync("UpdateState", session.Board, session.CurrentPlayer, 8, "");
            }
        }

        public async Task PassTurn(string gameId, string username)
        {
            _logger.LogInformation($"PassTurn вызван с gameId={gameId}, username={username}");

            if (!activeGames.TryGetValue(gameId, out var session))
            {
                _logger.LogWarning($"Игра {gameId} не найдена.");
                return;
            }

            if (!string.Equals(session.CurrentPlayer, username, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning($"Текущий игрок не {username}, передача хода не требуется.");
                return;
            }

            var nextPlayer = string.Equals(username, session.Player1, StringComparison.OrdinalIgnoreCase) ? session.Player2 : session.Player1;
            session.CurrentPlayer = nextPlayer;
            session.SelectedRow = -1;
            session.SelectedCol = -1;
            session.RoundMoves++;
            session.RoundTask = "";
            session.RoundAnswer = 0;

            _logger.LogInformation($"Передаём ход: {nextPlayer}, выбор клетки");
            await Clients.Group(gameId).SendAsync("UpdateState", session.Board, session.CurrentPlayer, 8, "");
        }

        private string GenerateUniqueTask(string difficulty, HashSet<string> usedTasks)
        {
            string task;
            do
            {
                switch (difficulty.ToLower())
                {
                    case "easy":
                        int num1e = random.Next(1, 101);
                        int num2e = random.Next(1, 101);
                        string opEasy = random.NextDouble() < 0.5 ? "+" : "-";
                        task = $"{num1e} {opEasy} {num2e} =";
                        if (opEasy == "-" && num1e < num2e) { int temp = num1e; num1e = num2e; num2e = temp; task = $"{num1e} {opEasy} {num2e} ="; }
                        break;
                    case "medium":
                        if (random.NextDouble() < 0.5)
                        {
                            int num1m = random.Next(1, 101);
                            int num2m = random.Next(1, num1m + 1);
                            string opMed = random.NextDouble() < 0.5 ? "×" : "÷";
                            if (opMed == "÷")
                            {
                                while (num1m % num2m != 0) num2m = random.Next(1, num1m + 1);
                            }
                            task = $"{num1m} {opMed} {num2m} =";
                        }
                        else
                        {
                            int num1m2 = random.Next(1, 101);
                            int num2m2 = random.Next(1, 101 - num1m2);
                            int num3m2 = random.Next(1, num1m2 + num2m2 + 1);
                            task = $"{num1m2} + {num2m2} - {num3m2} =";
                            if (EvaluateTask(task) < 0) num3m2 = random.Next(1, num1m2 + num2m2);
                        }
                        break;
                    case "hard":
                        int num1h = random.Next(1, 101);
                        int num2h = random.Next(1, 101);
                        int num3h = random.Next(1, num2h + 1);
                        if (random.NextDouble() < 0.5)
                        {
                            string op1h = new[] { "+", "-", "×", "÷" }[random.Next(4)];
                            string op2h = new[] { "+", "-", "×", "÷" }[random.Next(4)];
                            if (op2h == "÷" && num2h % num3h != 0) while (num2h % num3h != 0) num3h = random.Next(1, num2h + 1);
                            task = $"{num1h} {op1h} ({num2h} {op2h} {num3h}) =";
                        }
                        else
                        {
                            string op1h = new[] { "+", "-", "×", "÷" }[random.Next(4)];
                            string op2h = new[] { "+", "-", "×", "÷" }[random.Next(4)];
                            if (op2h == "÷" && num2h % num3h != 0) while (num2h % num3h != 0) num3h = random.Next(1, num2h + 1);
                            task = $"{num1h} {op1h} {num2h} {op2h} {num3h} =";
                        }
                        break;
                    default:
                        task = "1 + 1 =";
                        break;
                }
            } while (usedTasks.Contains(task) || EvaluateTask(task) < 0);
            usedTasks.Add(task);
            return task;
        }

        private int EvaluateTask(string task)
        {
            string[] parts = task.Split(new[] { ' ', '=' }, StringSplitOptions.RemoveEmptyEntries);
            int result = int.Parse(parts[0]);

            for (int i = 1; i < parts.Length - 1; i += 2)
            {
                int nextNum = int.Parse(parts[i + 1]);
                switch (parts[i])
                {
                    case "+": result += nextNum; break;
                    case "-": result -= nextNum; break;
                    case "×": result *= nextNum; break;
                    case "÷": result /= nextNum; break;
                }
            }
            return result;
        }

        private bool CheckWin(GameSession session, int row, int col, char symbol)
        {
            int boardSize = session.BoardSize;
            var board = session.Board;
            int lineLength = session.LineLength;

            int count;

            count = 0;
            for (int j = 0; j < boardSize; j++)
            {
                if (board[row][j] == symbol) count++;
                else count = 0;
                if (count >= lineLength) return true;
            }

            count = 0;
            for (int i = 0; i < boardSize; i++)
            {
                if (board[i][col] == symbol) count++;
                else count = 0;
                if (count >= lineLength) return true;
            }

            count = 0;
            int startRow = row - Math.Min(row, col);
            int startCol = col - Math.Min(row, col);
            for (int i = startRow, j = startCol; i < boardSize && j < boardSize; i++, j++)
            {
                if (board[i][j] == symbol) count++;
                else count = 0;
                if (count >= lineLength) return true;
            }

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

        private bool CheckDraw(GameSession session)
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

        public async Task SendMessage(string user, string message)
        {
            await Clients.All.SendAsync("ReceiveMessage", user, message);
        }
    }

    public class GameSession
    {
        public string GameId { get; set; }
        public string Player1 { get; set; }
        public string Player2 { get; set; }
        public string CurrentPlayer { get; set; }
        public char[][] Board { get; set; }
        public int BoardSize { get; set; }
        public int LineLength { get; set; }
        public string Difficulty { get; set; }
        public HashSet<string> UsedTasks { get; set; }
        public string RoundTask { get; set; }
        public int RoundAnswer { get; set; }
        public int SelectedRow { get; set; }
        public int SelectedCol { get; set; }
        public int RoundMoves { get; set; }
    }
}