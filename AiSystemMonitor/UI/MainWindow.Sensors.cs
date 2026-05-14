using System.Windows.Controls;
using System.Windows.Media;

namespace AiSystemMonitor
{
    public partial class MainWindow
    {
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
    }
}
