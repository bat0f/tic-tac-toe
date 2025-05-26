using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace tic_tac_toe_api.Hubs
{
    public class GameHub : Hub
    {
        private static readonly ConcurrentDictionary<string, GameSession> _games = new();
        private static readonly ConcurrentDictionary<string, (string Difficulty, int LineLength)> _waitingPlayers = new();

        public async Task JoinGame(string username, string difficulty, int lineLength)
        {
            var opponent = _waitingPlayers.FirstOrDefault(p =>
                p.Value.Difficulty == difficulty && p.Value.LineLength == lineLength && p.Key != username);

            if (opponent.Key != null)
            {
                _waitingPlayers.TryRemove(opponent.Key, out _);
                var gameId = Guid.NewGuid().ToString();
                var session = new GameSession
                {
                    Player1 = opponent.Key,
                    Player2 = username,
                    Difficulty = difficulty,
                    LineLength = lineLength,
                    Board = CreateBoard(lineLength),
                    CurrentPlayer = opponent.Key,
                    SymbolMap = new Dictionary<string, char> { { opponent.Key, 'X' }, { username, 'O' } }
                };
                _games.TryAdd(gameId, session);

                await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
                await Groups.AddToGroupAsync(_waitingPlayers.First(p => p.Key == opponent.Key).Key, gameId);

                await Clients.Group(gameId).SendAsync("GameStarted", gameId, session.Player1, session.Player2, session.SymbolMap);
                await SendMathTask(gameId, session.CurrentPlayer);
            }
            else
            {
                _waitingPlayers.TryAdd(username, (difficulty, lineLength));
                await Clients.Caller.SendAsync("WaitingForOpponent");
            }
        }

        public async Task MakeMove(string gameId, string username, int row, int col, int? answer)
        {
            if (!_games.TryGetValue(gameId, out var session) || session.CurrentPlayer != username)
                return;

            // Проверка задачи
            if (answer != session.CurrentTask?.Answer)
            {
                await Clients.Group(gameId).SendAsync("InvalidAnswer", username);
                session.CurrentPlayer = session.Player1 == username ? session.Player2 : session.Player1;
                await SendMathTask(gameId, session.CurrentPlayer);
                return;
            }

            // Проверка хода
            if (session.Board[row, col] != '\0')
                return;

            session.Board[row, col] = session.SymbolMap[username];
            await Clients.Group(gameId).SendAsync("MoveMade", username, row, col, session.SymbolMap[username]);

            // Проверка победы
            if (CheckWin(session.Board, session.SymbolMap[username], session.LineLength))
            {
                await Clients.Group(gameId).SendAsync("GameOver", username, "win");
                _games.TryRemove(gameId, out _);
                return;
            }

            // Проверка ничьей
            if (session.Board.Cast<char>().All(c => c != '\0'))
            {
                await Clients.Group(gameId).SendAsync("GameOver", null, "draw");
                _games.TryRemove(gameId, out _);
                return;
            }

            session.CurrentPlayer = session.Player1 == username ? session.Player2 : session.Player1;
            await SendMathTask(gameId, session.CurrentPlayer);
        }

        private async Task SendMathTask(string gameId, string player)
        {
            if (!_games.TryGetValue(gameId, out var session))
                return;

            var task = GenerateMathTask(session.Difficulty);
            session.CurrentTask = task;
            await Clients.Group(gameId).SendAsync("NewTask", player, task.Expression);
        }

        private char[,] CreateBoard(int lineLength)
        {
            int size = lineLength == 3 ? 6 : lineLength == 4 ? 8 : 10;
            return new char[size, size];
        }

        private bool CheckWin(char[,] board, char symbol, int lineLength)
        {
            int size = board.GetLength(0);

            // Горизонтали
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j <= size - lineLength; j++)
                {
                    bool win = true;
                    for (int k = 0; k < lineLength; k++)
                    {
                        if (board[i, j + k] != symbol)
                        {
                            win = false;
                            break;
                        }
                    }
                    if (win) return true;
                }
            }

            // Вертикали
            for (int i = 0; i <= size - lineLength; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    bool win = true;
                    for (int k = 0; k < lineLength; k++)
                    {
                        if (board[i + k, j] != symbol)
                        {
                            win = false;
                            break;
                        }
                    }
                    if (win) return true;
                }
            }

            // Главные диагонали (слева направо)
            for (int i = 0; i <= size - lineLength; i++)
            {
                for (int j = 0; j <= size - lineLength; j++)
                {
                    bool win = true;
                    for (int k = 0; k < lineLength; k++)
                    {
                        if (board[i + k, j + k] != symbol)
                        {
                            win = false;
                            break;
                        }
                    }
                    if (win) return true;
                }
            }

            // Побочные диагонали (справа налево)
            for (int i = 0; i <= size - lineLength; i++)
            {
                for (int j = lineLength - 1; j < size; j++)
                {
                    bool win = true;
                    for (int k = 0; k < lineLength; k++)
                    {
                        if (board[i + k, j - k] != symbol)
                        {
                            win = false;
                            break;
                        }
                    }
                    if (win) return true;
                }
            }

            return false;
        }

        private MathTask GenerateMathTask(string difficulty)
        {
            var rand = new Random();
            int num1, num2, num3;
            string expression;
            int answer;

            switch (difficulty.ToLower())
            {
                case "easy":
                    num1 = rand.Next(1, 101);
                    num2 = rand.Next(1, 101);
                    bool isSubtraction = rand.Next(0, 2) == 0 && num1 >= num2;
                    expression = isSubtraction ? $"{num1} − {num2}" : $"{num1} + {num2}";
                    answer = isSubtraction ? num1 - num2 : num1 + num2;
                    break;

                case "medium":
                    if (rand.Next(0, 2) == 0) // Одна операция (умножение или деление)
                    {
                        num1 = rand.Next(1, 201);
                        num2 = rand.Next(1, 21);
                        bool isDivision = rand.Next(0, 2) == 0;
                        if (isDivision)
                        {
                            num1 = num1 * num2; // Гарантируем целое число
                            expression = $"{num1} ÷ {num2}";
                            answer = num1 / num2;
                        }
                        else
                        {
                            expression = $"{num1} × {num2}";
                            answer = num1 * num2;
                        }
                    }
                    else // Две операции (сложение/вычитание)
                    {
                        num1 = rand.Next(1, 201);
                        num2 = rand.Next(1, 201);
                        num3 = rand.Next(1, 101);
                        bool subtractFirst = rand.Next(0, 2) == 0 && num1 >= num2;
                        expression = subtractFirst ? $"{num1} − {num2} + {num3}" : $"{num1} + {num2} − {num3}";
                        answer = subtractFirst ? (num1 - num2) + num3 : (num1 + num2) - num3;
                    }
                    break;

                case "hard":
                    num1 = rand.Next(1, 201);
                    num2 = rand.Next(1, 201);
                    num3 = rand.Next(1, 21);
                    bool useBrackets = rand.Next(0, 2) == 0; // 50% задач со скобками
                    bool isNegative = rand.Next(0, 100) < 15; // 15% отрицательных результатов
                    if (useBrackets)
                    {
                        bool subtractInBrackets = rand.Next(0, 2) == 0 && num2 >= num3;
                        expression = subtractInBrackets ? $"{num1} × ({num2} − {num3})" : $"{num1} × ({num2} + {num3})";
                        answer = subtractInBrackets ? num1 * (num2 - num3) : num1 * (num2 + num3);
                    }
                    else
                    {
                        bool multiplyFirst = rand.Next(0, 2) == 0;
                        expression = multiplyFirst ? $"{num1} + {num2} × {num3}" : $"{num1} × {num2} + {num3}";
                        answer = multiplyFirst ? num1 + (num2 * num3) : (num1 * num2) + num3;
                    }
                    if (isNegative)
                    {
                        expression = $"({expression}) − {answer + rand.Next(1, 51)}";
                        answer = answer - (answer + rand.Next(1, 51));
                    }
                    break;

                default:
                    throw new ArgumentException("Неверный уровень сложности.");
            }

            return new MathTask { Expression = expression, Answer = answer };
        }
    }

    public class GameSession
    {
        public string Player1 { get; set; } = string.Empty;
        public string Player2 { get; set; } = string.Empty;
        public string Difficulty { get; set; } = string.Empty;
        public int LineLength { get; set; }
        public char[,] Board { get; set; }
        public string CurrentPlayer { get; set; } = string.Empty;
        public Dictionary<string, char> SymbolMap { get; set; } = new();
        public MathTask? CurrentTask { get; set; }
    }

    public class MathTask
    {
        public string Expression { get; set; } = string.Empty;
        public int Answer { get; set; }
    }
}
