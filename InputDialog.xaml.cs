using System.Windows;
using System.Windows.Input;

namespace FileExplorerCS;

public partial class InputDialog : Window
{
    public string Answer { get; private set; } = string.Empty;

    public InputDialog(string question, string defaultAnswer = "")
    {
        InitializeComponent();
        Title = question;
        PromptText.Text = question;
        InputTextBox.Text = defaultAnswer;
        InputTextBox.SelectAll();
        Loaded += (s, e) => InputTextBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Answer = InputTextBox.Text.Trim();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void InputTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            OkButton_Click(sender, e);
        }
    }
}
