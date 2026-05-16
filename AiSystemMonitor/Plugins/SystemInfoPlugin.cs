using AiSystemMonitor.Services;
using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace AiSystemMonitor.Plugins
{
    class SystemInfoPlugin
    {
        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ ТІЛЬКИ коли юзер прямо пише 'мій пк', 'характеристики', 'яке залізо', 'аналіз пк'. КАТЕГОРИЧНО НЕ ВИКЛИКАЙ на слова 'привіт' або 'як справи'.")]
        public string GetSystemSpecs()
        {
            try
            {
                var sb = new StringBuilder();

                using (var searcher = new ManagementObjectSearcher("select Name, NumberOfCores, NumberOfLogicalProcessors, SocketDesignation from Win32_Processor"))
                {
                    var cpu = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (cpu != null)
                    {
                        string socket = cpu["SocketDesignation"]?.ToString() ?? "Невідомо";
                        string cpuName = cpu["Name"]?.ToString().Replace("6-Core Processor", "").Trim();
                        sb.AppendLine($"Процесор: {cpuName} ({cpu["NumberOfCores"]} ядер, {cpu["NumberOfLogicalProcessors"]} потоків, Сокет: {socket})");
                    }
                }

                using (var searcher = new ManagementObjectSearcher("select Product, Manufacturer from Win32_BaseBoard"))
                {
                    var board = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (board != null)
                    {
                        string manufacturer = board["Manufacturer"]?.ToString().Replace("Technology Co., Ltd.", "").Trim();
                        sb.AppendLine($"Материнка: {manufacturer} {board["Product"]}");
                    }
                }

                double ramGb = 0;
                using (var searcher = new ManagementObjectSearcher("select TotalPhysicalMemory from Win32_ComputerSystem"))
                {
                    var ram = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (ram != null) ramGb = Math.Ceiling(Convert.ToDouble(ram["TotalPhysicalMemory"]) / (1024 * 1024 * 1024));
                }

                string ramSpeed = "";
                string ddrType = "";
                using (var searcher = new ManagementObjectSearcher("select Speed, SMBIOSMemoryType from Win32_PhysicalMemory"))
                {
                    var ramModule = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (ramModule != null)
                    {
                        if (ramModule["Speed"] != null) ramSpeed = ramModule["Speed"].ToString();

                        if (ramModule["SMBIOSMemoryType"] != null)
                        {
                            int typeNum = Convert.ToInt32(ramModule["SMBIOSMemoryType"]);
                            if (typeNum == 26) ddrType = "DDR4 ";
                            else if (typeNum == 34) ddrType = "DDR5 ";
                            else if (typeNum == 24) ddrType = "DDR3 ";
                        }
                    }
                }

                sb.AppendLine($"ОЗП: {ramGb} ГБ {ddrType}{(string.IsNullOrEmpty(ramSpeed) ? "" : $"({ramSpeed} МГц)")}");

                using (var searcher = new ManagementObjectSearcher("select Name, AdapterRAM from Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string gpuName = obj["Name"]?.ToString() ?? "Unknown GPU";
                        string vramInfo = "";
                        if (obj["AdapterRAM"] != null)
                        {
                            double vramGb = Math.Ceiling(Convert.ToDouble(obj["AdapterRAM"]) / (1024 * 1024 * 1024));
                            vramInfo = $" ({vramGb}GB VRAM)";
                        }
                        sb.AppendLine($"GPU: {gpuName}{vramInfo}");
                    }
                }

                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ для отримання температур, навантаження, кулерів CPU (процесора) та GPU (відеокарти).")]
        public string GetSystemSensors()
        {
            var stats = SystemMonitor.LatestStats;

            string cpuTempStr = stats.CpuTemp > 0 ? $"{stats.CpuTemp:F1} °C" : "Датчик недоступний";
            string gpuTempStr = stats.GpuTemp > 0 ? $"{stats.GpuTemp:F1} °C" : "Датчик недоступний";
            string gpuHotspotStr = stats.GpuHotspot > 0 ? $"{stats.GpuHotspot:F1} °C" : "Недоступно";

            return $"""
            [CPU]
            - Навантаження: {stats.CpuUsage}%
            - Температура: {cpuTempStr}
    
            [GPU]
            - Навантаження: {stats.GpuLoad}%
            - Температура ядра: {gpuTempStr}
            - Гаряча точка: {gpuHotspotStr}
            - Кулери: {stats.GpuFan} RPM
            """;
        }

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ для перевірки всіх дисків: вільного місця, загального обсягу та стану здоров'я (S.M.A.R.T.). НЕ ДЛЯ ШВИДКОСТІ!")]
        public string GetDisksStatus()
        {
            try
            {
                var sb = new StringBuilder();

                // 1. Отримуємо стан здоров'я через WMI
                var healthDict = new Dictionary<string, string>();
                using (var searcher = new ManagementObjectSearcher("SELECT Model, Status FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject wmi_HD in searcher.Get())
                    {
                        string model = wmi_HD["Model"]?.ToString() ?? "Невідомо";
                        string status = wmi_HD["Status"]?.ToString() ?? "Невідомо";
                        healthDict[model] = status.ToUpper() == "OK" ? "OK" : status;
                    }
                }

                // 2. Отримуємо вільне місце та комбінуємо з S.M.A.R.T.
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    double free = drive.TotalFreeSpace / 1024.0 / 1024.0 / 1024.0;
                    double total = drive.TotalSize / 1024.0 / 1024.0 / 1024.0;

                    sb.AppendLine($"Диск {drive.Name[0]}: Вільно {free:F1}ГБ / {total:F1}ГБ");
                }

                sb.AppendLine("\nАпаратний стан (S.M.A.R.T.):");
                foreach (var h in healthDict) sb.AppendLine($"- {h.Key}: {h.Value}");

                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                return $"Помилка: {ex.Message}";
            }
        }

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ ТІЛЬКИ коли юзер прямо просить 'тест швидкості', 'протестуй диск' або 'швидкість диска'. Повертає МБ/с.")]
        public string TestDiskSpeed([Description("Літера диска, наприклад 'C'")] string driveLetter = "C")
        {
            try
            {
                string drive = driveLetter.Replace(":", "").Trim().ToUpper() + ":\\";
                if (!Directory.Exists(drive)) return $"Помилка: Диск {drive} не знайдено.";

                string tempFilePath = Path.Combine(drive, "techbro_speedtest.tmp");

                int bufferSize = 10 * 1024 * 1024;
                byte[] buffer = new byte[bufferSize];
                new Random().NextBytes(buffer);

                int writes = 20;
                int totalMb = (bufferSize * writes) / (1024 * 1024);

                var sw = Stopwatch.StartNew();

                using (var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.WriteThrough))
                {
                    for (int i = 0; i < writes; i++)
                    {
                        fs.Write(buffer, 0, buffer.Length);
                    }
                }

                sw.Stop();

                if (File.Exists(tempFilePath)) File.Delete(tempFilePath);

                double seconds = sw.Elapsed.TotalSeconds;
                double speed = totalMb / seconds;

                return $"Швидкість запису на диск {drive} становить {speed:F0} МБ/с.";
            }
            catch (UnauthorizedAccessException)
            {
                return $"Помилка: Немає прав для тестування диска. Спробуй запустити програму від імені Адміністратора.";
            }
            catch (Exception ex)
            {
                return $"Помилка тесту диска: {ex.Message}";
            }
        }

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ коли юзер питає про сині екрани (BSOD), вильоти ігор, краші, або 'чому вимкнувся/перезавантажився ПК'.")]
        public string GetRecentCrashes()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                // Підключаємося до системного журналу Windows
                var eventLog = new System.Diagnostics.EventLog("System");

                // Шукаємо критичні помилки за останні 24 години
                var recentErrors = eventLog.Entries.Cast<System.Diagnostics.EventLogEntry>()
                    .Where(e => e.TimeGenerated > DateTime.Now.AddDays(-1) &&
                               (e.EntryType == System.Diagnostics.EventLogEntryType.Error ||
                                e.EntryType == System.Diagnostics.EventLogEntryType.Warning))
                    .Where(e => e.Source.Contains("Kernel-Power") || e.Source.Contains("BugCheck") || e.Source.Contains("Display"))
                    .OrderByDescending(e => e.TimeGenerated)
                    .Take(3)
                    .ToList();

                if (recentErrors.Count == 0)
                    return "Системний журнал чистий. Критичних збоїв живлення (Kernel-Power), синіх екранів (BugCheck) чи вильотів відеодрайвера за останні 24 години не зафіксовано.";

                sb.AppendLine("Останні зафіксовані системні збої:");
                foreach (var entry in recentErrors)
                {
                    // Форматуємо для ШІ: [Час] Джерело: Текст помилки
                    sb.AppendLine($"- [{entry.TimeGenerated:HH:mm}] Джерело: {entry.Source}. Деталі: {entry.Message.Split('.')[0]}");
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Помилка читання журналу Windows: {ex.Message}";
            }
        }
    }
}
