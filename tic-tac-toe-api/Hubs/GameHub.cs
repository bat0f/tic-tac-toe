using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;

namespace tic_tac_toe_api.Hubs
{
    [Authorize]
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
                    UsedTasks = new HashSet<string>(),
                    StartTime = DateTime.UtcNow
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

            if (DateTime.UtcNow.Subtract(session.StartTime).TotalMinutes >= 15)
            {
                _logger.LogInformation($"Игра {gameId} завершена из-за истечения времени (15 минут). Ничья!");
                activeGames.Remove(gameId);
                await Clients.Group(gameId).SendAsync("GameOver", null, "draw", null, 0, 0, 0, 0);
                return;
            }

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

            // Генерируем задачу, если её нет или это начало нового раунда
            if (session.RoundMoves == 0 || string.IsNullOrEmpty(session.RoundTask))
            {
                session.RoundTask = GenerateUniqueTask(session.Difficulty, session.UsedTasks);
                session.RoundAnswer = EvaluateTask(session.RoundTask);
                _logger.LogInformation($"Новая задача раунда: {session.RoundTask}, Ответ: {session.RoundAnswer}");
            }
            else
            {
                _logger.LogInformation($"Используем задачу раунда: {session.RoundTask}, Ответ: {session.RoundAnswer}");
            }

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

            if (DateTime.UtcNow.Subtract(session.StartTime).TotalMinutes >= 15)
            {
                _logger.LogInformation($"Игра {gameId} завершена из-за истечения времени (15 минут). Ничья!");
                activeGames.Remove(gameId);
                await Clients.Group(gameId).SendAsync("GameOver", null, "draw", null, 0, 0, 0, 0);
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

                var (hasWin, winLineType, startX, startY, endX, endY) = CheckWin(session, row, col, symbol);
                if (hasWin || CheckDraw(session))
                {
                    if (hasWin)
                    {
                        _logger.LogInformation($"{username} победил! Выигрышная линия: {winLineType} от ({startX},{startY}) до ({endX},{endY})");
                        activeGames.Remove(gameId);
                        await Clients.Group(gameId).SendAsync("GameOver", username, "win", winLineType, startX, startY, endX, endY);
                    }
                    else
                    {
                        _logger.LogInformation("Ничья!");
                        activeGames.Remove(gameId);
                        await Clients.Group(gameId).SendAsync("GameOver", null, "draw", null, 0, 0, 0, 0);
                    }
                }
                else
                {
                    var nextPlayer = string.Equals(username, session.Player1, StringComparison.OrdinalIgnoreCase) ? session.Player2 : session.Player1;
                    session.CurrentPlayer = nextPlayer;
                    session.SelectedRow = -1;
                    session.SelectedCol = -1;
                    session.RoundMoves++;
                    if (session.RoundMoves >= 2)
                    {
                        session.RoundTask = "";
                        session.RoundAnswer = 0;
                        session.RoundMoves = 0;
                    }
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
                if (session.RoundMoves >= 2)
                {
                    session.RoundTask = "";
                    session.RoundAnswer = 0;
                    session.RoundMoves = 0;
                }
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

            if (DateTime.UtcNow.Subtract(session.StartTime).TotalMinutes >= 15)
            {
                _logger.LogInformation($"Игра {gameId} завершена из-за истечения времени (15 минут). Ничья!");
                activeGames.Remove(gameId);
                await Clients.Group(gameId).SendAsync("GameOver", null, "draw", null, 0, 0, 0, 0);
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

            // Если это первый ход в раунде, генерируем новую задачу
            if (session.RoundMoves == 1 || string.IsNullOrEmpty(session.RoundTask))
            {
                session.RoundTask = GenerateUniqueTask(session.Difficulty, session.UsedTasks);
                session.RoundAnswer = EvaluateTask(session.RoundTask);
                _logger.LogInformation($"Новая задача после пропуска: {session.RoundTask}, Ответ: {session.RoundAnswer}");
            }

            // Сбрасываем задачу и счётчик ходов, если раунд завершён
            if (session.RoundMoves >= 2)
            {
                session.RoundTask = "";
                session.RoundAnswer = 0;
                session.RoundMoves = 0;
            }

            _logger.LogInformation($"Передаём ход: {nextPlayer}, выбор клетки");
            await Clients.Group(gameId).SendAsync("UpdateState", session.Board, session.CurrentPlayer, 8, session.RoundTask);
        }

        public async Task ResetSession(string gameId)
        {
            _logger.LogInformation($"ResetSession вызван для gameId={gameId}");
            if (activeGames.TryGetValue(gameId, out var session))
            {
                session.StartTime = DateTime.UtcNow;
                session.Board = CreateEmptyBoard(session.BoardSize);
                session.CurrentPlayer = session.Player1;
                session.SelectedRow = -1;
                session.SelectedCol = -1;
                session.RoundMoves = 0;
                session.RoundTask = "";
                session.RoundAnswer = 0;
                session.UsedTasks.Clear();
                await Clients.Group(gameId).SendAsync("GameReset", gameId, session.Board, session.CurrentPlayer);
                _logger.LogInformation($"Сессия {gameId} сброшена, новое время: {session.StartTime}");
            }
            else
            {
                _logger.LogWarning($"Игра {gameId} не найдена для сброса.");
            }
        }

        private string GenerateUniqueTask(string difficulty, HashSet<string> usedTasks)
        {
            string task;
            int result;
            do
            {
                switch (difficulty.ToLower())
                {
                    case "easy":
                        int num1e = random.Next(1, 101);
                        int num2e = random.Next(1, 101);
                        string opEasy = random.NextDouble() < 0.5 ? "+" : "-";
                        task = $"{num1e} {opEasy} {num2e} =";
                        if (opEasy == "-" && num1e < num2e)
                        {
                            int temp = num1e; num1e = num2e; num2e = temp;
                            task = $"{num1e} {opEasy} {num2e} =";
                        }
                        break;
                    case "medium":
                        if (random.NextDouble() < 0.5)
                        {
                            int num1m = random.Next(2, 21);
                            int num2m = random.Next(2, 21);
                            string opMed = random.NextDouble() < 0.5 ? "×" : "÷";
                            if (opMed == "÷")
                            {
                                while (num1m % num2m != 0) num2m = random.Next(2, 21);
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
                        int num3h = random.Next(1, 101);
                        if (random.NextDouble() < 0.5)
                        {
                            string op1h = new[] { "+", "-", "×", "÷" }[random.Next(4)];
                            string op2h = new[] { "+", "-", "×", "÷" }[random.Next(4)];
                            if (op2h == "÷")
                            {
                                while (num2h % num3h != 0) num3h = random.Next(1, num2h + 1);
                            }
                            if (op1h == "÷")
                            {
                                while (num1h % (EvaluateExpression(new List<string> { num2h.ToString(), op2h, num3h.ToString() })) != 0)
                                {
                                    num2h = random.Next(1, 101);
                                    num3h = random.Next(1, num2h + 1);
                                    if (op2h == "÷")
                                    {
                                        while (num2h % num3h != 0) num3h = random.Next(1, num2h + 1);
                                    }
                                }
                            }
                            task = $"{num1h} {op1h} ({num2h} {op2h} {num3h}) =";
                        }
                        else
                        {
                            string op1h = new[] { "+", "-", "×", "÷" }[random.Next(4)];
                            string op2h = new[] { "+", "-", "×", "÷" }[random.Next(4)];
                            if (op1h == "÷")
                            {
                                while (num1h % num2h != 0) num2h = random.Next(1, num1h + 1);
                            }
                            if (op2h == "÷")
                            {
                                while (num2h % num3h != 0) num3h = random.Next(1, num2h + 1);
                            }
                            task = $"{num1h} {op1h} {num2h} {op2h} {num3h} =";
                        }
                        break;
                    default:
                        task = "1 + 1 =";
                        break;
                }
                result = EvaluateTask(task);
                _logger.LogInformation($"Сгенерирована задача: {task}, Результат: {result}");
            } while (usedTasks.Contains(task) || result < 0);
            usedTasks.Add(task);
            return task;
        }

        private int EvaluateTask(string task)
        {
            string[] parts = task.Split(new[] { ' ', '=' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> tokens = new List<string>(parts);

            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].StartsWith("("))
                {
                    int start = i;
                    int end = start;
                    int bracketCount = 0;
                    for (int j = start; j < tokens.Count; j++)
                    {
                        if (tokens[j].StartsWith("("))
                        {
                            bracketCount++;
                            tokens[j] = tokens[j].Substring(1);
                        }
                        if (tokens[j].EndsWith(")"))
                        {
                            bracketCount--;
                            tokens[j] = tokens[j].Substring(0, tokens[j].Length - 1);
                        }
                        end = j;
                        if (bracketCount == 0) break;
                    }

                    List<string> subTokens = tokens.GetRange(start, end - start + 1);
                    int subResult = EvaluateExpression(subTokens);
                    tokens.RemoveRange(start, end - start + 1);
                    tokens.Insert(start, subResult.ToString());
                    i = start;
                }
            }

            return EvaluateExpression(tokens);
        }

        private int EvaluateExpression(List<string> tokens)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] == "×" || tokens[i] == "÷")
                {
                    int left = int.Parse(tokens[i - 1]);
                    int right = int.Parse(tokens[i + 1]);
                    int result;
                    if (tokens[i] == "×")
                    {
                        result = left * right;
                    }
                    else
                    {
                        if (right == 0) throw new DivideByZeroException("Деление на ноль в выражении.");
                        if (left % right != 0) throw new InvalidOperationException($"Деление не нацело: {left} ÷ {right} = {left / (double)right}");
                        result = left / right;
                    }
                    _logger.LogInformation($"Промежуточный результат: {left} {tokens[i]} {right} = {result}");
                    tokens[i - 1] = result.ToString();
                    tokens.RemoveRange(i, 2);
                    i--;
                }
            }

            int finalResult = int.Parse(tokens[0]);
            for (int i = 1; i < tokens.Count; i += 2)
            {
                int nextNum = int.Parse(tokens[i + 1]);
                if (tokens[i] == "+")
                    finalResult += nextNum;
                else if (tokens[i] == "-")
                    finalResult -= nextNum;
                _logger.LogInformation($"Промежуточный результат: {finalResult} {tokens[i]} {nextNum} = {(tokens[i] == "+" ? finalResult + nextNum : finalResult - nextNum)}");
            }

            return finalResult;
        }

        private (bool hasWin, string winLineType, int startX, int startY, int endX, int endY) CheckWin(GameSession session, int row, int col, char symbol)
        {
            int boardSize = session.BoardSize;
            var board = session.Board;
            int lineLength = session.LineLength;

            _logger.LogInformation($"Проверка победы: row={row}, col={col}, symbol={symbol}, boardSize={boardSize}, lineLength={lineLength}");

            // Направления для проверки: (dx, dy) для горизонтали, вертикали, диагоналей
            int[,] directions = new int[,]
            {
        { 0, 1 },  // Горизонталь
        { 1, 0 },  // Вертикаль
        { 1, 1 },  // Диагональ (левый верх -> правый низ)
        { 1, -1 }  // Антидиагональ (левый низ -> правый верх)
            };
            string[] lineTypes = new[] { "horizontal", "vertical", "diagonal", "anti-diagonal" };

            for (int d = 0; d < directions.GetLength(0); d++)
            {
                int dx = directions[d, 0];
                int dy = directions[d, 1];
                int count = 1; // Начинаем с текущей клетки
                int startX = row, startY = col;
                int endX = row, endY = col;

                // Ищем в положительном направлении
                int i = row + dx;
                int j = col + dy;
                while (i >= 0 && i < boardSize && j >= 0 && j < boardSize && board[i][j] == symbol)
                {
                    endX = i;
                    endY = j;
                    count++;
                    i += dx;
                    j += dy;
                }

                // Ищем в отрицательном направлении
                i = row - dx;
                j = col - dy;
                while (i >= 0 && i < boardSize && j >= 0 && j < boardSize && board[i][j] == symbol)
                {
                    startX = i;
                    startY = j;
                    count++;
                    i -= dx;
                    j -= dy;
                }

                if (count >= lineLength)
                {
                    // Убеждаемся, что start и end не совпадают
                    if (startX == endX && startY == endY)
                    {
                        if (dx == 0) endY = Math.Min(boardSize - 1, startY + lineLength - 1);
                        else if (dy == 0) endX = Math.Min(boardSize - 1, startX + lineLength - 1);
                        else
                        {
                            endX = Math.Min(boardSize - 1, startX + lineLength - 1);
                            endY = startY + (endX - startX) * dy / dx;
                        }
                    }
                    _logger.LogInformation($"Линия найдена ({lineTypes[d]}): от ({startX},{startY}) до ({endX},{endY}), count={count}");
                    return (true, lineTypes[d], startX, startY, endX, endY);
                }
            }

            _logger.LogInformation("Выигрышная линия не найдена.");
            return (false, null, 0, 0, 0, 0);
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
        public DateTime StartTime { get; set; }
    }
}