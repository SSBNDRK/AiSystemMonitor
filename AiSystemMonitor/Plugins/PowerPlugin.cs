using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AiSystemMonitor.Plugins
{
    class PowerPlugin
    {
        private string _pendingPowerPlanGuid = null;
        private string _pendingPowerPlanName = null;
        private bool _pendingMaxPower = false;
        private bool _pendingCloseBrowsers = false;

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
    }
}
