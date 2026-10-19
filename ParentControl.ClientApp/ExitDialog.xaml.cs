using System.Windows;
using System.Windows.Input;
using Application = System.Windows.Application;

namespace ParentControl.ClientApp;

public partial class ExitDialog : Window
{
    public ExitDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Password_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Exit_Click(sender, e);
    }

    private async void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (!ExitButton.IsEnabled) return;
        ExitButton.IsEnabled = false;
        ErrorText.Text = "Проверка пароля…";
        try
        {
            var result = await ClientTray.RequestExit(PasswordInput.Password);
            PasswordInput.Clear();
            if (result.Ok)
            {
                Close();
                Application.Current.Shutdown();
            }
            else ErrorText.Text = result.Error;
        }
        catch (Exception ex) { ErrorText.Text = "Нет связи со службой: " + ex.Message; }
        finally { ExitButton.IsEnabled = true; }
    }
}
