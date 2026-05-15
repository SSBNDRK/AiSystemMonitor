using System.IO;

namespace AiSystemMonitor.Services
{
    public static class AppConfig
    {
        public static string ApiKey { get; set; } = "";
        public static string OllamaModel { get; set; } = "";

        public static void Load()
        {
            if (File.Exists("apikey.txt"))
                ApiKey = File.ReadAllText("apikey.txt").Trim();

            if (File.Exists("ollamamodel.txt"))
                OllamaModel = File.ReadAllText("ollamamodel.txt").Trim();
        }

        public static void Save()
        {
            File.WriteAllText("apikey.txt", ApiKey);
            if (!string.IsNullOrEmpty(OllamaModel) && OllamaModel != "Завантаження...")
                File.WriteAllText("ollamamodel.txt", OllamaModel);
        }
    }
}