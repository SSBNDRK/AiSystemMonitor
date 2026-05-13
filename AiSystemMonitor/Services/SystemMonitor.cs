using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;

namespace AiSystemMonitor.Services
{
    // Клас-контейнер для передачі даних у UI
    public class HardwareUpdateEventArgs : EventArgs
    {
        public int CpuUsage { get; set; }
        public double UsedRamGb { get; set; }
        public double TotalRamGb { get; set; }
        public int RamPercentage { get; set; }
        public int GpuLoad { get; set; }
        public double NetMbps { get; set; }
        public int NetPercentage { get; set; }
        public string CurrentTime { get; set; }
        public string CurrentDate { get; set; }
    }

    public class SystemMonitor
    {
        private DispatcherTimer _timer;
        private PerformanceCounter _cpuCounter;
        private PerformanceCounter _ramAvailableCounter;
        private Computer _hardwareMonitor;

        private double _totalRamGb = 16.0;
        private long _lastNetworkBytes = 0;
        private DateTime _lastNetworkTime = DateTime.MinValue;

        // Подія, на яку підпишеться MainWindow
        public event EventHandler<HardwareUpdateEventArgs> OnStatsUpdated;

        public SystemMonitor()
        {
            InitCounters();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _timer.Tick += (s, e) => UpdateStats();
        }

        private void InitCounters()
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _ramAvailableCounter = new PerformanceCounter("Memory", "Available MBytes");

                using (var searcher = new ManagementObjectSearcher("select TotalPhysicalMemory from Win32_ComputerSystem"))
                {
                    var ram = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (ram != null) _totalRamGb = Convert.ToDouble(ram["TotalPhysicalMemory"]) / (1024 * 1024 * 1024);
                }

                _cpuCounter.NextValue();

                _hardwareMonitor = new Computer { IsGpuEnabled = true };
                _hardwareMonitor.Open();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Monitor Init Error: " + ex.Message);
            }
        }

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();

        private void UpdateStats()
        {
            var args = new HardwareUpdateEventArgs();

            // CPU & RAM logic
            try { args.CpuUsage = (int)_cpuCounter.NextValue(); } catch { args.CpuUsage = 0; }

            args.TotalRamGb = _totalRamGb;
            try { args.UsedRamGb = _totalRamGb - (_ramAvailableCounter.NextValue() / 1024.0); } catch { args.UsedRamGb = 0; }
            args.RamPercentage = (int)((args.UsedRamGb / _totalRamGb) * 100);

            // GPU logic
            args.GpuLoad = 0;
            if (_hardwareMonitor != null)
            {
                foreach (var hw in _hardwareMonitor.Hardware)
                {
                    if (hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuNvidia)
                    {
                        hw.Update();
                        foreach (var sensor in hw.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Load && sensor.Name.Contains("Core"))
                                args.GpuLoad = (int)(sensor.Value ?? 0);
                        }
                    }
                }
            }

            // Network logic
            UpdateNetworkStats(args);

            // Time logic
            args.CurrentTime = DateTime.Now.ToString("HH:mm");
            args.CurrentDate = DateTime.Now.ToString("dddd, d MMMM yyyy", new System.Globalization.CultureInfo("uk-UA"));

            // Відправляємо дані у вікно
            OnStatsUpdated?.Invoke(this, args);
        }

        private void UpdateNetworkStats(HardwareUpdateEventArgs args)
        {
            long totalBytes = 0;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        var ipProps = ni.GetIPProperties();
                        if (ipProps.GatewayAddresses.Count > 0)
                            totalBytes += ni.GetIPStatistics().BytesReceived + ni.GetIPStatistics().BytesSent;
                    }
                }

                DateTime now = DateTime.Now;
                if (_lastNetworkTime != DateTime.MinValue)
                {
                    double timeDiff = (now - _lastNetworkTime).TotalSeconds;
                    long bytesDelta = totalBytes - _lastNetworkBytes;
                    double mbps = ((bytesDelta * 8.0) / timeDiff) / 1_000_000.0;

                    args.NetMbps = mbps < 0 ? 0 : mbps;
                    double scaleMax = mbps > 1000.0 ? 10000.0 : (mbps > 100.0 ? 1000.0 : 100.0);
                    args.NetPercentage = (int)((args.NetMbps / scaleMax) * 100);
                    if (args.NetPercentage > 100) args.NetPercentage = 100;
                }
                _lastNetworkBytes = totalBytes;
                _lastNetworkTime = now;
            }
            catch
            {
                args.NetMbps = 0;
                args.NetPercentage = 0;
            }
        }

        public void Close()
        {
            _timer?.Stop();
            _hardwareMonitor?.Close();
        }
    }
}