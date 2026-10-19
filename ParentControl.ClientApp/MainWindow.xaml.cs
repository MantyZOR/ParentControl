using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ParentControl.ClientApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Refresh();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (DependencyObject? element = e.OriginalSource as DependencyObject; element != null;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is Button) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Panel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var panel = (ScrollViewer)sender;
        for (DependencyObject? element = e.OriginalSource as DependencyObject; element != null && element != panel;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is not ScrollViewer inner) continue;
            if ((e.Delta > 0 && inner.VerticalOffset > 0.01) ||
                (e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight - 0.01))
                return;
            break;
        }

        var lines = SystemParameters.WheelScrollLines;
        var distance = lines < 0 ? panel.ViewportHeight : 16.0 * Math.Max(1, lines);
        panel.ScrollToVerticalOffset(AlignScrollOffset(panel, panel.VerticalOffset - e.Delta / 120.0 * distance));
        e.Handled = true;
    }

    private static double AlignScrollOffset(ScrollViewer panel, double offset)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(panel).DpiScaleY;
        var maxPixel = Math.Floor(panel.ScrollableHeight * pixelsPerDip);
        var pixel = Math.Round(offset * pixelsPerDip);
        return Math.Clamp(pixel, 0, maxPixel) / pixelsPerDip;
    }

    private void Panel_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        var panel = (ScrollViewer)sender;
        var aligned = AlignScrollOffset(panel, panel.VerticalOffset);
        if (Math.Abs(aligned - panel.VerticalOffset) * VisualTreeHelper.GetDpi(panel).DpiScaleY >= 0.25)
            panel.ScrollToVerticalOffset(aligned);
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Refresh()
    {
        try
        {
            var installed = SetupOperations.IsInstalled();
            var running = SetupOperations.IsRunning();
            var armed = SetupOperations.ProtectionArmed != false;
            ServiceText.Text = running ? armed
                ? SetupOperations.UserPaused ? "Служба работает · минуты на паузе" : "Служба работает · защита включена"
                : "Служба работает · защита пока выключена"
                : installed ? "Установлено · служба остановлена" : "Клиент ещё не установлен";
            ServiceText.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(running ? "#285F66" : "#9A584B"));
            IpText.Text = SetupOperations.IpAddresses();
            KeyText.Text = SetupOperations.PairingKey ?? "Появится после установки";
            var seconds = SetupOperations.BalanceSeconds;
            BalanceText.Text = seconds.HasValue ? $"Остаток банка: {(seconds.Value + 59) / 60} мин" : "Остаток банка: —";
            StartButton.IsEnabled = installed && !running;
            ArmButton.IsEnabled = installed && running && !armed;
            PauseButton.IsEnabled = installed && running && armed && !SetupOperations.UserPaused && seconds > 0;
            InstallButton.Content = installed ? "Обновить установку" : "Установить клиент";
            ErrorText.Text = "";
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false; StartButton.IsEnabled = false; ArmButton.IsEnabled = false;
        ServiceText.Text = "Установка…";
        try { await Task.Run(SetupOperations.Install); Refresh(); }
        catch (Exception ex) { ErrorText.Text = ex.Message; ServiceText.Text = "Ошибка установки"; }
        finally { InstallButton.IsEnabled = true; }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        ServiceText.Text = "Запуск службы…";
        try { await Task.Run(SetupOperations.Start); Refresh(); }
        catch (Exception ex) { ErrorText.Text = ex.Message; ServiceText.Text = "Ошибка запуска"; StartButton.IsEnabled = true; }
    }

    private async void Arm_Click(object sender, RoutedEventArgs e)
    {
        ArmButton.IsEnabled = false;
        ServiceText.Text = "Включение защиты…";
        try { await SetupOperations.Arm(); Refresh(); }
        catch (Exception ex) { ErrorText.Text = ex.Message; ServiceText.Text = "Не удалось включить защиту"; ArmButton.IsEnabled = true; }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        try { await SetupOperations.Pause(); Refresh(); }
        catch (Exception ex) { ErrorText.Text = ex.Message; Refresh(); ErrorText.Text = ex.Message; }
    }

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (SetupOperations.PairingKey is { } key) Clipboard.SetText(key);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
}
