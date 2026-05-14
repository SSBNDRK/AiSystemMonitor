using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using AiSystemMonitor.Plugins;

namespace AiSystemMonitor.Core
{
    public class AiResponse
    {
        public string Text { get; set; }
        public bool IsError { get; set; }
    }

    public class AiEngine
    {
        private Kernel _kernel;
        private IChatCompletionService _chat;
        private ChatHistory _history;
        private PromptExecutionSettings _settings;

        public string CurrentModelName { get; private set; } = "Ініціалізація...";

        public AiEngine()
        {
            // ЗОЛОТИЙ ПРОМТ ДЛЯ ПОТУЖНИХ МОДЕЛЕЙ (Без C#-перехоплювачів)
            string prompt = @"Ти — TechBro, AI-асистент для моніторингу ПК.

Відповідай українською.
Стиль: коротко, природньо, без markdown.

Ти НЕ маєш доступу до системних даних напряму.
Для:
- температур
- процесів
- GPU
- CPU
- RAM
- дисків
- ping
- характеристик ПК

ТИ ЗОБОВ'ЯЗАНИЙ викликати tool.

Не вигадуй системні дані.

Якщо потрібен tool:
- не пиши текст
- просто викликай tool

Після tool:
- коротко проаналізуй результат
- 1-2 речення максимум

Для process kill:
1. RequestProcessKill
2. чекати підтвердження
3. ConfirmProcessKill лише після 'так'

Працюєш лише з темами ПК та IT.";

            _history = new ChatHistory(prompt);
        }

        public async Task<bool> CheckOllamaIsAliveAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = await client.GetAsync("http://127.0.0.1:11434/");
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public void RebuildEngine(bool useLocal, string apiKey)
        {
            var builder = Kernel.CreateBuilder();
            if (useLocal)
            {
                CurrentModelName = "qwen3:8b";
                builder.AddOllamaChatCompletion(modelId: CurrentModelName, endpoint: new Uri("http://localhost:11434"));
                _settings = new OllamaPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Required(), Temperature = 0.2f };
            }
            else
            {
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new Exception("Бро, API ключ не може бути порожнім!");
                }

                if (apiKey.StartsWith("AIza"))
                {
                    CurrentModelName = "gemini-3.1-flash-lite";

                    var googleHttpClient = new HttpClient
                    {
                        BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/openai/"),
                        Timeout = TimeSpan.FromSeconds(30)
                    };

                    builder.AddOpenAIChatCompletion(
                        modelId: CurrentModelName,
                        apiKey: apiKey,
                        httpClient: googleHttpClient
                    );
                }
                else if (apiKey.StartsWith("gsk_")) // GROQ
                {
                    CurrentModelName = "llama-3.3-70b-versatile";
                    var groqHttpClient = new HttpClient { BaseAddress = new Uri("https://api.groq.com/openai/v1/") };
                    builder.AddOpenAIChatCompletion(modelId: CurrentModelName, apiKey: apiKey, httpClient: groqHttpClient);
                }
                else if (apiKey.StartsWith("sk-")) // OPENAI (ChatGPT)
                {
                    CurrentModelName = "gpt-4o-mini";
                    builder.AddOpenAIChatCompletion(modelId: CurrentModelName, apiKey: apiKey); // Для OpenAI не потрібен кастомний HttpClient
                }
                else
                {
                    throw new Exception("Невідомий формат ключа! Підтримуються формати: Google (AIza...), Groq (gsk_...) або OpenAI (sk-...).");
                }

                _settings = new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(), Temperature = 0.3 };
            }

            builder.Plugins.AddFromType<HardwarePlugin>();
            _kernel = builder.Build();
            _chat = _kernel.GetRequiredService<IChatCompletionService>();
        }

        public async Task<AiResponse> ProcessMessageAsync(string userInput)
        {
            var response = new AiResponse { IsError = false };
            _history.AddUserMessage(userInput);

            // КОНТРОЛЬ ПАМ'ЯТІ (SLIDING WINDOW)
            int maxHistorySize = 7;

            if (_history.Count > maxHistorySize)
            {
                // Вираховуємо, скільки зайвого накопичилося
                int itemsToRemove = _history.Count - maxHistorySize;

                // Видаляємо старі повідомлення, починаючи з індексу 1 (щоб НІКОЛИ не видалити системний промт на індексі 0)
                _history.RemoveRange(1, itemsToRemove);
            }

            try
            {
                var result = await _chat.GetChatMessageContentAsync(_history, _settings, _kernel);

                // Класичний цикл виклику інструментів (без перехоплювачів)
                while (result.Items.Any(i => i is FunctionCallContent))
                {
                    _history.Add(result);
                    foreach (var item in result.Items.OfType<FunctionCallContent>())
                    {
                        string functionResult = "Error: Tool execution failed.";
                        try
                        {
                            if (_kernel.Plugins.TryGetFunction("HardwarePlugin", item.FunctionName, out var function))
                            {
                                var context = new KernelArguments();
                                if (item.Arguments != null)
                                {
                                    foreach (var arg in item.Arguments) context[arg.Key] = arg.Value?.ToString();
                                }
                                var res = await function.InvokeAsync(_kernel, context);
                                functionResult = res.GetValue<string>() ?? "Success: Done.";
                            }
                        }
                        catch (Exception ex) { functionResult = $"Error: {ex.Message}"; }

                        var toolMessage = new ChatMessageContent(AuthorRole.Tool, content: functionResult);
                        toolMessage.Items.Add(new FunctionResultContent(item, functionResult));
                        _history.Add(toolMessage);
                    }

                    result = await _chat.GetChatMessageContentAsync(_history, _settings, _kernel);
                }

                string finalOutput = result.Content?.Trim() ?? "Збій генерації відповіді.";
                finalOutput = finalOutput.Replace("```json", "").Replace("```", "").Trim();

                if (finalOutput.Contains("\"name\": \"HardwarePlugin_") || finalOutput.Contains("\"parameters\":"))
                {
                    finalOutput = "Бро, я трохи заплутався в системних даних. Спробуй перефразувати запит!";
                }

                if (string.IsNullOrWhiteSpace(finalOutput)) finalOutput = "Бро, я на зв'язку, але не зрозумів запит.";

                _history.AddAssistantMessage(finalOutput);
                response.Text = finalOutput;
            }
            catch (Exception ex)
            {
                string errMsg;
                if (ex.Message.Contains("429") || ex.Message.Contains("rate_limit"))
                {
                    errMsg = "Я зараз трохи перевантажений запитами. Почекай 10 секунд!";
                }
                else
                {
                    errMsg = $"[Помилка API]: {ex.Message}";
                    response.IsError = true;
                    if (_history.Count > 1) _history.RemoveAt(_history.Count - 1);
                }
                response.Text = errMsg;
            }

            return response;
        }
    }
}