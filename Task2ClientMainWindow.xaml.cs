using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace MessengerApp
{
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

    public class RegisterRequestData
    {
        public string Login { get; set; } = null!;
        public string Password { get; set; } = null!;
        public string Name { get; set; } = null!;
    }

    public class MessagePacket
    {
        public MessageType Type { get; set; }
        public string Data { get; set; } = null!;
        public string SenderName { get; set; } = null!;
        public string ReceiverName { get; set; } = null!;
    }

    public partial class MainWindow : Window
    {
        private TcpClient _client = null!;
        private NetworkStream _stream = null!;
        private string _currentUserName = "Я";
        private bool _isAuthenticated = false;
        private string _targetReceiver = "Загальний чат";

        public MainWindow()
        {
            InitializeComponent();

            chatNameTextBlock.Text = "Чат: Загальний чат";
            chatNameTextBox.Text = "Загальний чат";

            chatList.Items.Add("Загальний чат");

            Loaded += MainWindow_Loaded;

            chatList.SelectionChanged += ChatList_SelectionChanged;
        }

        private void ChatList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (chatList.SelectedItem == null) return;

            _targetReceiver = chatList.SelectedItem.ToString()!;
            chatNameTextBlock.Text = $"Чат: {_targetReceiver}";
            chatNameTextBox.Text = _targetReceiver;

            LoadChatHistory(_targetReceiver);

            userList.Items.Clear();
            if (_targetReceiver == "Загальний чат")
            {
                userList.Items.Add("Всі користувачі");
            }
            else
            {
                userList.Items.Add(_targetReceiver);
                userList.Items.Add(_currentUserName);
            }
        }

        private void LoadChatHistory(string chatName)
        {
            messageList.Items.Clear();
            string fileName = $"chat_{chatName.Replace(" ", "_")}.txt";

            if (File.Exists(fileName))
            {
                var lines = File.ReadAllLines(fileName);
                foreach (var line in lines)
                {
                    messageList.Items.Add(line);
                }
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            string host = "127.0.0.1";
            int port = 8888;

            _client = new TcpClient();
            try
            {
                await _client.ConnectAsync(host, port);
                _stream = _client.GetStream();

                _ = ReceivePacketAsync(_stream);

                while (!_isAuthenticated)
                {
                    string choice = Microsoft.VisualBasic.Interaction.InputBox(
                        "Оберіть дію:\n1 - Увійти (Авторизація)\n2 - Зареєструватися",
                        "Вітаємо в Messenger",
                        "1");

                    if (choice == "2")
                    {
                        string regLogin = Microsoft.VisualBasic.Interaction.InputBox("Придумайте логін:", "Реєстрація - Логін", "");
                        string regPassword = Microsoft.VisualBasic.Interaction.InputBox("Придумайте пароль:", "Реєстрація - Пароль", "");
                        string regName = Microsoft.VisualBasic.Interaction.InputBox("Введіть ваше відображуване ім'я (Нікнейм):", "Реєстрація - Ім'я", "");

                        if (string.IsNullOrEmpty(regLogin) || string.IsNullOrEmpty(regPassword) || string.IsNullOrEmpty(regName))
                        {
                            MessageBox.Show("Усі поля реєстрації мають бути заповнені!", "Помилка");
                            continue;
                        }

                        var regData = new RegisterRequestData { Login = regLogin, Password = regPassword, Name = regName };
                        var regPacket = new MessagePacket
                        {
                            Type = MessageType.RegisterRequest,
                            Data = JsonSerializer.Serialize(regData)
                        };

                        await SendPacketAsync(_stream, regPacket);
                        await Task.Delay(1500);
                    }
                    else if (choice == "1")
                    {
                        string login = Microsoft.VisualBasic.Interaction.InputBox("Введіть логін:", "Авторизація", "admin");
                        string password = Microsoft.VisualBasic.Interaction.InputBox("Введіть пароль:", "Авторизація", "111");

                        if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
                        {
                            Application.Current.Shutdown();
                            return;
                        }

                        var loginData = new LoginRequestData { Login = login, Password = password };
                        var authPacket = new MessagePacket
                        {
                            Type = MessageType.LoginRequest,
                            Data = JsonSerializer.Serialize(loginData),
                        };

                        await SendPacketAsync(_stream, authPacket);
                        await Task.Delay(1500);
                    }
                    else
                    {
                        Application.Current.Shutdown();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не вдалося підключитися до сервера: {ex.Message}", "Помилка з'єднання");
                Application.Current.Shutdown();
            }
        }

        private async void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            await ProcessSendMessage();
        }

        private async void messageTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await ProcessSendMessage();
            }
        }

        private async Task ProcessSendMessage()
        {
            string input = messageTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input) || _stream == null) return;

            var textPacket = new MessagePacket
            {
                Type = MessageType.TextMessage,
                Data = input,
                ReceiverName = _targetReceiver
            };

            await SendPacketAsync(_stream, textPacket);

            messageList.Items.Add($"Я: {input}");
            SaveMessageToLocalHistory(_targetReceiver, input, isMine: true);

            messageTextBox.Clear();
            messageTextBox.Focus();
        }

        private async Task ReceivePacketAsync(NetworkStream stream)
        {
            try
            {
                while (true)
                {
                    byte[] lengthBuffer = new byte[4];
                    await stream.ReadExactlyAsync(lengthBuffer, 0, 4);
                    int length = BitConverter.ToInt32(lengthBuffer, 0);

                    byte[] payloadBuffer = new byte[length];
                    await stream.ReadExactlyAsync(payloadBuffer, 0, length);

                    string json = Encoding.UTF8.GetString(payloadBuffer);
                    var packet = JsonSerializer.Deserialize<MessagePacket>(json);

                    if (packet == null) continue;

                    Dispatcher.Invoke(() =>
                    {
                        switch (packet.Type)
                        {
                            case MessageType.LoginResponse:
                                _isAuthenticated = true;
                                _currentUserName = packet.SenderName;
                                userNameTextBlock.Text = $"Користувач: {_currentUserName}";
                                userStatusTextBlock.Text = "Статус: В мережі";
                                break;

                            case MessageType.Error:
                                MessageBox.Show(packet.Data, "Помилка сервера");
                                break;

                            case MessageType.TextMessage:
                                string chatWindow = (packet.ReceiverName == "Загальний чат" || string.IsNullOrEmpty(packet.ReceiverName))
                                    ? "Загальний чат"
                                    : packet.ReceiverName;

                                if (!chatList.Items.Contains(chatWindow) && packet.SenderName != _currentUserName)
                                {
                                    chatList.Items.Add(chatWindow);
                                }

                                if (_targetReceiver == chatWindow)
                                {
                                    messageList.Items.Add($"{packet.SenderName}: {packet.Data}");
                                }

                                SaveMessageToLocalHistory(chatWindow, packet.Data, isMine: (packet.SenderName == _currentUserName), packet.SenderName);
                                break;

                            case MessageType.UserListResponse:
                                break;

                            case MessageType.RemoveUserFromRoom:
                                string roomNameForExile = packet.Data;

                                if (_targetReceiver == roomNameForExile)
                                {
                                    _targetReceiver = "Загальний чат";
                                    chatNameTextBlock.Text = "Чат: Загальний чат";
                                    chatNameTextBox.Text = "Загальний чат";
                                    LoadChatHistory("Загальний чат");
                                }

                                if (chatList.Items.Contains(roomNameForExile))
                                {
                                    chatList.Items.Remove(roomNameForExile);
                                }

                                MessageBox.Show($"Вас було вилучено з кімнати/чату '{roomNameForExile}'.", "Доступ обмежено", MessageBoxButton.OK, MessageBoxImage.Information);
                                break;

                            case MessageType.RegisterResponse:
                                MessageBox.Show(packet.Data, "Реєстрація");
                                break;
                        }
                    });
                }
            }
            catch (EndOfStreamException)
            {
                Dispatcher.Invoke(() => MessageBox.Show("Сервер закрив підключення.", "Зв'язок розірвано"));
                Application.Current.Shutdown();
            }
            catch (Exception)
            {
                Dispatcher.Invoke(() => MessageBox.Show("Сталася помилка з'єднання на стороні клієнта.", "Помилка"));
                Application.Current.Shutdown();
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

        private static void SaveMessageToLocalHistory(string chatName, string text, bool isMine, string senderName = "")
        {
            try
            {
                string fileName = $"chat_{chatName.Replace(" ", "_")}.txt";
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string author = isMine ? "Я" : senderName;
                string logEntry = $"{timestamp} {author}: {text}{Environment.NewLine}";
                File.AppendAllText(fileName, logEntry);
            }
            catch { }
        }

        private async void AddChatButton_Click(object sender, RoutedEventArgs e)
        {
            string mode = Microsoft.VisualBasic.Interaction.InputBox(
                "Оберіть дію:\n1 - Додати особистий чат (Користувача)\n2 - Створити групову кімнату",
                "Контактна книга", "1").Trim();

            if (string.IsNullOrEmpty(mode)) return;

            if (mode == "1")
            {
                string personName = Microsoft.VisualBasic.Interaction.InputBox("Введіть ім'я користувача для особистого спілкування:", "Додати контакт", "").Trim();
                if (string.IsNullOrEmpty(personName) || personName == _currentUserName) return;

                if (!chatList.Items.Contains(personName))
                {
                    chatList.Items.Add(personName);
                }
                chatList.SelectedItem = personName;
            }
            else if (mode == "2")
            {
                string roomName = Microsoft.VisualBasic.Interaction.InputBox("Введіть назву для нової групової кімнати:", "Створення групи", "Нова Кімната").Trim();
                if (string.IsNullOrEmpty(roomName) || roomName == "Загальний чат") return;

                var createPacket = new MessagePacket { Type = MessageType.CreateRoom, Data = roomName };
                await SendPacketAsync(_stream, createPacket);

                if (!chatList.Items.Contains(roomName))
                {
                    chatList.Items.Add(roomName);
                }
                chatList.SelectedItem = roomName;
            }
        }

        private async void DeleteChatRoomButton_Click(object sender, RoutedEventArgs e)
        {
            if (chatList.SelectedItem == null)
            {
                MessageBox.Show("Виберіть чат із контактної книги ліворуч для видалення.", "Помилка");
                return;
            }

            string selectedChat = chatList.SelectedItem.ToString()!;
            if (selectedChat == "Загальний чат")
            {
                MessageBox.Show("Загальний чат заборонено видаляти!", "Помилка");
                return;
            }

            var result = MessageBox.Show($"Ви дійсно хочете видалити чат/групу '{selectedChat}' з вашої контактної книги?", "Підтвердження", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                if (_stream != null)
                {
                    await SendPacketAsync(_stream, new MessagePacket { Type = MessageType.DeleteRoom, Data = selectedChat });
                }

                chatList.Items.Remove(selectedChat);
                chatList.SelectedItem = "Загальний чат";
            }
        }

        private async void AddUserToChat_Click(object sender, RoutedEventArgs e)
        {
            if (_targetReceiver == "Загальний чат")
            {
                MessageBox.Show("У загальний чат не можна примусово додавати користувачів.", "Заборонено");
                return;
            }

            string userToAdd = Microsoft.VisualBasic.Interaction.InputBox(
                $"Введіть ім'я користувача, якого хочете запросити у групу '{_targetReceiver}':",
                "Запросити учасника", "").Trim();

            if (!string.IsNullOrEmpty(userToAdd) && _stream != null)
            {
                var addUserPacket = new MessagePacket
                {
                    Type = MessageType.AddUserToRoom,
                    Data = $"{_targetReceiver}|{userToAdd}"
                };
                await SendPacketAsync(_stream, addUserPacket);

                if (!userList.Items.Contains(userToAdd))
                {
                    userList.Items.Add(userToAdd);
                }
            }
        }

        private async void RemoveUserFromChat_Click(object sender, RoutedEventArgs e)
        {
            if (_targetReceiver == "Загальний чат")
            {
                MessageBox.Show("З загального чату не можна виганяти учасників.", "Заборонено");
                return;
            }

            string userToRemove = Microsoft.VisualBasic.Interaction.InputBox(
                $"Введіть ім'я користувача, якого потрібно вилучити з групи '{_targetReceiver}':",
                "Видалити учасника", "").Trim();

            if (!string.IsNullOrEmpty(userToRemove) && _stream != null)
            {
                var removeUserPacket = new MessagePacket
                {
                    Type = MessageType.RemoveUserFromRoom,
                    Data = $"{_targetReceiver}|{userToRemove}"
                };
                await SendPacketAsync(_stream, removeUserPacket);

                if (userList.Items.Contains(userToRemove))
                {
                    userList.Items.Remove(userToRemove);
                }
            }
        }
    }
}
