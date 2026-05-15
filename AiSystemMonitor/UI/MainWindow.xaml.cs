using AiSystemMonitor.Services;
using System.Windows;
using System.Windows.Threading;

namespace AiSystemMonitor
{
    public partial class MainWindow : Window
    {
        #region Змінні
        private bool _isAiThinking = false;
        private bool _isSidebarCollapsed = false;
        private bool _isNetworkSwitching = false;

        private FrameworkElement _lastSystemMessage = null;

        private SystemMonitor _sysMonitor;
        private AiEngine _aiEngine;
        #endregion

        public MainWindow()
        {
            AppConfig.Load();
            InitializeComponent();
            InitializeAppAsync();
        }

        // Кнопка "Згорнути"
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        // Кнопка "Закрити" (Хрестик)
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // Додай цей метод десь в класі MainWindow, наприклад, поруч із CloseButton_Click
        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            // Тобі знадобиться ім'я кнопки з XAML (я назвав її MaximizeButton)
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                // Можна також змінювати іконку на ☐ назад
                MaximizeButton.Content = "☐";
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                // Можна змінювати іконку на ⧉, коли вікно розгорнуто
                MaximizeButton.Content = "⧉";
            }
        }

        private async void InitializeAppAsync()
        {
            _aiEngine = new AiEngine();
            AddMessageToChat("TechBro", "Я TechBro. Аналізую систему та перевіряю мережу...");

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
    }
}