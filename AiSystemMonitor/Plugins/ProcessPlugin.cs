using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AiSystemMonitor.Plugins
{
    class ProcessPlugin
    {
        private static int _lastProcessSkip = 0;
        private string _pendingKillProcessName = null;
        public static List<string> LastProcesses = new();

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
    }
}
