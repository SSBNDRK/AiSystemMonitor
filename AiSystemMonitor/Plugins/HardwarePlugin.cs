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

        [KernelFunction, Description("Kills a running process. CRITICAL: ONLY use this tool if the user EXPLICITLY commands you to close it (e.g., 'закрий chrome').")]
        public string KillProcess([Description("The exact name of the process, e.g., 'chrome' or 'telegram'")] string processName)
        {
            // 1. Захист від дурня: якщо ШІ випадково додав ".exe", відрізаємо його
            string cleanName = processName.ToLower().Replace(".exe", "").Trim();

            // 2. Розширений чорний список критичних процесів Windows
            string[] protectedProcesses = {
                "explorer", "ollama", "aisystemagent", "svchost", "wininit",
                "services", "dwm", "csrss", "lsass", "smss", "taskmgr", "system"
            };

            if (protectedProcesses.Contains(cleanName))
            {
                return $"Помилка: '{cleanName}' — це критичний системний процес. Я не можу його закрити задля безпеки ПК.";
            }

            try
            {
                // Використовуємо очищене ім'я
                var targets = Process.GetProcessesByName(cleanName);
                if (targets.Length == 0) return $"Помилка: Програма '{cleanName}' не запущена або такий процес не знайдено.";

                foreach (var p in targets) p.Kill();

                return $"Успіх: Процес {cleanName} успішно закрито.";
            }
            catch (Exception ex)
            {
                return $"Помилка при закритті: {ex.Message}";
            }
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
            return GetSensorsByType(HardwareType.Cpu, HardwareType.Motherboard, HardwareType.SuperIO);
        }

        [KernelFunction, Description("Отримує датчики ТІЛЬКИ відеокарти (GPU): температуру, навантаження, вентилятори.")]
        public string GetGpuSensors()
        {
            return GetSensorsByType(HardwareType.GpuNvidia, HardwareType.GpuAmd);
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