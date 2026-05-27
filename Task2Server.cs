using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class User
{
    public int Id { get; set; }
    public string Login { get; set; } = null!;
    public string Password { get; set; } = null!;
    public string Name { get; set; } = null!;
}

public class RegisterRequestData
{
    public string Login { get; set; } = null!;
    public string Password { get; set; } = null!;
    public string Name { get; set; } = null!;
}

public enum MessageType
{
    LoginRequest,
    LoginResponse,
    TextMessage,
    Error,
    UserListRequest,
    UserListResponse,
    CreateRoom,
    AddUserToRoom,
    DeleteRoom,
    RemoveUserFromRoom,
    RegisterRequest,   
    RegisterResponse
}

public class LoginRequestData
{
    public string Login { get; set; } = null!;
    public string Password { get; set; } = null!;
}

public class MessagePacket
{
    public MessageType Type { get; set; }
    public string Data { get; set; } = null!;
    public string SenderName { get; set; } = null!;
    public string ReceiverName { get; set; } = null!;
}

class TcpMessengerServer
{
    private static readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private static UserManager _userManager = null!;
    private static readonly ConcurrentDictionary<string, List<int>> _rooms = new();

    static async Task Main(string[] args)
    {
        _userManager = new UserManager();

        int port = 8888;
        TcpListener listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"[Server] працює на порту {port}...");

        try
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync();
                _ = HandleClientAsync(client);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        User? authenticatedUser = null;
        using (client)
        await using (NetworkStream stream = client.GetStream())
        {
            try
            {
                while (authenticatedUser == null)
                {
                    MessagePacket? packet = await ReceivePacketAsync(stream);
                    if (packet == null) return;

                    if (packet.Type == MessageType.RegisterRequest)
                    {
                        var regData = JsonSerializer.Deserialize<RegisterRequestData>(packet.Data);
                        if (regData == null) continue;

                        string resultMessage = _userManager.RegisterNewUser(regData.Login, regData.Password, regData.Name);

                        await SendPacketAsync(stream, new MessagePacket
                        {
                            Type = MessageType.RegisterResponse,
                            Data = resultMessage
                        });
                    }
                    else if (packet.Type == MessageType.LoginRequest)
                    {
                        var loginData = JsonSerializer.Deserialize<LoginRequestData>(packet.Data);
                        if (loginData == null) continue;

                        authenticatedUser = _userManager.Authenticate(loginData.Login, loginData.Password);
                        
                        if (authenticatedUser != null)
                        {
                            if (_clients.ContainsKey(authenticatedUser.Id))
                            {
                                await SendPacketAsync(stream, new MessagePacket { Type = MessageType.Error, Data = "Цей користувач вже в мережі" });
                                authenticatedUser = null;
                                continue;
                            }

                            await SendPacketAsync(stream, new MessagePacket { Type = MessageType.LoginResponse, Data = "Успішно авторизовано", SenderName = authenticatedUser.Name });
                            
                            _clients.TryAdd(authenticatedUser.Id, client);
                            Console.WriteLine($"[SERVER] User: {authenticatedUser.Name} (ID: {authenticatedUser.Id}) тепер в мережі");

                            await BroadcastUsersListAsync();
                        }
                        else
                        {
                            await SendPacketAsync(stream, new MessagePacket { Type = MessageType.Error, Data = "Невірний логін або пароль" });
                        }
                    }
                }

                while (true)
                {
                    MessagePacket? packet = await ReceivePacketAsync(stream);
                    if (packet == null) break;

                    if (packet.Type == MessageType.CreateRoom)
                    {
                        string roomName = CleanWpfMetadata(packet.Data);
                        _rooms.GetOrAdd(roomName, new List<int> { authenticatedUser.Id });
                        Console.WriteLine($"[SERVER] Кімната '{roomName}' створена користувачем {authenticatedUser.Name}");
                    }
                    else if (packet.Type == MessageType.AddUserToRoom)
                    {
                        var parts = packet.Data.Split('|');
                        if (parts.Length == 2)
                        {
                            string roomName = CleanWpfMetadata(parts[0]);
                            string targetUserName = parts[1].Trim();

                            var targetUser = _userManager.GetAllUsers().FirstOrDefault(u => u.Name.Equals(targetUserName, StringComparison.OrdinalIgnoreCase));
                            if (targetUser != null && _rooms.TryGetValue(roomName, out var userIds))
                            {
                                lock (userIds)
                                {
                                    if (!userIds.Contains(targetUser.Id))
                                    {
                                        userIds.Add(targetUser.Id);
                                    }
                                }
                                Console.WriteLine($"[SERVER] {targetUserName} доданий в кімнату '{roomName}'");
                            }
                        }
                    }
                    else if (packet.Type == MessageType.TextMessage)
                    {
                        string cleanReceiver = CleanWpfMetadata(packet.ReceiverName);
                        packet.ReceiverName = cleanReceiver;

                        if (cleanReceiver == "Загальний чат" || string.IsNullOrEmpty(cleanReceiver))
                        {
                            Console.WriteLine($"[Загальний чат] {authenticatedUser.Name}: {packet.Data}");
                            await BroadcastMessageAsync(authenticatedUser.Id, packet, authenticatedUser.Name);
                        }
                        else if (_rooms.ContainsKey(cleanReceiver))
                        {
                            Console.WriteLine($"[Група '{cleanReceiver}'] {authenticatedUser.Name}: {packet.Data}");
                            await SendGroupMessageAsync(packet, authenticatedUser.Name);
                        }
                        else
                        {
                            Console.WriteLine($"[Приват] {authenticatedUser.Name} -> {cleanReceiver}: {packet.Data}");
                            await SendPrivateMessageAsync(packet, authenticatedUser.Name);
                        }
                    }
                    else if (packet.Type == MessageType.DeleteRoom)
                    {
                        string roomName = CleanWpfMetadata(packet.Data);

                        if (_rooms.TryGetValue(roomName, out var userIds))
                        {
                            List<int> membersToNotify;
                            lock (userIds)
                            {
                                membersToNotify = new List<int>(userIds);
                            }

                            _rooms.TryRemove(roomName, out _);
                            Console.WriteLine($"[SERVER] Кімната '{roomName}' була видалена користувачем {authenticatedUser.Name}");

                            foreach (int userId in membersToNotify)
                            {
                                if (_clients.TryGetValue(userId, out var memberClient))
                                {
                                    try
                                    {
                                        NetworkStream clientStream = memberClient.GetStream();
                                        await SendPacketAsync(clientStream, new MessagePacket
                                        {
                                            Type = MessageType.DeleteRoom,
                                            Data = roomName
                                        });
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    else if (packet.Type == MessageType.RemoveUserFromRoom)
                    {
                        var parts = packet.Data.Split('|');
                        if (parts.Length == 2)
                        {
                            string roomName = CleanWpfMetadata(parts[0]);
                            string targetUserName = parts[1].Trim();

                            var targetUser = _userManager.GetAllUsers().FirstOrDefault(u => u.Name.Equals(targetUserName, StringComparison.OrdinalIgnoreCase));
        
                            if (targetUser != null && _rooms.TryGetValue(roomName, out var userIds))
                            {
                                bool removed = false;
                                lock (userIds)
                                {
                                    if (userIds.Contains(targetUser.Id))
                                    {
                                        userIds.Remove(targetUser.Id);
                                        removed = true;
                                    }
                                }

                                if (removed)
                                {
                                    Console.WriteLine($"[SERVER] Користувача {targetUserName} видалено з кімнати '{roomName}'");

                                    if (_clients.TryGetValue(targetUser.Id, out var expelledClient))
                                    {
                                        try
                                        {
                                            NetworkStream clientStream = expelledClient.GetStream();
                                            await SendPacketAsync(clientStream, new MessagePacket
                                            {
                                                Type = MessageType.RemoveUserFromRoom,
                                                Data = roomName 
                                            });
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Сталася помилка з клієнтом: {ex.Message}");
            }
            finally
            {
                if (authenticatedUser != null)
                {
                    _clients.TryRemove(authenticatedUser.Id, out _);
                    Console.WriteLine($"[Server] User: {authenticatedUser.Name} (ID: {authenticatedUser.Id}) вийшов");
                    await BroadcastUsersListAsync();
                }
            }
        }
    }
    private static string CleanWpfMetadata(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (input.Contains(":"))
        {
            var parts = input.Split(new[] { ':' }, 2);
            if (parts[0].Contains("System.Windows.Controls"))
            {
                return parts[1].Trim();
            }
        }
        return input.Trim();
    }
    
    private static async Task SendGroupMessageAsync(MessagePacket packet, string senderName)
    {
        packet.SenderName = senderName;

        if (_rooms.TryGetValue(packet.ReceiverName, out var userIds))
        {
            List<int> targets;
            lock (userIds)
            {
                targets = new List<int>(userIds);
            }

            foreach (int userId in targets)
            {
                if (_clients.TryGetValue(userId, out var client))
                {
                    var user = _userManager.GetUserById(userId);
                    if (user != null && user.Name == senderName) continue;

                    try
                    {
                        NetworkStream clientStream = client.GetStream();
                        await SendPacketAsync(clientStream, packet);
                    }
                    catch { }
                }
            }
        }
    }
    private static async Task BroadcastUsersListAsync()
    {
        var onlineUsersNames = new List<string>();
        foreach (var clientId in _clients.Keys)
        {
            var user = _userManager.GetUserById(clientId);
            if (user != null) onlineUsersNames.Add(user.Name);
        }

        var usersListPacket = new MessagePacket
        {
            Type = MessageType.UserListResponse,
            Data = JsonSerializer.Serialize(onlineUsersNames)
        };

        foreach (var pair in _clients)
        {
            try
            {
                NetworkStream clientStream = pair.Value.GetStream();
                await SendPacketAsync(clientStream, usersListPacket);
            }
            catch { }
        }
    }

    private static async Task BroadcastMessageAsync(int senderId, MessagePacket packet, string senderName)
    {
        packet.SenderName = senderName;

        foreach (var pair in _clients)
        {
            if (pair.Key == senderId)
                continue;

            try
            {
                NetworkStream clientStream = pair.Value.GetStream();
                await SendPacketAsync(clientStream, packet);
            }
            catch { }
        }
    }

    private static async Task SendPrivateMessageAsync(MessagePacket packet, string senderName)
    {
        packet.SenderName = senderName;

        foreach (var pair in _clients)
        {
            var user = _userManager.GetUserById(pair.Key);

            if (user != null && user.Name.Equals(packet.ReceiverName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    NetworkStream clientStream = pair.Value.GetStream();
                    await SendPacketAsync(clientStream, packet);
                }
                catch { }
                return;
            }
        }
    }

    private static async Task SendPacketAsync(NetworkStream stream, MessagePacket packet)
    {
        string json = JsonSerializer.Serialize(packet);
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] lengthPrefix = BitConverter.GetBytes(payload.Length);

        await stream.WriteAsync(lengthPrefix, 0, 4);
        await stream.WriteAsync(payload, 0, payload.Length);
        await stream.FlushAsync();
    }

    private static async Task<MessagePacket?> ReceivePacketAsync(NetworkStream stream)
    {
        try
        {
            byte[] lengthBuffer = new byte[4];
            await stream.ReadExactlyAsync(lengthBuffer, 0, 4);
            int length = BitConverter.ToInt32(lengthBuffer, 0);
            
            byte[] payloadBuffer = new byte[length];
            await stream.ReadExactlyAsync(payloadBuffer, 0, length);
            string json = Encoding.UTF8.GetString(payloadBuffer);

            return JsonSerializer.Deserialize<MessagePacket>(json);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public class UserManager
{
    private const string FilePath = "users.json";
    private List<User> _users = new();
    
    public List<User> GetAllUsers()
    {
        return _users;
    }

    public UserManager()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                string json = File.ReadAllText(FilePath);
                _users = JsonSerializer.Deserialize<List<User>>(json) ?? new List<User>();
                
                if (_users.Count == 0) InitializeDefaultUser();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Critical] Не вдалося прочитати {FilePath}: {ex.Message}");
                InitializeDefaultUser();
            }
        }
        else
        {
            InitializeDefaultUser();
        }
    }

    private void InitializeDefaultUser()
    {
        _users = new List<User>
        {
            new User { Id = 1, Login = "admin", Password = "111", Name = "Адміністратор" },
            new User { Id = 2, Login = "user", Password = "222", Name = "Іван" }
        };
        string json = JsonSerializer.Serialize(_users, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }

    public User? Authenticate(string login, string password)
    {
        return _users.FirstOrDefault(u => u.Login == login && u.Password == password);
    }

    public List<string> GetAllUsersNames()
    {
        return _users.Select(u => u.Name).ToList();
    }

    public User? GetUserById(int id)
    {
        return _users.FirstOrDefault(u => u.Id == id);
    }
    
    public string RegisterNewUser(string login, string password, string name)
    {
        if (_users.Any(u => u.Login.Equals(login, StringComparison.OrdinalIgnoreCase)))
        {
            return "Помилка: Користувач з таким логіном уже існує!";
        }
        if (_users.Any(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return "Помилка: Користувач з таким ім'ям (ніком) уже існує!";
        }
        int newId = _users.Count > 0 ? _users.Max(u => u.Id) + 1 : 1;

        User newUser = new User { Id = newId, Login = login, Password = password, Name = name };
        _users.Add(newUser);

        try
        {
            string json = JsonSerializer.Serialize(_users, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
            return "Успіх: Користувача успішно зареєстровано! Тепер ви можете увійти.";
        }
        catch (Exception ex)
        {
            return $"[Критична помилка збереження]: {ex.Message}";
        }
    }
}
