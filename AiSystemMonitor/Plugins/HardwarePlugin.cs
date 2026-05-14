using Microsoft.SemanticKernel;
using System.ComponentModel;
using LibreHardwareMonitor.Hardware;
using System.Diagnostics;
using System.Management;
using System.Text;
using System.Linq;
using System.IO;

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
                // Використовуємо сервер Google (8.8.8.8) як еталон для перевірки інтернету
                using var pingSender = new System.Net.NetworkInformation.Ping();
                var reply = pingSender.Send("8.8.8.8", 1500); // 1.5 секунди на очікування відповіді

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

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ для перевірки вільного місця на ОДНОМУ конкретному диску. НЕ для швидкості.")]
        public string GetDiskInfo([Description("Літера диска, наприклад 'C'")] string driveLetter)
        {
            if (char.IsDigit(driveLetter[0])) return "Error: Invalid drive letter.";

            string cleanName = driveLetter.Trim().ToUpper().Replace(":", "");
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.Name.StartsWith(cleanName));

            if (drive != null)
            {
                double free = drive.TotalFreeSpace / 1024.0 / 1024.0 / 1024.0;
                double total = drive.TotalSize / 1024.0 / 1024.0 / 1024.0;

                // Повертаємо сухі дані
                return $"{free:F1}GB free / {total:F1}GB total";
            }
            return "Error: Drive not found.";
        }

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ ТІЛЬКИ коли юзер прямо просить 'тест швидкості', 'протестуй диск' або 'швидкість диска'. Повертає МБ/с.")]
        public string TestDiskSpeed([Description("Літера диска, наприклад 'C'")] string driveLetter = "C")
        {
            try
            {
                // Очищаємо літеру диска (якщо бот передасть "C:" або просто "C")
                string drive = driveLetter.Replace(":", "").Trim().ToUpper() + ":\\";
                if (!Directory.Exists(drive)) return $"Помилка: Диск {drive} не знайдено.";

                string tempFilePath = Path.Combine(drive, "techbro_speedtest.tmp");

                // Створюємо буфер на 10 МБ і заповнюємо випадковими даними (щоб SSD не читерив)
                int bufferSize = 10 * 1024 * 1024;
                byte[] buffer = new byte[bufferSize];
                new Random().NextBytes(buffer);

                int writes = 20; // Пишемо 20 разів по 10 МБ = 200 МБ загалом
                int totalMb = (bufferSize * writes) / (1024 * 1024);

                // Запускаємо секундомір
                var sw = Stopwatch.StartNew();

                // FileOptions.WriteThrough змушує систему писати прямо на диск, минаючи кеш Windows
                using (var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.WriteThrough))
                {
                    for (int i = 0; i < writes; i++)
                    {
                        fs.Write(buffer, 0, buffer.Length);
                    }
                }

                sw.Stop();

                // Прибираємо за собою
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

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ ТІЛЬКИ для перевірки ВІЛЬНОГО МІСЦЯ на дисках ('всі диски', 'пам'ять'). КАТЕГОРИЧНО НЕ ВИКЛИКАЙ, якщо користувач просить перевірити швидкість!")]
        public string GetAllDisksInfo()
        {
            try
            {
                var sb = new StringBuilder();
                // Шукаємо всі готові до роботи диски
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    double free = drive.TotalFreeSpace / 1024.0 / 1024.0 / 1024.0;
                    double total = drive.TotalSize / 1024.0 / 1024.0 / 1024.0;
                    sb.AppendLine($"- Диск {drive.Name[0]}: вільно {free:F1}ГБ з {total:F1}ГБ");
                }
                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        // Додаємо статичну змінну для збереження позиції
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
                .Skip(_lastProcessSkip) // Пропускаємо ті, що вже показали
                .Take(count)
                .ToList();

            var lines = new List<string>();
            string header = isNextPage ? "Наступні процеси (споживання ОЗП):" : "Топ процесів (споживання ОЗП):";
            lines.Add(header);
            for (int i = 0; i < topProcesses.Count; i++)
            {
                var p = topProcesses[i];
                LastProcesses.Add(p.Name);
                // Зберігаємо правильну нумерацію
                lines.Add($"{_lastProcessSkip + i + 1}. {p.Name} — {(p.Memory / 1024 / 1024):F0} МБ");
            }

            _lastProcessSkip += count;
            return string.Join("\n", lines);
        }

        [KernelFunction, Description("Gets the name and current status of the GPU (Video Card).")]
        public string GetGpuStatus()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("select * from Win32_VideoController");
                var gpu = searcher.Get().Cast<ManagementObject>().FirstOrDefault();

                if (gpu != null)
                {
                    string name = gpu["Name"]?.ToString() ?? "Unknown GPU";
                    string status = gpu["Status"]?.ToString() ?? "Unknown";

                    // Суха констатація фактів
                    return $"{name} | Status: {status}";
                }
                return "Error: GPU not found.";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
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
                        // Відрізаємо зайвий рекламний текст процесора
                        string cpuName = cpu["Name"]?.ToString().Replace("6-Core Processor", "").Trim();
                        sb.AppendLine($"Процесор: {cpuName} ({cpu["NumberOfCores"]} ядер, {cpu["NumberOfLogicalProcessors"]} потоків, Сокет: {socket})");
                    }
                }

                using (var searcher = new ManagementObjectSearcher("select Product, Manufacturer from Win32_BaseBoard"))
                {
                    var board = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (board != null)
                    {
                        // Скорочуємо назву компанії і слово "Материнська плата"
                        string manufacturer = board["Manufacturer"]?.ToString().Replace("Technology Co., Ltd.", "").Trim();
                        sb.AppendLine($"Материнка: {manufacturer} {board["Product"]}");
                    }
                }

                // --- БЛОК ОЗП З ВИЗНАЧЕННЯМ DDR ---
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

                        // Визначаємо тип пам'яті (26 = DDR4, 34 = DDR5, 24 = DDR3)
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

                // ОНОВЛЕНО: Тепер ми витягуємо точний обсяг пам'яті відеокарти (VRAM)
                using (var searcher = new ManagementObjectSearcher("select Name, AdapterRAM from Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string gpuName = obj["Name"]?.ToString() ?? "Unknown GPU";
                        string vramInfo = "";
                        if (obj["AdapterRAM"] != null)
                        {
                            // AdapterRAM повертає байти. Переводимо в Гігабайти.
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

        [KernelFunction, Description("ВИКЛИКАЙ ЦЕ ТІЛЬКИ коли юзер питає про 'датчики', 'температуру' або 'навантаження процесора'. НЕ викликай для 'характеристик'.")]
        public string GetCpuSensors()
        {
            var sb = new StringBuilder();
            var computer = new Computer { IsCpuEnabled = true };
            try
            {
                computer.Open();
                foreach (var hardware in computer.Hardware)
                {
                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        hardware.Update();
                        sb.AppendLine($"[CPU: {hardware.Name}]");
                        foreach (var sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                            {
                                // Фільтруємо лише Core датчики, ігноруючи системне сміття
                                if (sensor.Name.Contains("Core") || sensor.Name.Contains("Package") || sensor.Name.Contains("Tctl"))
                                {
                                    sb.AppendLine($"- Температура {sensor.Name}: {sensor.Value.Value:F1} °C");
                                }
                            }
                            if (sensor.SensorType == SensorType.Load && sensor.Value.HasValue)
                            {
                                if (sensor.Name.Contains("Total"))
                                {
                                    sb.AppendLine($"- Загальне навантаження: {sensor.Value.Value:F1} %");
                                }
                            }
                        }
                    }
                }
                computer.Close();
                string result = sb.ToString().Trim();
                return string.IsNullOrEmpty(result) ? "Датчики CPU не знайдені." : result;
            }
            catch (Exception ex) { return $"Помилка датчиків CPU: {ex.Message}"; }
        }

        [KernelFunction, Description("Отримує датчики ТІЛЬКИ відеокарти (GPU): температуру, навантаження, вентилятори.")]
        public string GetGpuSensors()
        {
            var sb = new StringBuilder();
            var computer = new Computer { IsGpuEnabled = true };
            try
            {
                computer.Open();
                foreach (var hardware in computer.Hardware)
                {
                    if (hardware.HardwareType == HardwareType.GpuAmd || hardware.HardwareType == HardwareType.GpuNvidia)
                    {
                        hardware.Update();
                        sb.AppendLine($"[GPU: {hardware.Name}]");
                        foreach (var sensor in hardware.Sensors)
                        {
                            if ((sensor.SensorType == SensorType.Temperature || sensor.SensorType == SensorType.Load || sensor.SensorType == SensorType.Fan) && sensor.Value.HasValue)
                            {
                                string unit = sensor.SensorType == SensorType.Temperature ? "°C" : (sensor.SensorType == SensorType.Load ? "%" : "RPM");
                                sb.AppendLine($"- {sensor.Name}: {sensor.Value.Value:F1} {unit}");
                            }
                        }
                    }
                }
                computer.Close();
                return sb.ToString().Trim();
            }
            catch (Exception ex) { return $"Помилка датчиків GPU: {ex.Message}"; }
        }

        [KernelFunction, Description("Перший етап закриття процесу: перевірка безпеки та розрахунок пам'яті. НІКОЛИ не закриває процес одразу.")]
        public string RequestProcessKill(
        [Description("Назва процесу (наприклад, chrome, discord)")] string processName)
        {
            try
            {
                // Список недоторканних процесів
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

                // Повертаємо спеціальний маркер для нашого AiEngine
                return $"APPROVE_REQUIRED|{processName}|{memoryMb:F0}";
            }
            catch (Exception ex)
            {
                return $"ERROR|Помилка при підготовці: {ex.Message}";
            }
        }

        [KernelFunction, Description("Другий етап закриття процесу: Остаточне закриття процесу. Викликається ТІЛЬКИ після явного 'Так' від користувача.")]
        public string ConfirmProcessKill(
            [Description("Назва процесу для закриття")] string processName)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                int count = processes.Length;
                foreach (var p in processes)
                {
                    p.Kill();
                }
                return $"SUCCESS|Я успішно закрив {count} процес(ів) {processName}. Тепер твоєму ПК дихається легше!";
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
                    // Рядок з кодуванням 866 видалено!
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
                _pendingPowerPlanGuid = null; // Очищаємо пам'ять після успіху
                _pendingPowerPlanName = null;

                return $"SUCCESS: Схему живлення успішно змінено на {name}.";
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }

        // Приватний хелпер, щоб не дублювати код
        private string GetSensorsByType(params HardwareType[] targetTypes)
        {
            var sb = new StringBuilder();
            var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsControllerEnabled = true };
            try
            {
                computer.Open();
                foreach (var hardware in computer.Hardware)
                {
                    if (targetTypes.Contains(hardware.HardwareType))
                    {
                        hardware.Update();
                        foreach (var sensor in hardware.Sensors)
                        {
                            if ((sensor.SensorType == SensorType.Temperature || sensor.SensorType == SensorType.Load) && sensor.Value.HasValue && sensor.Value.Value > 0)
                            {
                                string unit = sensor.SensorType == SensorType.Temperature ? "°C" : "%";
                                sb.AppendLine($"{hardware.Name} - {sensor.Name}: {sensor.Value.Value:F1} {unit}");
                            }
                        }
                    }
                }
                computer.Close();
                return sb.ToString().Trim();
            }
            catch (Exception ex) { return $"Помилка датчиків: {ex.Message}"; }
        }
    }
}