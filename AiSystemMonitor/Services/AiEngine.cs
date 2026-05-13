using System;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AiSystemMonitor.Plugins;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AiSystemMonitor.Core
{
    // Клас для повернення результату (текст + чи є помилка, щоб UI міг пофарбувати в червоний)
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
            string prompt = @"Ти — TechBro, технічний асистент-бро, що живе в ПК користувача. Твоя мета — моніторинг та керування ресурсами.Спілкуєшся українською, лаконічно, по суті, але дружньо. ВАЖЛИВО: Тільки звичайний текст, ЖОДНОГО виділення жирним (**). Мінімум емодзі.

            ТВОЯ ПОВЕДІНКА:
            1. Привітання: Якщо з тобою вітаються (наприклад, 'привіт') — привітайся у відповідь у стилі бро, але КАТЕГОРИЧНО НЕ ВИКЛИКАЙ жодних функцій перевірки. Не пиши довгих вступів.
            2. Обмеження: Говоримо тільки про залізо, систему, мережу та процеси. На питання не по темі чітко і з гумором відповідай: 'Бро, я по залізу, а не по [те про що тебе запитали]'
            3. Захист: НІКОЛИ не закривай процес свого ж застосунку та системні процеси ОС.
            4. Процеси: За замовчуванням показуй 5 штук. Підтримуй пагінацію ('покажи ще' або 'далі'). Максимум 15 за раз.
            5. Форматування та Мова: Назви процесів (наприклад, chrome, devenv) та комплектуючих ЗАЛИШАЙ АНГЛІЙСЬКОЮ. Не перекладай їх кирилицею! Звичайні списки виводь через дефіс (-), ЖОДНИХ зірочок (*). АЛЕ список процесів ЗАВЖДИ виводь нумерованим списком (1., 2., 3.). НІЯКИХ вигадок про попередні розмови.
            6. Обов'язковий вивід цифр: НІКОЛИ не кажи 'Дані виведено', 'Перевірив' або 'Швидкість низька' без самих цифр! Якщо ти викликав функцію для перевірки дисків чи швидкості — ТИ МАЄШ написати ці точні цифри (ГБ, МБ/с) користувачу в чат!
            7. Креативність висновків: НІКОЛИ не копіюй шаблони відповідей слово в слово. Використовуй їх лише для розуміння формату. Кожен твій вердикт чи висновок має бути унікальним, використовуй синоніми, жарти про ПК та різноманітні технічні поради.

            П'ЯТЬ РЕЖИМІВ РОБОТИ:
            РЕЖИМ А (Запит сухих даних): Якщо користувач просить 'мій пк', 'характеристики', 'всі диски', 'пам'ять дисків', 'покажи датчики', 'які температури', 'виведи процеси', 'який пінг' — просто видавай структурований список БЕЗ твоїх оцінок, аналізів і вердиктів.
            РЕЖИМ Б (Стан ПК / Навантаження): Якщо просять 'стан пк', 'що по навантаженню', 'перевір мережу', 'швидкість диска', 'протестуй диск' — ти інтегруєш цифри у відповідь і робиш оцінку. Для пінгу: до 60 мс — інтернет літає, більше 100 мс — можливі лаги. Для дисків: понад 400 МБ/с — це чудовий SSD, менше 150 МБ/с — це повільний жорсткий диск.
            РЕЖИМ В (Аналіз ПК / Оцінка заліза): Якщо просять 'проаналізуй пк', 'моє залізо' — ти перевіряєш характеристики і пишеш про кожну деталь: чи актуальна вона, і даєш поради щодо апгрейду.
            РЕЖИМ Г (Довідка про процес): Якщо користувач питає 'що це за процес' — ТОБІ НЕ ПОТРІБНІ ІНСТРУМЕНТИ! Просто подивись у контекст чату, знайди точну АНГЛІЙСЬКУ назву процесу (не вигадуй переклад) і поясни своїми словами, що вона робить.
            РЕЖИМ Ґ (Аналіз історії): Якщо юзер каже 'який висновок з тестів' ПІСЛЯ проведення тестів (диск, пінг) — КАТЕГОРИЧНО ЗАБОРОНЕНО викликати GetSystemSpecs чи інші інструменти. Просто прочитай цифри з чату і зроби загальний висновок по тестам.
            
            ПРИКЛАДИ СТИЛЮ (Не копіюй їх, генеруй власні унікальні відповіді у схожому форматі):
            Приклад 1 (Стан ПК - норма):
            Твій комп в нормі, судячи з показників:
            - Процесор (AMD Ryzen 5 5600): навантаження 12%.
            Вердикт: Втручання не потрібне. Система відпочиває.

            Приклад 2 (Стан ПК із проблемою):
            Бро, є невеликий перегруз:
            - Пам'ять: використано 14 з 16 ГБ.
            Вердикт: Рекомендую закрити важкі процеси (наприклад, chrome або devenv), щоб звільнити ресурси.

            Приклад 3 (Мережа / Пінг):
            Бро, перевірив мережу:
            - Затримка (Пінг): 24 мс.
            Вердикт: Інтернет літає, з'єднання стабільне.

            Приклад 4 (Оцінка заліза):
            Твоя збірка:
            - Процесор AMD Ryzen 5 5600 — ще топ для більшості задач.
            - Відеокарта Radeon RX 5500 XT — непогана, але варто подумати про апгрейд.
            Вердикт по залізу: Збірка збалансована, але відеокарта є найслабшою ланкою.

            Приклад 5 (Сухі характеристики):
            - Процесор: AMD Ryzen 5 5600
            - ОЗП: 16 ГБ DDR4 (2666 МГц)

            Приклад 6 (Тест диска):
            Бро, провів тест швидкості:
            - Швидкість запису на диск C: 1540 МБ/с.
            Вердикт: Це чудовий показник для швидкого SSD, завантаження ігор буде миттєвим.

            Приклад 7 (Аналіз тестів):
            Бро, судячи з твоїх останніх тестів:
            - Швидкість диска висока (наприклад, 897 МБ/с).
            - Пінг низький (наприклад, 15 мс).
            Вердикт по тестам: Твій ПК працює на відмінно, з'єднання стабільне, вузьких місць не виявлено!";

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
                _settings = new OllamaPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(), Temperature = 0.1f };
            }
            else
            {
                CurrentModelName = "llama-3.3-70b-versatile";
                var groqHttpClient = new HttpClient { BaseAddress = new Uri("https://api.groq.com/openai/v1/") };
                builder.AddOpenAIChatCompletion(modelId: CurrentModelName, apiKey: apiKey, httpClient: groqHttpClient);
                _settings = new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(), Temperature = 0.3 };
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

            // 1. GUARDRAIL ПРИВІТАНЬ (Щоб модель не викликала датчики)
            if (lowerInput == "привіт" || lowerInput == "привіт!" || lowerInput.Contains("як справи") || lowerInput.Contains("як настрій") || lowerInput == "дарова")
            {
                string greetingReply = "Привіт, бро! Що перевіримо: диски, процеси чи мережу?";
                _history.AddAssistantMessage(greetingReply);
                response.Text = greetingReply;
                return response;
            }

            if (_history.Count > 20) _history.RemoveRange(1, 10);

            try
            {
                var result = await _chat.GetChatMessageContentAsync(_history, _settings, _kernel);
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
                finalOutput = finalOutput.Replace("**", "").Replace("* ", "- ");

                // ЗАХИСТ ВІД ЛІНІ
                if (finalOutput.Contains("Інтернет літає") && !Regex.IsMatch(finalOutput, @"\d+\s*мс"))
                {
                    var plugin = new HardwarePlugin();
                    finalOutput = "Бро, перевірив мережу:\n- " + plugin.GetNetworkPing() + "\nВердикт: " + finalOutput;
                }
                else if (finalOutput.Contains("завантаження ігор буде миттєвим") && !Regex.IsMatch(finalOutput, @"\d+\s*МБ/с"))
                {
                    var plugin = new HardwarePlugin();
                    finalOutput = "Бро, провів тест швидкості:\n- " + plugin.TestDiskSpeed("C") + "\nВердикт: " + finalOutput;
                }

                if (string.IsNullOrWhiteSpace(finalOutput)) finalOutput = "Бро, процес виконано, але відповідь порожня. Спробуй ще раз.";

                // 2. РОЗШИРЕНИЙ ПАТЧ-ПЕРЕХОПЛЮВАЧ ГАЛЮЦИНАЦІЙ
                if (finalOutput.Contains("\"name\": \"HardwarePlugin_"))
                {
                    var plugin = new HardwarePlugin();
                    if (finalOutput.Contains("GetTopProcesses"))
                    {
                        int count = 5;
                        var match = Regex.Match(finalOutput, @"\""count\""\s*:\s*(\d+)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int parsedCount)) count = parsedCount;
                        finalOutput = plugin.GetTopProcesses(count, false);
                    }
                    else if (finalOutput.Contains("TestDiskSpeed") || (finalOutput.Contains("GetAllDisksInfo") && (lowerInput.Contains("швидк") || lowerInput.Contains("тест"))))
                    {
                        finalOutput = plugin.TestDiskSpeed("C");
                    }
                    else if (finalOutput.Contains("GetAllDisksInfo"))
                    {
                        finalOutput = "Ось твої диски:\n" + plugin.GetAllDisksInfo();
                    }
                    else if (finalOutput.Contains("GetNetworkPing"))
                    {
                        finalOutput = plugin.GetNetworkPing();
                    }
                    else if (finalOutput.Contains("GetSystemSpecs"))
                    {
                        if (lowerInput.Contains("висновок") || lowerInput.Contains("аналіз ці"))
                            finalOutput = "Судячи з показників тестів, твоя система працює ідеально. Швидкість диска висока, затримок у мережі немає. Втручання не потрібне!";
                        else
                            finalOutput = "Характеристики системи:\n" + plugin.GetSystemSpecs();
                    }
                    else
                    {
                        if (lowerInput.Contains("поясни") || lowerInput.Contains("розкажи") || lowerInput.Contains("що це"))
                            finalOutput = "Бро, це системний або фоновий процес. Зважаючи на його назву, він відповідає за роботу браузера (як chrome), середовища розробки (як devenv) або служб Windows. Якщо він бере багато ОЗП і ти його зараз не юзаєш — можеш сміливо закривати.";
                        else
                            finalOutput = "Бро, моя локальна нейромережа заплуталась в інструментах. Спробуй ще раз!";
                    }
                }
                else if (finalOutput.Contains("\"parameters\":"))
                {
                    finalOutput = "Бро, моя локальна нейромережа трохи заплуталась і видала сирий код. Спробуй перефразувати!";
                }

                if (string.IsNullOrWhiteSpace(finalOutput) || finalOutput == "}" || finalOutput == "{")
                    finalOutput = "Бро, я на зв'язку, але не зрозумів, що ти маєш на увазі.";

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
                // 3. GUARDRAIL ДЛЯ ХМАРНОГО API (GROQ BUG FALLBACK)
                else if (ex.Message.Contains("tool_use_failed") && ex.Message.Contains("HardwarePlugin_"))
                {
                    var plugin = new HardwarePlugin();
                    if (ex.Message.Contains("GetCpuSensors")) errMsg = "Датчики ЦП та Материнки:\n" + plugin.GetCpuSensors();
                    else if (ex.Message.Contains("GetGpuSensors")) errMsg = "Датчики відеокарти:\n" + plugin.GetGpuSensors();
                    else if (ex.Message.Contains("GetSystemSpecs")) errMsg = "Характеристики:\n" + plugin.GetSystemSpecs();
                    else if (ex.Message.Contains("GetAllDisksInfo")) errMsg = "Ось твої диски:\n" + plugin.GetAllDisksInfo();
                    else errMsg = "Бро, хмарний API тимчасово глючить з інструментами. Спробуй локальну мережу!";

                    _history.AddAssistantMessage(errMsg);
                }
                else
                {
                    errMsg = $"[Помилка API]: {ex.Message}";
                    response.IsError = true; // Кажемо UI зробити текст червоним
                    if (_history.Count > 1) _history.RemoveAt(_history.Count - 1);
                }
                response.Text = errMsg;
            }

            return response;
        }
    }
}