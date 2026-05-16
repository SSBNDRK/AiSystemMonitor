using AiSystemMonitor.Services;
using Microsoft.SemanticKernel;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace AiSystemMonitor.Plugins
{
    public class HardwarePlugin
    {
        public static List<string> LastProcesses = new();
        private List<string> _pendingCleanupFiles = new();

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

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ коли юзер просить перевірити мережу, інтернет, DNS, шлюз або стабільність підключення. Робить коротку діагностику мережі.")]
        public string GetNetworkDiagnostics()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Діагностика мережі:");

            sb.AppendLine(CheckPing("8.8.8.8", "Google DNS"));
            sb.AppendLine(CheckPing("1.1.1.1", "Cloudflare DNS"));

            try
            {
                var addresses = Dns.GetHostAddresses("google.com");
                sb.AppendLine(addresses.Length > 0
                    ? "- DNS: працює"
                    : "- DNS: не вдалося отримати адресу");
            }
            catch
            {
                sb.AppendLine("- DNS: помилка перевірки");
            }

            try
            {
                var gateway = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(ni => ni.GetIPProperties().GatewayAddresses)
                    .Select(g => g.Address.ToString())
                    .FirstOrDefault(g => !string.IsNullOrWhiteSpace(g) && g != "0.0.0.0");

                if (!string.IsNullOrWhiteSpace(gateway))
                    sb.AppendLine(CheckPing(gateway, "Шлюз"));
                else
                    sb.AppendLine("- Шлюз: не знайдено");
            }
            catch
            {
                sb.AppendLine("- Шлюз: помилка перевірки");
            }

            return sb.ToString().Trim();
        }

        private string CheckPing(string host, string label)
        {
            try
            {
                using var pingSender = new System.Net.NetworkInformation.Ping();
                var reply = pingSender.Send(host, 1500);

                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                {
                    string timeText = reply.RoundtripTime == 0 ? "<1" : reply.RoundtripTime.ToString();
                    return $"- {label}: {timeText} мс";
                }

                return $"- {label}: недоступний ({reply.Status})";
            }
            catch
            {
                return $"- {label}: помилка перевірки";
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

        private string _pendingKillProcessName = null;

        [KernelFunction, Description("Перший етап закриття процесу: перевіряє безпеку, рахує пам'ять і зберігає процес для підтвердження. НІКОЛИ не закриває процес одразу.")]
        public string RequestProcessKill([Description("Назва процесу (наприклад, chrome, discord)")] string processName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(processName))
                    return "ERROR|Назва процесу порожня.";

                processName = processName.Trim();

                string[] systemProcesses =
                {
            "explorer", "svchost", "winlogon", "services", "system",
            "idle", "devenv", "csrss", "lsass", "smss", "dwm"
        };

                if (systemProcesses.Contains(processName.ToLower()))
                {
                    _pendingKillProcessName = null;
                    return $"CRITICAL_ERROR|Бро, {processName} — це системний процес. Якщо я його закрию, твій ПК може зависнути або вилетіти. Я не буду цього робити.";
                }

                var processes = Process.GetProcessesByName(processName);
                if (processes.Length == 0)
                {
                    _pendingKillProcessName = null;
                    return $"NOT_FOUND|Процес {processName} не знайдено серед запущених.";
                }

                long totalMemory = 0;
                foreach (var p in processes)
                {
                    try { totalMemory += p.WorkingSet64; } catch { }
                }

                double memoryMb = totalMemory / 1024.0 / 1024.0;

                // ВАЖЛИВО: зберігаємо процес, який очікує підтвердження
                _pendingKillProcessName = processName;

                return $"APPROVE_REQUIRED|{processName}|{memoryMb:F0}";
            }
            catch (Exception ex)
            {
                _pendingKillProcessName = null;
                return $"ERROR|Помилка при підготовці: {ex.Message}";
            }
        }

        [KernelFunction, Description("Другий етап закриття процесу. Викликається ТІЛЬКИ після явного 'Так' від користувача. Не приймає назву процесу, а закриває тільки раніше підготовлений процес.")]
        public string ConfirmProcessKill()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_pendingKillProcessName))
                    return "ERROR|Немає процесу, який очікує підтвердження. Спочатку треба викликати RequestProcessKill.";

                string processName = _pendingKillProcessName;
                _pendingKillProcessName = null;

                var processes = Process.GetProcessesByName(processName);
                int count = processes.Length;

                if (count == 0)
                    return $"NOT_FOUND|Процес {processName} вже не запущений.";

                foreach (var p in processes)
                {
                    try { p.Kill(); }
                    catch { }
                }

                return $"SUCCESS|Закрито процесів: {count} ({processName}).";
            }
            catch (Exception ex)
            {
                _pendingKillProcessName = null;
                return $"ERROR|Не вдалося завершити дію: {ex.Message}";
            }
        }

        private string _pendingPowerPlanGuid = null;
        private string _pendingPowerPlanName = null;

        [KernelFunction("GetPowerPlans")]
        [Description("Отримує список схем живлення Windows у зручному вигляді. Активна схема позначена.")]
        public string GetPowerPlans()
        {
            try
            {
                var plans = ReadPowerPlans();

                if (plans.Count == 0)
                    return "Не вдалося знайти схеми живлення Windows.";

                var sb = new StringBuilder();
                sb.AppendLine("Доступні режими електроживлення:");

                for (int i = 0; i < plans.Count; i++)
                {
                    string activeMark = plans[i].IsActive ? " — активний зараз" : "";
                    sb.AppendLine($"{i + 1}. {plans[i].Name}{activeMark}");
                }

                sb.AppendLine();
                sb.AppendLine("Можеш написати: «увімкни високу продуктивність», «увімкни збалансований режим» або «увімкни економію».");

                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                return $"Помилка читання схем живлення: {ex.Message}";
            }
        }

        private List<PowerPlanInfo> ReadPowerPlans()
        {
            var plans = new List<PowerPlanInfo>();

            var psi = new ProcessStartInfo("powercfg", "/list")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            foreach (var line in output.Split('\n'))
            {
                var guidMatch = Regex.Match(line, @"[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}");
                if (!guidMatch.Success) continue;

                string guid = guidMatch.Value;
                string name = line;

                int open = line.IndexOf('(');
                int close = line.LastIndexOf(')');

                if (open >= 0 && close > open)
                    name = line.Substring(open + 1, close - open - 1).Trim();

                bool isActive = line.Contains("*");

                plans.Add(new PowerPlanInfo
                {
                    Guid = guid,
                    Name = name,
                    IsActive = isActive
                });
            }

            return plans;
        }

        private class PowerPlanInfo
        {
            public string Guid { get; set; }
            public string Name { get; set; }
            public bool IsActive { get; set; }
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

        [KernelFunction("RequestPowerPlanChangeByName")]
        [Description("Готує зміну схеми живлення за назвою. Викликай, коли юзер просить увімкнути збалансований режим, економію або високу продуктивність.")]
        public string RequestPowerPlanChangeByName(
        [Description("Назва або частина назви схеми живлення, наприклад: 'висока продуктивність', 'економія', 'збалансована'")] string planName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(planName))
                    return "ERROR|Назва схеми живлення порожня.";

                var plans = ReadPowerPlans();

                var selected = plans.FirstOrDefault(p =>
                    p.Name.Contains(planName, StringComparison.OrdinalIgnoreCase) ||
                    planName.Contains(p.Name, StringComparison.OrdinalIgnoreCase));

                if (selected == null)
                {
                    string available = string.Join(", ", plans.Select(p => p.Name));
                    return $"NOT_FOUND|Не знайшов схему живлення «{planName}». Доступні: {available}";
                }

                _pendingPowerPlanGuid = selected.Guid;
                _pendingPowerPlanName = selected.Name;

                return $"APPROVE_REQUIRED|Змінити режим електроживлення на «{selected.Name}»? Напиши «так», щоб підтвердити.";
            }
            catch (Exception ex)
            {
                return $"ERROR|Помилка підготовки зміни живлення: {ex.Message}";
            }
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

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ коли юзер питає про автозавантаження, стартап або чому ПК довго вмикається. Показує програми автозапуску зі статусом увімкнено/вимкнено.")]
        public string GetStartupApps()
        {
            try
            {
                var startupItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_StartupCommand"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"]?.ToString()?.Trim() ?? "";
                        if (!string.IsNullOrWhiteSpace(name) && !startupItems.ContainsKey(name))
                            startupItems[name] = "зареєстровано";
                    }
                }

                var approvedStatuses = GetStartupApprovedStatuses();

                var sb = new StringBuilder();
                sb.AppendLine("Програми автозавантаження:");

                foreach (var item in startupItems.OrderBy(x => x.Key))
                {
                    string status = "статус невідомий";

                    var matched = approvedStatuses
                        .FirstOrDefault(x =>
                            x.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase) ||
                            x.Key.Contains(item.Key, StringComparison.OrdinalIgnoreCase) ||
                            item.Key.Contains(x.Key, StringComparison.OrdinalIgnoreCase));

                    if (!string.IsNullOrWhiteSpace(matched.Key))
                        status = matched.Value;

                    sb.AppendLine($"- {item.Key} — {status}");
                }

                if (startupItems.Count == 0)
                    return "Автозавантаження порожнє або недоступне для читання.";

                sb.AppendLine();
                sb.AppendLine("Примітка: це програми, зареєстровані в автозавантаженні. Частина з них може бути вимкнена в диспетчері задач.");

                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                return $"Помилка читання автозавантаження: {ex.Message}";
            }
        }

        private Dictionary<string, string> GetStartupApprovedStatuses()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string[] subKeys =
            {
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"
    };

            RegistryHive[] hives =
            {
        RegistryHive.CurrentUser,
        RegistryHive.LocalMachine
    };

            foreach (var hive in hives)
            {
                foreach (var subKey in subKeys)
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                        using var key = baseKey.OpenSubKey(subKey);

                        if (key == null) continue;

                        foreach (var valueName in key.GetValueNames())
                        {
                            var value = key.GetValue(valueName) as byte[];
                            if (value == null || value.Length == 0) continue;

                            // У StartupApproved перший байт часто означає стан:
                            // 0x02 — увімкнено, 0x03 — вимкнено
                            string status = value[0] switch
                            {
                                0x02 => "увімкнено",
                                0x03 => "вимкнено",
                                _ => "статус невідомий"
                            };

                            result[valueName] = status;
                        }
                    }
                    catch
                    {
                        // Ігноруємо недоступні гілки реєстру
                    }
                }
            }

            return result;
        }

        private bool _pendingMaxPower = false;
        private bool _pendingCloseBrowsers = false;

        [KernelFunction, Description("Перший етап оптимізації. ВИКЛИКАЙ ЦЕ коли юзер просить буст, ігровий режим або закрити браузери. Готує систему, але чекає підтвердження.")]
        public string RequestBrowserBoost(
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
        public string ConfirmBrowserBoost()
        {
            if (!_pendingMaxPower && !_pendingCloseBrowsers)
                return "ERROR|Немає підготовленого бусту. Спочатку треба викликати RequestBrowserBoost.";

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

        [KernelFunction, Description("Перший етап бусту. Аналізує реальні програми юзера (на робочому столі та важкі програми в треї), які можна закрити.")]
        public string AnalyzeBackgroundAppsForOptimization()
        {
            try
            {
                var myProcess = Process.GetCurrentProcess().ProcessName.ToLower();

                var activeApps = Process.GetProcesses()
                    .Where(p =>
                    {
                        try
                        {
                            string name = p.ProcessName.ToLower();

                            // 1. ЗАХИСТ: Не чіпаємо себе, Провідник та Ollama
                            if (name == myProcess || name == "explorer" || name.Contains("ollama")) return false;

                            // 2. ФІЛЬТР СИСТЕМИ: Відкидаємо все, що лежить у папці Windows
                            string path = p.MainModule?.FileName?.ToLower() ?? "";
                            if (path.StartsWith(@"c:\windows")) return false;

                            // 3. ЛОВИМО ТРЕЙ: Беремо ті, що мають вікно, АБО їдять більше 60 МБ (Discord, Steam, Telegram)
                            long memMb = p.WorkingSet64 / 1024 / 1024;
                            return p.MainWindowHandle != IntPtr.Zero || memMb > 60;
                        }
                        catch
                        {
                            // Якщо Access Denied — це системний процес рівня ядра/служб. Відкидаємо.
                            return false;
                        }
                    })
                    .GroupBy(p => p.ProcessName)
                    .Select(g => new
                    {
                        Name = g.Key,
                        MemoryMb = g.Sum(p => { try { return p.WorkingSet64; } catch { return 0; } }) / 1024 / 1024
                    })
                    .Where(p => p.MemoryMb > 50) // Фінальний фільтр дрібниць
                    .OrderByDescending(p => p.MemoryMb)
                    .ToList();

                if (activeApps.Count == 0)
                    return "SUCCESS|Важких фонових програм не знайдено. Можу тільки увімкнути Максимальну продуктивність живлення. Запитай юзера, чи вмикати.";

                var sb = new StringBuilder("SUCCESS|Я знайшов такі відкриті та фонові програми:\n");
                foreach (var app in activeApps)
                {
                    sb.AppendLine($"- {app.Name} ({app.MemoryMb} МБ)");
                }
                sb.AppendLine("Запитай у юзера: 'Які з цих програм закрити для бусту, чи закрити всі?'");

                return sb.ToString();
            }
            catch (Exception ex) { return $"ERROR|{ex.Message}"; }
        }

        [KernelFunction, Description("Другий етап бусту. Викликається ТІЛЬКИ після того, як юзер погодився на буст і сказав, що саме закривати.")]
        public string ExecuteSelectedAppsOptimization(
            [Description("Увімкнути макс. продуктивність живлення (true/false)")] bool enableMaxPower,
            [Description("Назви процесів для закриття через кому (наприклад: 'chrome,telegram'). Якщо нічого не треба закривати — передай порожній рядок.")] string appsToClose)
        {
            var sb = new StringBuilder("SUCCESS|\nЗвіт про буст:\n");

            if (enableMaxPower)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = "powercfg", Arguments = "/setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", CreateNoWindow = true, UseShellExecute = false });
                    sb.AppendLine("- Живлення: Макс. продуктивність [УВІМКНЕНО] ⚡");
                }
                catch { sb.AppendLine("- Живлення: Помилка доступу"); }
            }

            if (!string.IsNullOrWhiteSpace(appsToClose) && appsToClose.ToLower() != "none")
            {
                var apps = appsToClose.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).ToList();
                int closedTotal = 0;

                foreach (var appName in apps)
                {
                    int count = 0;
                    foreach (var p in Process.GetProcessesByName(appName))
                    {
                        try { p.Kill(); count++; closedTotal++; } catch { }
                    }
                    if (count > 0) sb.AppendLine($"- {appName}: закрито ({count} процесів) 🧹");
                }

                if (closedTotal == 0) sb.AppendLine("- Програми: Не вдалося закрити вказані програми.");
            }

            return sb.ToString().Trim();
        }

        [KernelFunction, Description("Перший етап очищення ПК. Аналізує тимчасові файли Windows і користувача, але нічого не видаляє без підтвердження.")]
        public string AnalyzeTempCleanup()
        {
            try
            {
                _pendingCleanupFiles.Clear();

                var tempFolders = new List<string>
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
        };

                long totalBytes = 0;
                int totalFiles = 0;

                foreach (var folder in tempFolders.Distinct())
                {
                    if (!Directory.Exists(folder)) continue;

                    foreach (var file in SafeEnumerateFiles(folder))
                    {
                        try
                        {
                            var info = new FileInfo(file);

                            // Щоб не чіпати активні тимчасові файли
                            if (info.LastWriteTime > DateTime.Now.AddDays(-1))
                                continue;

                            totalBytes += info.Length;
                            totalFiles++;
                            _pendingCleanupFiles.Add(file);

                            // Захист від дуже довгого аналізу
                            if (totalFiles >= 5000)
                                break;
                        }
                        catch { }
                    }
                }

                double totalMb = totalBytes / 1024.0 / 1024.0;

                if (totalFiles == 0)
                    return "EMPTY|Не знайшов безпечних тимчасових файлів для очищення.";

                return $"APPROVE_REQUIRED|Знайдено приблизно {totalFiles} тимчасових файлів на {totalMb:F1} МБ. Напиши «так», щоб очистити.";
            }
            catch (Exception ex)
            {
                _pendingCleanupFiles.Clear();
                return $"ERROR|Помилка аналізу очищення: {ex.Message}";
            }
        }

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ ТІЛЬКИ коли юзер просить технічну інформацію про конкретний процес: RAM, шлях, кількість процесів, ресурси. НЕ викликай, якщо юзер питає 'що таке Discord/Steam/Chrome' — тоді відповідай звичайним текстом.")]
        public string GetProcessDetails(
    [Description("Назва процесу без .exe, наприклад chrome, discord, steam")] string processName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(processName))
                    return "ERROR|Назва процесу порожня.";

                processName = processName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).Trim();

                var processes = Process.GetProcessesByName(processName);

                if (processes.Length == 0)
                    return $"NOT_FOUND|Процес {processName} не знайдено серед запущених.";

                long totalMemory = 0;
                var paths = new HashSet<string>();
                int windowCount = 0;

                foreach (var p in processes)
                {
                    try { totalMemory += p.WorkingSet64; } catch { }

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(p.MainModule?.FileName))
                            paths.Add(p.MainModule.FileName);
                    }
                    catch { }

                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                            windowCount++;
                    }
                    catch { }
                }

                double memoryMb = totalMemory / 1024.0 / 1024.0;

                var sb = new StringBuilder();
                sb.AppendLine($"Інформація про процес {processName}:");
                sb.AppendLine($"- Кількість процесів: {processes.Length}");
                sb.AppendLine($"- Використання RAM: {memoryMb:F0} МБ");
                sb.AppendLine($"- Вікон з інтерфейсом: {windowCount}");

                if (paths.Count > 0)
                {
                    sb.AppendLine("- Шлях:");
                    foreach (var path in paths.Take(3))
                        sb.AppendLine($"  {path}");
                }
                else
                {
                    sb.AppendLine("- Шлях: недоступний");
                }

                sb.AppendLine("- Закривати можна тільки після підтвердження користувача.");

                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                return $"ERROR|Помилка отримання інформації про процес: {ex.Message}";
            }
        }

        [KernelFunction, Description("Другий етап очищення ПК. Видаляє тільки ті тимчасові файли, які були знайдені через AnalyzeTempCleanup. Викликати тільки після підтвердження користувача.")]
        public string ConfirmTempCleanup()
        {
            try
            {
                if (_pendingCleanupFiles.Count == 0)
                    return "ERROR|Немає підготовленого очищення. Спочатку треба викликати AnalyzeTempCleanup.";

                int deleted = 0;
                long freedBytes = 0;

                foreach (var file in _pendingCleanupFiles.ToList())
                {
                    try
                    {
                        if (!File.Exists(file)) continue;

                        var info = new FileInfo(file);
                        long size = info.Length;

                        File.Delete(file);

                        deleted++;
                        freedBytes += size;
                    }
                    catch
                    {
                        // Файл може бути зайнятий системою — це нормально
                    }
                }

                _pendingCleanupFiles.Clear();

                double freedMb = freedBytes / 1024.0 / 1024.0;
                return $"SUCCESS|Очищення завершено. Видалено файлів: {deleted}. Звільнено приблизно {freedMb:F1} МБ.";
            }
            catch (Exception ex)
            {
                _pendingCleanupFiles.Clear();
                return $"ERROR|Помилка очищення: {ex.Message}";
            }
        }

        private IEnumerable<string> SafeEnumerateFiles(string root)
        {
            var files = new List<string>();

            try
            {
                files.AddRange(Directory.EnumerateFiles(root));
            }
            catch { }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    foreach (var file in SafeEnumerateFiles(dir))
                        files.Add(file);
                }
            }
            catch { }

            return files;
        }
    }
}