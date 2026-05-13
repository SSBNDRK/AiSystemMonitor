using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using AiSystemMonitor.Plugins; // Підключаємо нашу папку з плагінами

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
            // НОВИЙ AGENTIC ПРОМТ: Мінімум шаблонів, максимум аналітики
            string prompt = @"Ти — TechBro, ШІ-асистент з моніторингу ПК та системний аналітик.
            Спілкуєшся українською, лаконічно, але як жива людина (бро-стиль). ЖОДНОГО виділення жирним (**).

            ТВІЙ АЛГОРИТМ РОБОТИ (ГІБРИДНИЙ ПІДХІД):
            1. ВИБІР ІНСТРУМЕНТУ: Якщо тебе питають про стан системи (процеси, диски, характеристики, пінг, швидкість) — ТИ ЗОБОВ'ЯЗАНИЙ спочатку викликати відповідний інструмент. НІКОЛИ не вигадуй цифри.
            2. АНАЛІЗ: Отримавши дані від інструменту, не просто виведи їх, а ПРОАНАЛІЗУЙ. (Наприклад: якщо швидкість диска 800 МБ/с - скажи, що це швидкий SSD; якщо Хром бере 2ГБ - поясни, що це через вкладки).
            3. ВЛАСНІ ЗНАННЯ: Якщо тебе просять пояснити, що робить процес (наприклад, 'що таке explorer') — не викликай інструменти. Просто поясни це своїми словами, опираючись на свої знання ОС Windows.
            4. ОБМЕЖЕННЯ: Говоримо тільки про ПК та IT. На питання про погоду, кулінарію чи фікуси жартівливо відповідай: 'Бро, я по залізу, а не по [те про що питали]'.

            ФОРМАТУВАННЯ:
            - Назви процесів та заліза залишай англійською.
            - Списки завжди виводь через дефіс (-). Жодних зірочок.
            - Якщо виводиш топ процесів — завжди нумеруй їх (1., 2., 3.).
            - Забудь про жорсткі шаблони. Кожна твоя відповідь має бути унікальною.";

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
                CurrentModelName = "llama3.1:latest";
                builder.AddOllamaChatCompletion(modelId: CurrentModelName, endpoint: new Uri("http://localhost:11434"));
                _settings = new OllamaPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(), Temperature = 0.3f }; // Трохи підняли температуру для креативності
            }
            else
            {
                CurrentModelName = "llama-3.3-70b-versatile";
                var groqHttpClient = new HttpClient { BaseAddress = new Uri("https://api.groq.com/openai/v1/") };
                builder.AddOpenAIChatCompletion(modelId: CurrentModelName, apiKey: apiKey, httpClient: groqHttpClient);
                _settings = new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(), Temperature = 0.4 };
            }

            builder.Plugins.AddFromType<HardwarePlugin>();
            _kernel = builder.Build();
            _chat = _kernel.GetRequiredService<IChatCompletionService>();
        }

        public async Task<AiResponse> ProcessMessageAsync(string userInput)
        {
            var response = new AiResponse { IsError = false };
            string lowerInput = userInput.ToLower().Trim();

            _history.AddUserMessage(userInput);

            // GUARDRAIL 1: Швидке привітання (щоб не ганяти ШІ дарма)
            if (lowerInput == "привіт" || lowerInput == "привіт!" || lowerInput.Contains("як справи") || lowerInput == "дарова")
            {
                string greetingReply = "Привіт, бро! Системи в нормі. Що перевіримо: диски, процеси чи мережу?";
                _history.AddAssistantMessage(greetingReply);
                response.Text = greetingReply;
                return response;
            }

            if (_history.Count > 20) _history.RemoveRange(1, 10);

            try
            {
                // КРОК 1: LLM визначає намір (Intent) і каже, які інструменти потрібні
                var result = await _chat.GetChatMessageContentAsync(_history, _settings, _kernel);

                // КРОК 2: C# безпечно виконує ці інструменти
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

                        // Додаємо СИРІ ДАНІ в історію
                        var toolMessage = new ChatMessageContent(AuthorRole.Tool, content: functionResult);
                        toolMessage.Items.Add(new FunctionResultContent(item, functionResult));
                        _history.Add(toolMessage);
                    }

                    // КРОК 3: LLM отримує сирі дані, аналізує їх і генерує фінальний текст "від себе"
                    result = await _chat.GetChatMessageContentAsync(_history, _settings, _kernel);
                }

                // Витягуємо фінальний текст від нейромережі
                string finalOutput = result.Content?.Trim() ?? "Збій генерації відповіді.";

                // Мінімальна косметика (видаляємо markdown-артефакти)
                finalOutput = finalOutput.Replace("```json", "").Replace("```", "").Trim();
                finalOutput = finalOutput.Replace("**", "");

                // Якщо локальна модель збожеволіла і видала JSON замість тексту
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
                // GUARDRAIL 2: Порятунок від багів хмарного Groq API
                else if (ex.Message.Contains("tool_use_failed") && ex.Message.Contains("HardwarePlugin_"))
                {
                    var plugin = new HardwarePlugin();
                    if (ex.Message.Contains("GetCpuSensors")) errMsg = "Датчики ЦП та Материнки:\n" + plugin.GetCpuSensors();
                    else if (ex.Message.Contains("GetGpuSensors")) errMsg = "Датчики відеокарти:\n" + plugin.GetGpuSensors();
                    else if (ex.Message.Contains("GetSystemSpecs")) errMsg = "Характеристики:\n" + plugin.GetSystemSpecs();
                    else if (ex.Message.Contains("GetAllDisksInfo")) errMsg = "Ось твої диски:\n" + plugin.GetAllDisksInfo();
                    else errMsg = "Бро, хмарний API тимчасово глючить. Спробуй локальну мережу!";

                    _history.AddAssistantMessage(errMsg);
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