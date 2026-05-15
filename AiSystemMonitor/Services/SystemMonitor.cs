using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;

namespace AiSystemMonitor.Services
{
    public class HardwareUpdateEventArgs : EventArgs
    {
        public int CpuUsage { get; set; }
        public float CpuTemp { get; set; }
        public double UsedRamGb { get; set; }
        public double TotalRamGb { get; set; }
        public int RamPercentage { get; set; }

        // ДАНІ ВІДЕОКАРТИ
        public int GpuLoad { get; set; }
        public float GpuTemp { get; set; }
        public float GpuHotspot { get; set; }
        public int GpuFan { get; set; }

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

        public event EventHandler<HardwareUpdateEventArgs> OnStatsUpdated;

        public static HardwareUpdateEventArgs LatestStats { get; private set; } = new HardwareUpdateEventArgs();

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

                // ФІКС 1: Вмикаємо і процесор (CPU), і відеокарту (GPU)
                _hardwareMonitor = new Computer { IsGpuEnabled = true, IsCpuEnabled = true, IsMotherboardEnabled = true };
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
            try { args.CpuUsage = (int)_cpuCounter.NextValue(); } catch { args.CpuUsage = 0; }

            args.TotalRamGb = _totalRamGb;
            try { args.UsedRamGb = _totalRamGb - (_ramAvailableCounter.NextValue() / 1024.0); } catch { args.UsedRamGb = 0; }
            args.RamPercentage = (int)((args.UsedRamGb / _totalRamGb) * 100);

            args.GpuLoad = 0;
            args.GpuTemp = 0;
            args.CpuTemp = 0;

            float backupCpuTemp = 0; // Для хитрих Ryzen

            if (_hardwareMonitor != null)
            {
                foreach (var hw in _hardwareMonitor.Hardware)
                {
                    try { hw.Update(); } catch { continue; }

                    // === 1. ВІДЕОКАРТА ===
                    // Шукаємо навантаження, температури та кулери GPU
                    if (hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuNvidia)
                    {
                        foreach (var sensor in hw.Sensors)
                        {
                            // Навантаження
                            if (sensor.SensorType == SensorType.Load && sensor.Name.Contains("Core"))
                                args.GpuLoad = (int)(sensor.Value ?? 0);

                            // Температури (Шукаємо Hotspot та звичайну)
                            if (sensor.SensorType == SensorType.Temperature)
                            {
                                if (sensor.Name.Contains("Hot Spot") || sensor.Name.Contains("Hotspot"))
                                    args.GpuHotspot = sensor.Value ?? 0;
                                else if (sensor.Name.Contains("Core"))
                                    args.GpuTemp = sensor.Value ?? 0;
                            }

                            // Вентилятори (RPM)
                            if (sensor.SensorType == SensorType.Fan)
                                args.GpuFan = (int)(sensor.Value ?? 0);
                        }
                    }

                    // === 2. ПРОЦЕСОР (Стандартний пошук) ===
                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        foreach (var sensor in hw.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                            {
                                if (sensor.Value.Value > args.CpuTemp) args.CpuTemp = sensor.Value.Value;
                            }
                        }
                    }

                    // === 3. МАТЕРИНКА (Запасний план для AMD Ryzen) ===
                    if (hw.HardwareType == HardwareType.Motherboard)
                    {
                        foreach (var sensor in hw.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                            {
                                string sName = sensor.Name.ToLower();
                                if (sName.Contains("cpu") || sName.Contains("core") || sName.Contains("tctl"))
                                {
                                    if (sensor.Value.Value > backupCpuTemp) backupCpuTemp = sensor.Value.Value;
                                }
                            }
                        }
                    }
                }
            }

            // Якщо основний датчик процесора мовчить, беремо дані з материнки
            if (args.CpuTemp == 0 && backupCpuTemp > 0)
            {
                args.CpuTemp = backupCpuTemp;
            }

            UpdateNetworkStats(args);

            args.CurrentTime = DateTime.Now.ToString("HH:mm");
            args.CurrentDate = DateTime.Now.ToString("dddd, d MMMM yyyy", new System.Globalization.CultureInfo("uk-UA"));

            LatestStats = args; // Зберігаємо останні дані

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