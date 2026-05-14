using System.Windows.Controls;

namespace AiSystemMonitor
{
    public partial class MainWindow
    {
        private readonly Dictionary<string, string> _slashCommands = new()
        {
            { "/мій пк", "Показати характеристики системи" },
            { "/процеси", "Вивести топ процесів (ОЗП)" },
            { "/датчики", "Температури CPU та GPU" },
            { "/диски", "Вільне місце на всіх дисках" },
            { "/пінг", "Перевірка затримки інтернету" }
        };


        // 1. Обробник тексту (Тепер автоматично виділяє першу команду)
        private void UserInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = UserInputBox.Text;

            if (text.StartsWith("/"))
            {
                var matches = _slashCommands.Where(k => k.Key.StartsWith(text.ToLower())).ToList();

                if (matches.Any())
                {
                    CommandListBox.Items.Clear();
                    foreach (var match in matches)
                    {
                        CommandListBox.Items.Add($"{match.Key} — {match.Value}");
                    }
                    CommandPopup.IsOpen = true;
                    CommandListBox.SelectedIndex = 0; // <--- АВТОВИДІЛЕННЯ ПЕРШОГО ПУНКТУ
                }
                else
                {
                    CommandPopup.IsOpen = false;
                }
            }
            else
            {
                CommandPopup.IsOpen = false;
            }
        }

        // 2. Клавіатура: Стрілки, Tab, Enter (Повністю нова логіка!)
        private async void UserInputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Якщо меню команд відкрите, перехоплюємо клавіші
            if (CommandPopup.IsOpen)
            {
                if (e.Key == System.Windows.Input.Key.Down)
                {
                    if (CommandListBox.SelectedIndex < CommandListBox.Items.Count - 1)
                        CommandListBox.SelectedIndex++;
                    e.Handled = true;
                    return;
                }
                else if (e.Key == System.Windows.Input.Key.Up)
                {
                    if (CommandListBox.SelectedIndex > 0)
                        CommandListBox.SelectedIndex--;
                    e.Handled = true;
                    return;
                }
                else if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Tab)
                {
                    if (CommandListBox.SelectedItem != null)
                    {
                        string selected = CommandListBox.SelectedItem.ToString();

                        // ФІКС БАГУ "/мій пк": тепер ділимо рядок по нашому роздільнику " — "
                        string command = selected.Split(new[] { " — " }, StringSplitOptions.None)[0].Trim();

                        UserInputBox.Text = command + " "; // Одразу додаємо пробіл в кінці!
                        UserInputBox.CaretIndex = UserInputBox.Text.Length; // Ставимо курсор в кінець

                        CommandPopup.IsOpen = false;
                        e.Handled = true; // Блокуємо моментальну відправку повідомлення
                        return;
                    }
                }
                else if (e.Key == System.Windows.Input.Key.Escape)
                {
                    CommandPopup.IsOpen = false;
                    e.Handled = true;
                    return;
                }
            }

            // Якщо меню закрито, Enter просто відправляє повідомлення
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                await ProcessUserMessage();
            }
        }

        // 3. Клік мишкою (Також з фіксом для "/мій пк")
        private void CommandListBox_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (CommandListBox.SelectedItem != null)
            {
                string selected = CommandListBox.SelectedItem.ToString();
                string command = selected.Split(new[] { " — " }, StringSplitOptions.None)[0].Trim();

                UserInputBox.Text = command + " ";
                UserInputBox.CaretIndex = UserInputBox.Text.Length;

                CommandPopup.IsOpen = false;
                CommandListBox.SelectedItem = null;
                UserInputBox.Focus();
            }
        }
    }
}
