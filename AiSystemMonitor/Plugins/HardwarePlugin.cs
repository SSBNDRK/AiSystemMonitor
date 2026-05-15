using AiSystemMonitor.Services;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;

namespace AiSystemMonitor.Plugins
{
    public class HardwarePlugin
    {
        public static List<string> LastProcesses = new();

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ коли юзер питає про 'пінг', 'затримку' або перевірку мережі/інтернету. Повертає затримку до сервера в мілісекундах (мс).")]
        public string GetNetworkPing()
        {
            try
            {
                using var pingSender = new System.Net.NetworkInformation.Ping();
                var reply = pingSender.Send("8.8.8.8", 1500);

                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                {
                    return $"Пінг (затримка): {reply.RoundtripTime} мс";
                }

                return $"Мережа недоступна. Статус: {reply.Status}";
            }
            catch (Exception ex)
            {
                return $"Помилка перевірки мережі: {ex.Message}";
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

        private static int _lastProcessSkip = 0;

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ ТІЛЬКИ коли юзер просить ПОКАЗАТИ або ВИВЕСТИ 'процеси'. КАТЕГОРИЧНО НЕ ВИКЛИКАЙ, якщо юзер просить ПОЯСНИТИ, ОПИСАТИ або РОЗКАЗАТИ про процес!")]
        public string GetTopProcesses(
        [Description("Кількість процесів для виводу (за замовчуванням 5, максимум 15)")] int count = 5,
        [Description("Встанови true, ТІЛЬКИ якщо користувач просить 'ще', 'далі' або 'наступні'")] bool isNextPage = false)
        {
            count = Math.Min(count, 15);

            if (!isNextPage)
            {
                _lastProcessSkip = 0;
                LastProcesses.Clear();
            }

            var topProcesses = Process.GetProcesses()
                .GroupBy(p => p.ProcessName)
                .Select(g => new
                {
                    Name = g.Key,
                    Memory = g.Sum(p =>
                    {
                        try { return p.WorkingSet64; }
                        catch { return 0; }
                    })
                })
                .OrderByDescending(p => p.Memory)
                .Skip(_lastProcessSkip)
                .Take(count)
                .ToList();

            var lines = new List<string>();
            string header = isNextPage ? "Наступні процеси (споживання ОЗП):" : "Топ процесів (споживання ОЗП):";
            lines.Add(header);
            for (int i = 0; i < topProcesses.Count; i++)
            {
                var p = topProcesses[i];
                LastProcesses.Add(p.Name);
                lines.Add($"{_lastProcessSkip + i + 1}. {p.Name} — {(p.Memory / 1024 / 1024):F0} МБ");
            }

            _lastProcessSkip += count;
            return string.Join("\n", lines);
        }

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

        [KernelFunction, Description("Перший етап закриття процесу: перевірка безпеки та розрахунок пам'яті. НІКОЛИ не закриває процес одразу.")]
        public string RequestProcessKill([Description("Назва процесу (наприклад, chrome, discord)")] string processName)
        {
            try
            {
                string[] systemProcesses = { "explorer", "svchost", "winlogon", "services", "system", "idle", "devenv" };

                if (systemProcesses.Contains(processName.ToLower()))
                {
                    return $"CRITICAL_ERROR|Бро, {processName} — це системний процес. Якщо я його закрию, твій ПК просто ляже. Я не буду цього робити.";
                }

                var processes = Process.GetProcessesByName(processName);
                if (processes.Length == 0)
                    return $"NOT_FOUND|Процес {processName} не знайдено серед запущених.";

                long totalMemory = 0;
                foreach (var p in processes)
                {
                    try { totalMemory += p.WorkingSet64; } catch { }
                }

                double memoryMb = totalMemory / 1024.0 / 1024.0;

                return $"APPROVE_REQUIRED|{processName}|{memoryMb:F0}";
            }
            catch (Exception ex)
            {
                return $"ERROR|Помилка при підготовці: {ex.Message}";
            }
        }

        [KernelFunction, Description("Другий етап закриття процесу: Остаточне закриття процесу. Викликається ТІЛЬКИ після явного 'Так' від користувача.")]
        public string ConfirmProcessKill([Description("Назва процесу для закриття")] string processName)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                int count = processes.Length;
                foreach (var p in processes)
                {
                    p.Kill();
                }
                return $"SUCCESS|Закрито процесів: {count} ({processName}).";
            }
            catch (Exception ex)
            {
                return $"ERROR|Не вдалося завершити дію: {ex.Message}";
            }
        }

        private string _pendingPowerPlanGuid = null;
        private string _pendingPowerPlanName = null;

        [KernelFunction("GetPowerPlans")]
        [Description("Отримує список усіх доступних схем живлення Windows. Активна схема позначена зірочкою (*).")]
        public string GetPowerPlans()
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/list")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                return output;
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [KernelFunction("RequestPowerPlanChange")]
        [Description("Готує зміну схеми живлення. ВИКЛИКАТИ ПЕРЕД ConfirmPowerPlanChange.")]
        public string RequestPowerPlanChange(
            [Description("GUID схеми живлення (довгий код із літер та цифр)")] string guid,
            [Description("Назва схеми для відображення")] string name)
        {
            _pendingPowerPlanGuid = guid;
            _pendingPowerPlanName = name;
            return $"SUCCESS: Ready to change to '{name}'. Запитай у юзера підтвердження ('так' чи 'ні').";
        }

        [KernelFunction("ConfirmPowerPlanChange")]
        [Description("Остаточно змінює схему живлення. Викликати ТІЛЬКИ після слова 'так' від юзера.")]
        public string ConfirmPowerPlanChange()
        {
            if (string.IsNullOrEmpty(_pendingPowerPlanGuid))
                return "ERROR: Немає підготовленої схеми. Спочатку виклич RequestPowerPlanChange.";

            try
            {
                var psi = new ProcessStartInfo("powercfg", $"/setactive {_pendingPowerPlanGuid}")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                process.WaitForExit();

                string name = _pendingPowerPlanName;
                _pendingPowerPlanGuid = null;
                _pendingPowerPlanName = null;

                return $"SUCCESS: Схему живлення успішно змінено на {name}.";
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ коли юзер питає про автозавантаження або чому ПК довго вмикається. Повертає список програм.")]
        public string GetStartupApps()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (key != null)
                    {
                        foreach (var val in key.GetValueNames())
                        {
                            sb.AppendLine($"- {val}");
                        }
                    }
                }
                return sb.Length > 0 ? sb.ToString() : "EMPTY_STARTUP";
            }
            catch (Exception ex) { return $"Помилка читання реєстру: {ex.Message}"; }
        }

        private bool _pendingMaxPower = false;
        private bool _pendingCloseBrowsers = false;

        [KernelFunction, Description("Перший етап оптимізації. ВИКЛИКАЙ ЦЕ коли юзер просить буст, ігровий режим або закрити браузери. Готує систему, але чекає підтвердження.")]
        public string RequestOptimization(
            [Description("Встановити 'Максимальну продуктивність'")] bool enableMaxPower,
            [Description("Закрити браузери")] bool closeBrowsers)
        {
            _pendingMaxPower = enableMaxPower;
            _pendingCloseBrowsers = closeBrowsers;

            // Якщо треба закрити браузери, бот ПОВИНЕН попередити користувача!
            if (closeBrowsers)
            {
                return "SUCCESS|Я підготував ПК до бусту. УВАГА: Я зараз закрию всі браузери (Chrome, Edge тощо). Збережи свою роботу і напиши 'Так', щоб я продовжив!";
            }

            return "SUCCESS|Я підготував схему живлення. Напиши 'Так', щоб активувати буст.";
        }

        [KernelFunction, Description("Другий етап оптимізації. Викликай ТІЛЬКИ після того, як юзер написав 'Так' на пропозицію бусту.")]
        public string ConfirmOptimization()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Звіт про оптимізацію:");

            if (_pendingMaxPower)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = "powercfg", Arguments = "/setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", CreateNoWindow = true, UseShellExecute = false });
                    sb.AppendLine("- Максимальна продуктивність: [УВІМКНЕНО] ⚡");
                }
                catch { sb.AppendLine("- Живлення: Помилка доступу"); }
            }

            if (_pendingCloseBrowsers)
            {
                string[] browsers = { "chrome", "msedge", "opera", "firefox" };
                int closedCount = 0;
                foreach (var b in browsers)
                {
                    foreach (var p in Process.GetProcessesByName(b))
                    {
                        try { p.Kill(); closedCount++; } catch { }
                    }
                }
                sb.AppendLine($"- Фонові браузери: Закрито ({closedCount} процесів) 🧹");
            }

            // Очищаємо пам'ять
            _pendingMaxPower = false;
            _pendingCloseBrowsers = false;

            return sb.ToString();
        }
    }
}