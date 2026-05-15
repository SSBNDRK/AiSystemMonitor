using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AiSystemMonitor
{
    public partial class MainWindow
    {
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
        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // Зчитуємо API ключ
            if (System.IO.File.Exists("apikey.txt"))
                ApiKeyBox.Text = System.IO.File.ReadAllText("apikey.txt").Trim();

            // Відкриваємо наше красиве меню
            SettingsPopup.IsOpen = true;

            // Завантажуємо список моделей Ollama
            OllamaModelSelector.Items.Clear();
            OllamaModelSelector.Items.Add("Завантаження...");
            OllamaModelSelector.SelectedIndex = 0;

            var models = await _aiEngine.GetAvailableOllamaModelsAsync();

            OllamaModelSelector.Items.Clear();
            foreach (var m in models) OllamaModelSelector.Items.Add(m);

            // Читаємо збережену модель, або ставимо першу в списку
            string savedModel = System.IO.File.Exists("ollamamodel.txt") ? System.IO.File.ReadAllText("ollamamodel.txt").Trim() : "";
            if (!string.IsNullOrEmpty(savedModel) && models.Contains(savedModel))
                OllamaModelSelector.SelectedItem = savedModel;
            else if (models.Count > 0)
                OllamaModelSelector.SelectedIndex = 0;
        }

        // 2. Збереження з нового меню
        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            // 1. Читаємо те, що введено зараз
            string newKey = ApiKeyBox.Text.Trim();
            string newModel = OllamaModelSelector.SelectedItem?.ToString() ?? "";

            // 2. Читаємо те, що було збережено раніше
            string oldKey = System.IO.File.Exists("apikey.txt") ? System.IO.File.ReadAllText("apikey.txt").Trim() : "";
            string oldModel = System.IO.File.Exists("ollamamodel.txt") ? System.IO.File.ReadAllText("ollamamodel.txt").Trim() : "";

            // 3. ПЕРЕВІРКА НА СПАМ: Якщо нічого не змінилося - просто закриваємо меню і виходимо!
            if (newKey == oldKey && (newModel == oldModel || newModel == "Завантаження..."))
            {
                SettingsPopup.IsOpen = false;
                return;
            }

            // 4. Якщо були зміни - зберігаємо
            System.IO.File.WriteAllText("apikey.txt", newKey);

            if (!string.IsNullOrEmpty(newModel) && newModel != "Завантаження...")
            {
                System.IO.File.WriteAllText("ollamamodel.txt", newModel);
            }

            SettingsPopup.IsOpen = false;

            // Перезапускаємо нейромережу тільки тому, що налаштування дійсно змінилися
            bool useLocalNetwork = NetworkToggle.IsChecked == true;
            RebuildAiEngine(useLocalNetwork);

            AddMessageToChat("Система", "✅ Налаштування оновлено. Двигун перезапущено.", "#A6ADC8", false);
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

                    AddMessageToChat("Система", "☁️ Важке завдання виконано хмарою. Повертаюсь в економний локальний режим...", "#A6ADC8", false);

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

                // ЧИТАЄМО ЗБЕРЕЖЕНУ ЛОКАЛЬНУ МОДЕЛЬ
                string savedModel = System.IO.File.Exists("ollamamodel.txt") ? System.IO.File.ReadAllText("ollamamodel.txt").Trim() : null;

                if (!useLocal && string.IsNullOrEmpty(apiKey))
                {
                    AiStatusText.Text = "API ключ не знайдено!";
                    AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F38BA8"));
                    return;
                }

                AiStatusText.Text = useLocal ? "Підключення до Ollama..." : "Підключення до хмари...";

                // ПЕРЕДАЄМО ЗБЕРЕЖЕНУ МОДЕЛЬ ТРЕТІМ ПАРАМЕТРОМ
                _aiEngine.RebuildEngine(useLocal, apiKey, savedModel);

                AiStatusText.Text = useLocal ? $"Локальна ({_aiEngine.CurrentModelName})" : $"Хмарна ({_aiEngine.CurrentModelName})";
                AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(useLocal ? "#A6E3A1" : "#F9E2AF"));
            }
            catch (Exception ex)
            {
                AiStatusText.Text = "Помилка двигуна!";
                AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F38BA8"));
                AddMessageToChat("Система", $"Збій перемикання мережі: {ex.Message}", null, false);
            }
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
    }
}
