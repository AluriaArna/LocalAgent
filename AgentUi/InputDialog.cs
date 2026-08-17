using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AgentUi;

public class InputDialog : Window
{
    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        Title = title;
        Width = 400;
        Height = 160;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var textBox = new TextBox { Text = defaultValue, Margin = new Thickness(0, 0, 0, 10) };

        var ok = new Button { Content = "OK", Width = 90, IsDefault = true };
        ok.Click += (_, _) => Close(textBox.Text);

        var cancel = new Button { Content = "Отмена", Width = 90, Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(15),
            Children =
            {
                new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) },
                textBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok }
                }
            }
        };
    }
}