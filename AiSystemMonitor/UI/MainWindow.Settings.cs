using AiSystemMonitor.Services;
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
            ApiKeyBox.Text = AppConfig.ApiKey;

            // Відкриваємо наше красиве меню
            SettingsPopup.IsOpen = true;

            // Завантажуємо список моделей Ollama
            OllamaModelSelector.Items.Clear();
            OllamaModelSelector.Items.Add("Завантаження...");
            OllamaModelSelector.SelectedIndex = 0;

            var models = await _aiEngine.GetAvailableOllamaModelsAsync();

            OllamaModelSelector.Items.Clear();

            foreach (var m in models)
                OllamaModelSelector.Items.Add(m);

            string savedModel = AppConfig.OllamaModel;

            if (!string.IsNullOrEmpty(savedModel) && models.Contains(savedModel))
                OllamaModelSelector.SelectedItem = savedModel;
            else if (models.Count > 0)
                OllamaModelSelector.SelectedIndex = 0;
        }

        // 2. Збереження з нового меню
        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            // Оновлюємо змінні в пам'яті
            AppConfig.ApiKey = ApiKeyBox.Text.Trim();
            AppConfig.OllamaModel = OllamaModelSelector.SelectedItem?.ToString() ?? "";

            // Зберігаємо на диск один раз
            AppConfig.Save();

            SettingsPopup.IsOpen = false;
            RebuildAiEngine(NetworkToggle.IsChecked == true);
            AddMessageToChat("Система", "✅ Налаштування оновлено.", "#A6ADC8", false);
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

                    AddMessageToChat("Система", "❌ Сервер Ollama не відповідає. Переконайся, що програма Ollama запущена на ПК.", "#F38BA8", false);

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
                string apiKey = AppConfig.ApiKey;

                // ЧИТАЄМО ЗБЕРЕЖЕНУ ЛОКАЛЬНУ МОДЕЛЬ
                string savedModel = AppConfig.OllamaModel;

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
