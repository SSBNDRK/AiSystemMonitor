using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace AiSystemMonitor.Plugins
{
    class NetworkPlugin
    {
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
    }
}
