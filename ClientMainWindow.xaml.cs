using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using static System.Runtime.CompilerServices.RuntimeHelpers;

namespace MessengerApp
{
    public enum MessageType { LoginRequest, LoginResponse, TextMessage, Error, UserListRequest, UserListResponse, CreateRoom, AddUserToRoom, DeleteRoom, RemoveUserFromRoom }

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
            chatList.Items.Add("Загальний чат");

            Loaded += MainWindow_Loaded;

            chatList.SelectionChanged += ChatList_SelectionChanged;
            userList.SelectionChanged += UserList_SelectionChanged;

        }

        private void ChatList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (chatList.SelectedItem == null) return;

            _targetReceiver = chatList.SelectedItem.ToString()!;
            chatNameTextBlock.Text = $"Чат: {_targetReceiver}";

            LoadChatHistory(_targetReceiver);
        }

        private void UserList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (userList.SelectedItem == null) return;

            string targetUser = userList.SelectedItem.ToString()!;

            if (!chatList.Items.Contains(targetUser))
            {
                chatList.Items.Add(targetUser);
            }

            chatList.SelectedItem = targetUser;
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
                    await Task.Delay(1000); 
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

            var textPacket = new MessagePacket { Type = MessageType.TextMessage, 
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
                                userList.Items.Add(_currentUserName);
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
                                var users = JsonSerializer.Deserialize<string[]>(packet.Data);
                                if (users == null) break;
                                userList.Items.Clear();
                                foreach (var user in users)
                                {
                                    if(user != _currentUserName) userList.Items.Add(user);
                                }
                                break;
                            case MessageType.RemoveUserFromRoom:
                                string roomNameForExile = packet.Data;

                                if (_targetReceiver == roomNameForExile)
                                {
                                    _targetReceiver = "Загальний чат";
                                    chatNameTextBlock.Text = "Чат: Загальний чат";
                                    LoadChatHistory("Загальний чат");
                                }

                                if (chatList.Items.Contains(roomNameForExile))
                                {
                                    chatList.Items.Remove(roomNameForExile);
                                }

                                MessageBox.Show($"Вас було видалено з групи '{roomNameForExile}'.", "Доступ обмежено", MessageBoxButton.OK, MessageBoxImage.Information);
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
            catch {}
        }

        private async void AddChatButton_Click(object sender, RoutedEventArgs e)
        {
            string roomName = Microsoft.VisualBasic.Interaction.InputBox(
                "Введіть назву для нової групи (кімнати) або ім'я користувача:",
                "Створення чату/кімнати",
                "Нова Кімната"
            ).Trim();

            if (string.IsNullOrEmpty(roomName) || roomName == "Загальний чат") return;

            var createPacket = new MessagePacket
            {
                Type = MessageType.CreateRoom,
                Data = roomName
            };
            await SendPacketAsync(_stream, createPacket);

            string userToAdd = Microsoft.VisualBasic.Interaction.InputBox(
                $"Введіть ім'я користувача, якого хочете додати в чат '{roomName}':",
                "Запросити в кімнату",
                ""
            ).Trim();

            if (!string.IsNullOrEmpty(userToAdd))
            {
                var addUserPacket = new MessagePacket
                {
                    Type = MessageType.AddUserToRoom,
                    Data = $"{roomName}|{userToAdd}"
                };
                await SendPacketAsync(_stream, addUserPacket);
            }
            if (!chatList.Items.Contains(roomName))
            {
                chatList.Items.Add(roomName);
            }
            chatList.SelectedItem = roomName;
        }

        private async void AddUserToChat_Click(object sender, RoutedEventArgs e)
        {
            string userToAdd = Microsoft.VisualBasic.Interaction.InputBox(
                $"Введіть ім'я користувача, якого хочете додати в чат '{chatNameTextBlock}':",
                "Запросити в кімнату",
                ""
            ).Trim();

            if (!string.IsNullOrEmpty(userToAdd))
            {
                var addUserPacket = new MessagePacket
                {
                    Type = MessageType.AddUserToRoom,
                    Data = $"{chatNameTextBlock}|{userToAdd}"
                };
                await SendPacketAsync(_stream, addUserPacket);
            }
        }

        private async void DeleteChatRoomButton_Click(object sender, RoutedEventArgs e)
        {
            if (chatList.SelectedItem == null)
            {
                MessageBox.Show("Виберіть чат, який бажаєте видалити.", "Помилка");
                return;
            }

            string selectedChat = chatList.SelectedItem.ToString();

            if(selectedChat == "Загальний чат")
            {
                MessageBox.Show("Загальний чат заборонено видаляти!");
                return;
            }

            var result = MessageBox.Show($"Ви дійсно хочете видалити групу/чат '{selectedChat}' для всіх учасників?",
                                 "Підтвердження видалення",
                                 MessageBoxButton.YesNo,
                                 MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                if (_stream != null)
                {
                    var deletePacket = new MessagePacket
                    {
                        Type = MessageType.DeleteRoom,
                        Data = selectedChat 
                    };
                    await SendPacketAsync(_stream, deletePacket);
                }

                chatList.Items.Remove(selectedChat);
                chatList.SelectedItem = "Загальний чат";
            }
        }
        private async void DeleteChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (chatList.SelectedItem == null)
            {
                MessageBox.Show("Виберіть чат або групу для взаємодії.", "Помилка");
                return;
            }

            string selectedChat = chatList.SelectedItem.ToString()!;

            if (selectedChat == "Загальний чат")
            {
                MessageBox.Show("Загальний чат не можна редагувати чи видалити!", "Заборонено");
                return;
            }

            var dialogResult = MessageBox.Show(
                $"Бажаєте ПОВНІСТЮ ВИДАЛИТИ групу '{selectedChat}'?\n\n(Натисніть 'Ні', щоб просто видалити (вигнати) конкретного учасника з цієї групи)",
                "Керування групою",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (dialogResult == MessageBoxResult.Yes)
            {
                if (_stream != null)
                {
                    var deletePacket = new MessagePacket { Type = MessageType.DeleteRoom, Data = selectedChat };
                    await SendPacketAsync(_stream, deletePacket);
                }
                chatList.Items.Remove(selectedChat);
                chatList.SelectedItem = "Загальний чат";
            }
            else if (dialogResult == MessageBoxResult.No)
            {
                string userToRemove = Microsoft.VisualBasic.Interaction.InputBox(
                    $"Введіть точне ім'я користувача, якого хочете видалити з групи '{selectedChat}':",
                    "Видалення учасника",
                    ""
                ).Trim();

                if (string.IsNullOrEmpty(userToRemove)) return;

                if (_stream != null)
                {
                    var removeUserPacket = new MessagePacket
                    {
                        Type = MessageType.RemoveUserFromRoom,
                        Data = $"{selectedChat}|{userToRemove}"
                    };
                    await SendPacketAsync(_stream, removeUserPacket);
                }
            }
        }
    }
}
