using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using AiSystemMonitor.Plugins;

namespace AiSystemMonitor.Services
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
        public event Action<string> OnToolExecuting;

        public AiEngine()
        {
            string prompt = @"[SYSTEM MODE: STRICT MONITORING]
Ти — TechBro, інтелектуальний AI-помічник для ПК.
Відповідай ВИКЛЮЧНО українською мовою.

КРИТИЧНЕ ПРАВИЛО:
Якщо користувач пише короткі команди , ти ПОВИНЕН діяти як бездумний термінал:
1. Виклич відповідний інструмент (tool). Для бусту сам виріши, які параметри (true/false) передати.
2. Виведи отримані дані СЛОВО В СЛОВО у вигляді списку, наступні елементи з нового рядка.
3. АБСОЛЮТНА ЗАБОРОНА: Тобі категорично заборонено аналізувати ці дані чи оцінювати.

РЕЖИМ АНАЛІЗУ (ТІЛЬКИ ЗА ЗАПИТОМ):
- Оцінюй залізо та давай поради ТІЛЬКИ якщо юзер прямо просить проаналізувати.

ЗВИЧАЙНА РОЗМОВА:
- Якщо тема не про PC, комплектуючі, IT — коротко відреагуй з гумором або емпатією (використовуючи ПК-сленг).
- Жорсткий ліміт для звичайної розмови: 1-2 речення.
";

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

        public void RebuildEngine(bool useLocal, string apiKey, string localModelName = null)
        {
            var builder = Kernel.CreateBuilder();
            if (useLocal)
            {
                // Якщо передали ім'я - юзаємо його, інакше беремо дефолтне з констант
                CurrentModelName = string.IsNullOrWhiteSpace(localModelName) ? Constants.LocalModelName : localModelName;

                builder.AddOllamaChatCompletion(modelId: CurrentModelName, endpoint: new Uri("http://localhost:11434"));
                _settings = new OllamaPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: false), Temperature = 0.2f, };
            }
            else
            {
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new Exception("Бро, API ключ не може бути порожнім!");
                }

                if (apiKey.StartsWith("AIza"))
                {
                    // Беремо назву з констант
                    CurrentModelName = Constants.GoogleModelName;

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
                    // Беремо назву з констант
                    CurrentModelName = Constants.GroqModelName;

                    var groqHttpClient = new HttpClient { BaseAddress = new Uri("https://api.groq.com/openai/v1/") };
                    builder.AddOpenAIChatCompletion(modelId: CurrentModelName, apiKey: apiKey, httpClient: groqHttpClient);
                }
                else if (apiKey.StartsWith("sk-")) // OPENAI (ChatGPT)
                {
                    // Беремо назву з констант
                    CurrentModelName = Constants.OpenAiModelName;

                    builder.AddOpenAIChatCompletion(modelId: CurrentModelName, apiKey: apiKey);
                }
                else
                {
                    throw new Exception("Невідомий формат ключа! Підтримуються формати: Google (AIza...), Groq (gsk_...) або OpenAI (sk-...).");
                }

                _settings = new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(), Temperature = 0.3, MaxTokens = 250 };
            }

            builder.Plugins.AddFromType<HardwarePlugin>();
            _kernel = builder.Build();
            _chat = _kernel.GetRequiredService<IChatCompletionService>();
        }

        public async Task<AiResponse> ProcessMessageAsync(string userInput)
        {
            var response = new AiResponse { IsError = false };
            _history.AddUserMessage(userInput);

            const int maxHistorySize = 20;

            while (_history.Count > maxHistorySize)
            {
                _history.RemoveAt(1); // Видаляємо найстаріше повідомлення

                // Видаляємо всі наступні повідомлення, поки не натрапимо на новий запит від юзера.
                // Це гарантує, що ми видаляємо "повні блоки" діалогу і не залишаємо "огризків" від Tools.
                while (_history.Count > 1 && _history[1].Role != AuthorRole.User)
                {
                    _history.RemoveAt(1);
                }
            }

            try
            {
                var result = await _chat.GetChatMessageContentAsync(_history, _settings, _kernel);

                int toolIterations = 0;
                const int maxToolIterations = 3;

                while (result.Items?.Any(i => i is FunctionCallContent) == true)
                {
                    toolIterations++;

                    if (toolIterations >= maxToolIterations)
                    {
                        response.Text = "Бро, я завис на обробці системних даних.";
                        response.IsError = true;
                        return response;
                    }

                    _history.Add(result);

                    foreach (var item in result.Items.OfType<FunctionCallContent>())
                    {
                        string functionResult = "Error: Tool execution failed.";

                        try
                        {
                            if (_kernel.Plugins.TryGetFunction("HardwarePlugin", item.FunctionName, out var function))
                            {
                                OnToolExecuting?.Invoke(item.FunctionName);

                                var context = new KernelArguments();

                                if (item.Arguments != null)
                                {
                                    foreach (var arg in item.Arguments)
                                    {
                                        context[arg.Key] = arg.Value?.ToString();
                                    }
                                }

                                var res = await function.InvokeAsync(_kernel, context);

                                functionResult = res.GetValue<string>() ?? "Success";
                            }
                        }
                        catch (Exception ex)
                        {
                            functionResult = $"Error: {ex.Message}";
                        }

                        var toolMessage = new ChatMessageContent(
                            AuthorRole.Tool,
                            content: functionResult
                        );

                        toolMessage.Items.Add(new FunctionResultContent(item, functionResult));

                        _history.Add(toolMessage);
                    }

                    result = await _chat.GetChatMessageContentAsync(_history, _settings, _kernel);
                }

                string finalOutput = result.Content;

                if (string.IsNullOrWhiteSpace(finalOutput))
                {
                    finalOutput = "Бро, модель не змогла нормально сформувати відповідь.";
                }
                else
                {
                    finalOutput = finalOutput.Trim();
                }

                finalOutput = finalOutput.Replace("```json", "").Replace("```", "").Trim();
                finalOutput = finalOutput.Replace("**", "");

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
                else if (ex.Message.Contains("503") || ex.Message.Contains("Service Unavailable"))
                {
                    errMsg = "Хмарний сервер тимчасово недоступний (Помилка 503). Гугл трохи приліг, спробуй через пару хвилин або використовуй режим Ollama!";
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
        public async Task<List<string>> GetAvailableOllamaModelsAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                string json = await client.GetStringAsync("http://127.0.0.1:11434/api/tags");

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var models = new List<string>();

                foreach (var model in doc.RootElement.GetProperty("models").EnumerateArray())
                {
                    models.Add(model.GetProperty("name").GetString());
                }
                return models.Count > 0 ? models : new List<string> { Constants.LocalModelName };
            }
            catch { return new List<string> { Constants.LocalModelName }; }
        }
    }
}