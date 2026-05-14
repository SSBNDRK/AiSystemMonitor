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
            InitializeComponent();
            InitializeAppAsync();
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