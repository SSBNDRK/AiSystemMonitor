using AiSystemMonitor.Core;
using AiSystemMonitor.Services;
using LibreHardwareMonitor.Hardware;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AiSystemMonitor
{
    public partial class MainWindow : Window
    {
        #region Змінні
        private bool _isAiThinking = false;
        private bool _isSidebarCollapsed = false;
        private bool _isNetworkSwitching = false;

        private SystemMonitor _sysMonitor;
        private AiEngine _aiEngine; // <--- Підключили новий двигун!
        #endregion

        public MainWindow()
        {
            InitializeComponent();
            InitializeAppAsync();
        }

        #region Ініціалізація та Перемикання Мережі

        private async void InitializeAppAsync()
        {
            _aiEngine = new AiEngine(); // Ініціалізуємо ядро
            AddMessageToChat("TechBro", "Я TechBro. Аналізую систему та перевіряю мережу...", isInteractive: false);

            _sysMonitor = new SystemMonitor();
            _sysMonitor.OnStatsUpdated += (s, args) =>
            {
                Dispatcher.Invoke(() => {
                    CpuProgressBar.Value = args.CpuUsage;
                    CpuText.Text = $"{args.CpuUsage}%";
                    SetProgressBarColor(CpuProgressBar, args.CpuUsage);

                    RamProgressBar.Value = args.RamPercentage;
                    RamText.Text = $"{args.UsedRamGb:F1} / {args.TotalRamGb:F1} GB";
                    SetProgressBarColor(RamProgressBar, args.RamPercentage);

                    GpuProgressBar.Value = args.GpuLoad;
                    GpuText.Text = $"{args.GpuLoad}%";
                    SetProgressBarColor(GpuProgressBar, args.GpuLoad);

                    NetProgressBar.Value = args.NetPercentage;
                    NetText.Text = $"{args.NetMbps:F1} Мбіт/с";
                    SetProgressBarColor(NetProgressBar, args.NetPercentage);

                    TimeText.Text = args.CurrentTime;
                    DateText.Text = args.CurrentDate;
                });
            };
            _sysMonitor.Start();

            bool isOllamaAlive = await _aiEngine.CheckOllamaIsAliveAsync();
            if (isOllamaAlive)
            {
                NetworkToggle.IsChecked = true;
            }
            else
            {
                NetworkToggle.IsChecked = false;
                RebuildAiEngine(false);
            }
        }

        private async void NetworkToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!this.IsLoaded || _isNetworkSwitching) return;
            _isNetworkSwitching = true;
            NetworkToggle.IsEnabled = false;

            bool useLocalNetwork = NetworkToggle.IsChecked == true;
            if (useLocalNetwork)
            {
                AiStatusText.Text = "Перевірка Ollama...";
                bool isOllamaAlive = await _aiEngine.CheckOllamaIsAliveAsync();

                if (!isOllamaAlive)
                {
                    NetworkToggle.Checked -= NetworkToggle_Changed;
                    NetworkToggle.IsChecked = false;
                    NetworkToggle.Checked += NetworkToggle_Changed;

                    AiStatusText.Text = "Помилка мережі";
                    var oldError = ChatPanel.Children.OfType<StackPanel>().FirstOrDefault(p => p.Tag?.ToString() == "OllamaError");
                    if (oldError != null) ChatPanel.Children.Remove(oldError);

                    AddMessageToChat("Система", "⚠️ Локальна мережа Ollama не запущена! Будь ласка, увімкніть Ollama на вашому ПК перед перемиканням.");

                    if (ChatPanel.Children.Count > 0 && ChatPanel.Children[ChatPanel.Children.Count - 1] is StackPanel lastPanel)
                        lastPanel.Tag = "OllamaError";

                    RebuildAiEngine(false);
                    NetworkToggle.IsEnabled = true;
                    _isNetworkSwitching = false;
                    return;
                }
            }

            var resolvedError = ChatPanel.Children.OfType<StackPanel>().FirstOrDefault(p => p.Tag?.ToString() == "OllamaError");
            if (resolvedError != null) ChatPanel.Children.Remove(resolvedError);

            RebuildAiEngine(useLocalNetwork);

            NetworkToggle.IsEnabled = true;
            _isNetworkSwitching = false;
        }

        private void RebuildAiEngine(bool useLocal)
        {
            try
            {
                string apiKey = System.IO.File.Exists("apikey.txt") ? System.IO.File.ReadAllText("apikey.txt").Trim() : "";

                if (!useLocal && string.IsNullOrEmpty(apiKey))
                {
                    AiStatusText.Text = "API ключ не знайдено!";
                    AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F38BA8"));
                    return;
                }

                AiStatusText.Text = useLocal ? "Підключення до Ollama..." : "Підключення до хмари...";

                // Викликаємо побудову моделі в нашому новому сервісі
                _aiEngine.RebuildEngine(useLocal, apiKey);

                AiStatusText.Text = useLocal ? $"Локальна ({_aiEngine.CurrentModelName})" : $"Хмарна ({_aiEngine.CurrentModelName})";
                AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(useLocal ? "#A6E3A1" : "#F9E2AF"));
            }
            catch (Exception ex)
            {
                AiStatusText.Text = "Помилка двигуна!";
                AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F38BA8"));
                AddMessageToChat("Система", $"Збій перемикання мережі: {ex.Message}");
            }
        }
        #endregion

        #region Логіка Чату (UI)

        private async void SendButton_Click(object sender, RoutedEventArgs e) => await ProcessUserMessage();
        private async void UserInputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) await ProcessUserMessage();
        }

        private async Task ProcessUserMessage()
        {
            string userInput = UserInputBox.Text.Trim();
            if (string.IsNullOrEmpty(userInput) || _isAiThinking || _aiEngine == null) return;

            UserInputBox.Text = string.Empty;
            _isAiThinking = true;

            // ПРАПОРЕЦЬ: Чи використовували ми запасний план?
            bool wasFallbackTriggered = false;

            AddMessageToChat("Ти", userInput);
            ScrollToBottom();

            var thinkingPanel = AddMessageToChat("TechBro", "Аналізую запит.", "#6C7086", false);
            ScrollToBottom();

            var border = thinkingPanel.Children.OfType<Border>().FirstOrDefault();
            var grid = border?.Child as Grid;
            var thinkingTextBlock = grid?.Children.OfType<TextBlock>().FirstOrDefault();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            int dotCount = 1;
            timer.Tick += (s, e) =>
            {
                if (thinkingTextBlock != null)
                {
                    dotCount = dotCount > 3 ? 1 : dotCount + 1;
                    thinkingTextBlock.Text = "Аналізую запит" + new string('.', dotCount);
                }
            };
            timer.Start();

            // Чекаємо на відповідь від нейромережі
            AiResponse response = await _aiEngine.ProcessMessageAsync(userInput);

            timer.Stop();

            // ЗАПАСНИЙ ПЛАН
            if (response.IsError && response.Text.Contains("canceled") && NetworkToggle.IsChecked == true)
            {
                wasFallbackTriggered = true; // Запам'ятовуємо, що ми рятували ситуацію!

                if (thinkingTextBlock != null)
                    thinkingTextBlock.Text = "Локалка не впоралась, підключаю хмарні потужності...";

                string apiKey = System.IO.File.Exists("apikey.txt") ? System.IO.File.ReadAllText("apikey.txt").Trim() : "";

                if (!string.IsNullOrEmpty(apiKey))
                {
                    // Вимикаємо обробник подій тимчасово, щоб він не викликав RebuildAiEngine двічі
                    NetworkToggle.Checked -= NetworkToggle_Changed;
                    NetworkToggle.Unchecked -= NetworkToggle_Changed;

                    NetworkToggle.IsChecked = false; // Візуально вимикаємо тумблер

                    NetworkToggle.Checked += NetworkToggle_Changed;
                    NetworkToggle.Unchecked += NetworkToggle_Changed;

                    RebuildAiEngine(false); // Офіційно перезбираємо двигун на хмару

                    // Робимо ПОВТОРНИЙ запит
                    response = await _aiEngine.ProcessMessageAsync(userInput);
                }
                else
                {
                    response.Text = "Локальна мережа зависла, а ключа для хмари немає. Введи API ключ у налаштуваннях!";
                }
            }

            ChatPanel.Children.Remove(thinkingPanel);

            string finalColor = response.IsError ? "#F38BA8" : null;
            AddMessageToChat("TechBro", response.Text, finalColor);

            // АВТО-ПОВЕРНЕННЯ НА ЛОКАЛКУ
            if (wasFallbackTriggered)
            {
                // Робимо мікро-паузу, щоб інтерфейс відмалював великий текст
                await Task.Delay(1000);

                // Виводимо системне повідомлення (без кнопок копіювання)
                AddMessageToChat("Система", "☁️ Важке завдання виконано хмарою. Повертаюсь в економний локальний режим...", "#A6ADC8", false, false);

                // Просто вмикаємо тумблер! Твій метод NetworkToggle_Changed зробить всю іншу магію сам!
                NetworkToggle.IsChecked = true;
            }

            _isAiThinking = false;
            ScrollToBottom();
        }

        private StackPanel AddMessageToChat(string sender, string message, string hexColor = null, bool isInteractive = true, bool isDeletable = true)
        {
            var messagePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

            string displayName = sender == "TechBro" ? $"TechBro [{_aiEngine.CurrentModelName}]" : sender;

            var senderText = new TextBlock
            {
                Text = sender,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(sender == "TechBro" ? "#CBA6F7" : "#89B4FA")),
            };
            headerPanel.Children.Add(senderText);

            if (sender == "TechBro")
            {
                var modelBadge = new TextBlock
                {
                    Text = $" [{_aiEngine.CurrentModelName}]",
                    FontSize = 11,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6C7086")),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                };
                headerPanel.Children.Add(modelBadge);
            }

            var timeText = new TextBlock
            {
                Text = " • " + DateTime.Now.ToString("HH:mm"),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6C7086")),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(timeText);

            Brush normalBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(sender == "Ти" ? "#313244" : "#181825"));
            Brush hoverBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(sender == "Ти" ? "#45475A" : "#313244"));

            var messageBorder = new Border
            {
                Background = normalBg,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12)
            };

            var messageGrid = new Grid();

            // Визначаємо колір тексту (якщо передали кастомний - юзаємо його)
            Brush textBrush = hexColor != null
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString(sender == "TechBro" ? "#BAC2DE" : "#CDD6F4"));

            var contentText = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                FontSize = 14,
                Foreground = textBrush,
                Margin = new Thickness(0, 0, 45, 0)
            };

            var actionControls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Hidden
            };

            // ЯКЩО ПОВІДОМЛЕННЯ ІНТЕРАКТИВНЕ - ДОДАЄМО КНОПКИ ТА ХОВЕР
            if (isInteractive)
            {
                var copyIcon = new TextBlock { Text = "📋", Margin = new Thickness(0, 0, 10, 0), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Скопіювати текст" };
                copyIcon.MouseLeftButtonDown += (s, e) =>
                {
                    Clipboard.SetText(contentText.Text);
                    ShowNotification("Текст скопійовано в буфер! 📋");
                };
                actionControls.Children.Add(copyIcon);

                if (isDeletable)
                {
                    var deleteIcon = new TextBlock { Text = "🗑️", Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Видалити повідомлення" };
                    deleteIcon.MouseLeftButtonDown += (s, e) =>
                    {
                        ChatPanel.Children.Remove(messagePanel);
                        ShowNotification("Повідомлення успішно видалено 🗑️");
                    };
                    actionControls.Children.Add(deleteIcon);
                }

                // Ховер ефект працює ТІЛЬКИ тут
                messageBorder.MouseEnter += (s, e) =>
                {
                    messageBorder.Background = hoverBg;
                    actionControls.Visibility = Visibility.Visible;
                };
                messageBorder.MouseLeave += (s, e) =>
                {
                    messageBorder.Background = normalBg;
                    actionControls.Visibility = Visibility.Hidden;
                };
            }
            else
            {
                // Для "думок" і системних текстів робимо похилий шрифт і без кнопок
                contentText.FontStyle = FontStyles.Italic;
            }

            messageGrid.Children.Add(contentText);
            messageGrid.Children.Add(actionControls);
            messageBorder.Child = messageGrid;

            messagePanel.Children.Add(headerPanel);
            messagePanel.Children.Add(messageBorder);
            ChatPanel.Children.Add(messagePanel);

            ScrollToBottom();

            return messagePanel; // Тепер ми повертаємо всю панель!
        }

        private void ScrollToBottom() => ChatScrollViewer.ScrollToEnd();

        #endregion

        #region Моніторинг Датчиків у Реальному Часі

        // Хелпер для зміни кольору (зелений -> жовтий -> червоний)
        private void SetProgressBarColor(ProgressBar bar, int value)
        {
            if (value > 85) bar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F38BA8")); // Червоний
            else if (value > 50) bar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F9E2AF")); // Жовтий
            else
            {
                // Повертаємо базовий колір залежно від того, яка це смужка
                if (bar.Name == "CpuProgressBar") bar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A6E3A1"));
                else if (bar.Name == "RamProgressBar") bar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F9E2AF"));
                else if (bar.Name == "GpuProgressBar") bar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#89B4FA"));
                else if (bar.Name == "NetProgressBar") bar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBA6F7"));
            }
        }

        // Правильно закриваємо сенсори при виході з програми
        protected override void OnClosed(EventArgs e)
        {
            _sysMonitor?.Close();
            base.OnClosed(e);
        }

        // Обробник натискання на кнопку ☰
        private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e)
        {
            _isSidebarCollapsed = !_isSidebarCollapsed;

            if (_isSidebarCollapsed)
            {
                SidebarColumn.Width = new GridLength(60);
                SidebarContentPanel.Visibility = Visibility.Collapsed;
                SettingsText.Visibility = Visibility.Collapsed;
                ClockPanel.Visibility = Visibility.Collapsed; // <--- ДОДАЛИ ХОВАННЯ ГОДИННИКА
            }
            else
            {
                SidebarColumn.Width = new GridLength(250);
                SidebarContentPanel.Visibility = Visibility.Visible;
                SettingsText.Visibility = Visibility.Visible;
                ClockPanel.Visibility = Visibility.Visible; // <--- ДОДАЛИ ПОВЕРНЕННЯ ГОДИННИКА
            }
        }

        // 1. Відкриття спливаючого меню
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // Перед відкриттям зчитуємо поточний ключ, щоб показати його в полі
            if (System.IO.File.Exists("apikey.txt"))
            {
                ApiKeyBox.Text = System.IO.File.ReadAllText("apikey.txt").Trim();
            }

            // Відкриваємо наше красиве меню
            SettingsPopup.IsOpen = true;
        }

        // 2. Збереження з нового меню
        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            // Зберігаємо ключ
            System.IO.File.WriteAllText("apikey.txt", ApiKeyBox.Text.Trim());

            // Закриваємо меню
            SettingsPopup.IsOpen = false;

            // Перезапускаємо нейромережу з новим ключем
            bool useLocalNetwork = NetworkToggle.IsChecked == true;
            RebuildAiEngine(useLocalNetwork);

            // Замість білого системного вікна MessageBox, виводимо статус прямо в чат!
            AddMessageToChat("Система", "✅ Налаштування успішно збережено. Двигун перезапущено.");
        }

        private async void ShowNotification(string message)
        {
            NotificationText.Text = message;

            // Плавна поява
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromSeconds(0.2));
            NotificationBadge.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            await System.Threading.Tasks.Task.Delay(2000); // Чекаємо 2 секунди

            // Плавне зникнення
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromSeconds(0.5));
            NotificationBadge.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        #endregion
    }
}