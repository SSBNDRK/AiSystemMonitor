using AiSystemMonitor.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AiSystemMonitor
{
    public partial class MainWindow
    {
        private async void SendButton_Click(object sender, RoutedEventArgs e) => await ProcessUserMessage();
        private void ScrollToBottom() => ChatScrollViewer.ScrollToEnd();

        private async Task ProcessUserMessage()
        {
            string userInput = UserInputBox.Text.Trim();
            if (string.IsNullOrEmpty(userInput) || _isAiThinking || _aiEngine == null) return;

            UserInputBox.Text = string.Empty;
            _isAiThinking = true;

            SendButton.IsEnabled = false;
            SendButton.Opacity = 0.5;

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

                // Виводимо системне повідомлення
                AddMessageToChat("Система", "☁️ Важке завдання виконано хмарою. Повертаюсь в економний локальний режим...", "#A6ADC8");

                // Просто вмикаємо тумблер! Твій метод NetworkToggle_Changed зробить всю іншу магію сам!
                NetworkToggle.IsChecked = true;
            }

            _isAiThinking = false;
            SendButton.IsEnabled = true;
            SendButton.Opacity = 1.0;
            UserInputBox.Focus();

            ScrollToBottom();
        }

        private StackPanel AddMessageToChat(string sender, string message, string hexColor = null, bool isInteractive = true)
        {
            // 1. АВТОВИДАЛЕННЯ: Якщо пише НЕ Система, прибираємо з екрану попереднє системне повідомлення
            if (sender != "Система" && _lastSystemMessage != null)
            {
                ChatPanel.Children.Remove(_lastSystemMessage); // Виправлено на ChatPanel
                _lastSystemMessage = null;
            }

            var messagePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

            string displayName = sender == "TechBro" ? $"TechBro [{_aiEngine.CurrentModelName}]" : sender;

            var senderText = new TextBlock
            {
                Text = sender,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                sender == "TechBro" ? "#CBA6F7" : (sender == "Система" ? "#A6ADC8" : "#89B4FA")
            ))
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

            // Визначаємо колір тексту (параметр тепер hexColor)
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

                // Тут у тебе була змінна isDeletable, якої немає в параметрах. Я замінив її на isInteractive,
                // оскільки якщо воно інтерактивне, логічно, що його можна видалити.
                var deleteIcon = new TextBlock { Text = "🗑️", Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Видалити повідомлення" };
                deleteIcon.MouseLeftButtonDown += (s, e) =>
                {
                    ChatPanel.Children.Remove(messagePanel);
                    ShowNotification("Повідомлення успішно видалено 🗑️");
                };
                actionControls.Children.Add(deleteIcon);

                // Ховер ефект
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

            // 2. ЗБЕРЕЖЕННЯ: Якщо це Система, запам'ятовуємо всю панель, щоб видалити її наступного разу
            if (sender == "Система")
            {
                _lastSystemMessage = messagePanel;
            }

            ScrollToBottom();

            // ПОВЕРНЕННЯ ЗАВЖДИ В КІНЦІ!
            return messagePanel;
        }
    }
}
