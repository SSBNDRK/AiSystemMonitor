using Microsoft.SemanticKernel;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Management;
using System.Text;

namespace AiSystemMonitor.Plugins
{
    class MaintenancePlugin
    {
        private List<string> _pendingCleanupFiles = new();

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
                        // Файл може бути зайнятий системою
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
