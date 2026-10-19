using System.Windows;
using System.Windows.Threading;

namespace ParentControl.ClientApp;

public partial class WarningWindow : Window
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };

    public WarningWindow(long minutes)
    {
        InitializeComponent();
        MinutesText.Text = $"Осталось {minutes} мин";
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, area.Right - ActualWidth - 22);
            Top = Math.Max(area.Top, area.Bottom - ActualHeight - 22);
            timer.Start();
        };
        timer.Tick += (_, _) => Close();
        MouseEnter += (_, _) => timer.Stop();
        MouseLeave += (_, _) => timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        timer.Stop();
        try { await SetupOperations.Pause(); Close(); }
        catch (Exception ex) { ErrorText.Text = ex.Message; PauseButton.IsEnabled = true; timer.Start(); }
    }
}
