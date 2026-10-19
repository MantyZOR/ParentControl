using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;

namespace ParentControl.Overlay;

public partial class MainWindow : Window
{
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool canClose;
    private readonly bool primary;
    private readonly Mutex? overlayMutex;
    private string? pendingPower;
    private string rulesMarkdown = "";
    private bool showingRules;
    private bool rulesUnread;
    private bool acknowledgingRules;
    private long rulesRevision;

    public MainWindow() : this(Forms.Screen.PrimaryScreen!, true) { }
    private MainWindow(Forms.Screen screen, bool primary)
    {
        this.primary = primary;
        if (primary) overlayMutex = new Mutex(true, "Global\\ParentControlOverlay", out _);
        InitializeComponent();
        RulesDocumentViewer.Document = MarkdownDocument.Create("");
        if (!primary)
        {
            PowerControls.Visibility = Visibility.Collapsed;
            AdminEntry.Visibility = Visibility.Collapsed;
            PromoInput.IsEnabled = false;
        }
        Left = screen.Bounds.Left; Top = screen.Bounds.Top; Width = screen.Bounds.Width; Height = screen.Bounds.Height;
        SourceInitialized += (_, _) =>
        {
            var bounds = screen.Bounds;
            SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
                bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0004 | 0x0010);
        };
        if (primary)
        {
            Loaded += async (_, _) =>
            {
                foreach (var other in Forms.Screen.AllScreens.Where(s => !s.Primary))
                {
                    var mirror = new MainWindow(other, false);
                    mirror.Show();
                }
                await LoadBackground();
                await Refresh(); timer.Tick += async (_, _) => await Refresh(); timer.Start();
            };
        }
    }

    private async Task LoadBackground()
    {
        try
        {
            var bytes = await http.GetByteArrayAsync("http://localhost:48731/public/background");
            var image = new BitmapImage();
            image.BeginInit(); image.StreamSource = new MemoryStream(bytes); image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit();
            BackgroundImage.Source = image;
            foreach (var window in Application.Current.Windows.OfType<MainWindow>()) window.BackgroundImage.Source = image;
        }
        catch { }
    }

    private async Task Refresh()
    {
        try
        {
            var json = await http.GetStringAsync("http://localhost:48731/public/status");
            using var document = JsonDocument.Parse(json);
            var allowed = document.RootElement.GetProperty("allowed").GetBoolean();
            var seconds = document.RootElement.GetProperty("seconds").GetInt64();
            var display = $"{(seconds + 59) / 60} мин";
            var paused = document.RootElement.TryGetProperty("paused", out var pauseProperty) && pauseProperty.GetBoolean();
            var rules = document.RootElement.TryGetProperty("rulesMarkdown", out var rulesProperty)
                ? rulesProperty.GetString() ?? "" : "";
            var revision = document.RootElement.TryGetProperty("rulesRevision", out var revisionProperty)
                ? revisionProperty.GetInt64() : 0;
            var unread = document.RootElement.TryGetProperty("rulesUnread", out var unreadProperty) &&
                unreadProperty.GetBoolean();
            foreach (var window in Application.Current.Windows.OfType<MainWindow>())
            {
                window.SetRulesMarkdown(rules);
                window.SetRulesNotification(unread && !window.showingRules, revision);
                if (unread && window.showingRules) _ = window.AcknowledgeRules();
                window.RemainingText.Text = display;
                window.ModeBadge.Text = paused ? "PARENTCONTROL  /  ВАША ПАУЗА" : "PARENTCONTROL  /  ДОСТУП ОГРАНИЧЕН";
                window.TitleText.Text = paused ? "Минуты сохранены" : "Время для отдыха";
                window.DescriptionText.Text = paused
                    ? "Банк времени остановлен. Нажмите «Продолжить работу», когда будете готовы."
                    : "Компьютер станет доступен, когда администратор добавит время или наступит разрешённый период.";
                window.ResumeButton.Visibility = paused && window.primary ? Visibility.Visible : Visibility.Collapsed;
            }
            if (allowed) CloseAuthorizedOverlay();
            else { Topmost = true; Activate(); }
        }
        catch { MessageText.Text = "Нет связи с локальной службой. Доступ временно закрыт."; }
    }

    private void SetRulesMarkdown(string markdown)
    {
        if (rulesMarkdown == markdown) return;
        rulesMarkdown = markdown;
        RulesDocumentViewer.Document = MarkdownDocument.Create(markdown);
    }

    private void SetRulesNotification(bool unread, long revision)
    {
        rulesRevision = revision;
        if (rulesUnread == unread) return;
        rulesUnread = unread;
        if (unread)
        {
            RulesEcho.Visibility = Visibility.Visible;
            var duration = TimeSpan.FromSeconds(2.2);
            RulesEcho.BeginAnimation(OpacityProperty, new DoubleAnimation(0.82, 0.12, duration)
            { RepeatBehavior = RepeatBehavior.Forever });
            RulesEchoShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, 12, duration) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            RulesEcho.BeginAnimation(OpacityProperty, null);
            RulesEchoShift.BeginAnimation(TranslateTransform.XProperty, null);
            RulesEcho.Visibility = Visibility.Collapsed;
        }
    }

    private async Task AcknowledgeRules()
    {
        if (acknowledgingRules || rulesRevision <= 0) return;
        acknowledgingRules = true;
        var revision = rulesRevision;
        try
        {
            using var response = await http.PostAsync("http://localhost:48731/public/rules-seen",
                new StringContent(revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Encoding.UTF8, "text/plain"));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.GetProperty("ok").GetBoolean())
                foreach (var window in Application.Current.Windows.OfType<MainWindow>())
                    if (window.rulesRevision == revision) window.SetRulesNotification(false, revision);
        }
        catch { /* Refresh will retry while the rules page is open. */ }
        finally { acknowledgingRules = false; }
    }

    private void RulesTab_Click(object sender, RoutedEventArgs e) => SelectPage(true);
    private void MinutesTab_Click(object sender, RoutedEventArgs e) => SelectPage(false);

    private void SelectPage(bool rules)
    {
        if (showingRules == rules) return;
        showingRules = rules;
        StatusView.Visibility = rules ? Visibility.Collapsed : Visibility.Visible;
        RulesView.Visibility = rules ? Visibility.Visible : Visibility.Collapsed;
        RulesTab.Tag = rules ? "Active" : "Inactive";
        MinutesTab.Tag = rules ? "Inactive" : "Active";
        if (rules)
        {
            SetRulesNotification(false, rulesRevision);
            _ = AcknowledgeRules();
        }
    }

    private void CloseAuthorizedOverlay()
    {
        timer.Stop();
        foreach (var window in Application.Current.Windows.OfType<MainWindow>().ToList())
        {
            window.canClose = true;
            window.Close();
        }
        overlayMutex?.Dispose();
    }

    private async void Redeem_Click(object sender, RoutedEventArgs e)
    {
        if (!primary) return;
        try
        {
            var response = await http.PostAsync("http://localhost:48731/public/redeem",
                new StringContent(PromoInput.Text, Encoding.UTF8, "text/plain"));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            MessageText.Text = document.RootElement.GetProperty("ok").GetBoolean()
                ? "Код принят. Время добавлено." : document.RootElement.GetProperty("error").GetString();
            PromoInput.Clear(); await Refresh();
        }
        catch { MessageText.Text = "Не удалось применить код."; }
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (!primary || !ResumeButton.IsEnabled) return;
        ResumeButton.IsEnabled = false;
        try
        {
            using var response = await http.PostAsync("http://localhost:48731/public/resume", new StringContent(""));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!document.RootElement.GetProperty("ok").GetBoolean())
                MessageText.Text = document.RootElement.GetProperty("error").GetString() ?? "Доступ пока закрыт.";
            else MessageText.Text = "";
            await Refresh();
        }
        catch { MessageText.Text = "Не удалось продолжить работу. Проверьте службу Client."; }
        finally { ResumeButton.IsEnabled = true; }
    }

    private void AdminEntry_Click(object sender, RoutedEventArgs e)
    {
        if (!primary) return;
        AdminMessageText.Text = "";
        AdminPasswordInput.Clear();
        ModalScrim.Visibility = AdminPanel.Visibility = Visibility.Visible;
        PowerPanel.Visibility = Visibility.Collapsed;
        AdminPasswordInput.Focus();
    }

    private void ClosePanels_Click(object sender, RoutedEventArgs e) => ClosePanels();

    private void ClosePanels()
    {
        ModalScrim.Visibility = AdminPanel.Visibility = PowerPanel.Visibility = Visibility.Collapsed;
        AdminPasswordInput.Clear();
        pendingPower = null;
    }

    private void AdminPassword_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AdminUnlock_Click(sender, e);
    }

    private async void AdminUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (!primary || !AdminUnlockButton.IsEnabled) return;
        AdminUnlockButton.IsEnabled = false;
        try
        {
            var response = await http.PostAsync("http://localhost:48731/public/maintenance",
                new StringContent(AdminPasswordInput.Password, Encoding.UTF8, "text/plain"));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var ok = document.RootElement.GetProperty("ok").GetBoolean();
            AdminPasswordInput.Clear();
            if (ok)
            {
                ClosePanels();
                await Refresh();
            }
            else AdminMessageText.Text = document.RootElement.GetProperty("error").GetString() ?? "Доступ не открыт.";
        }
        catch { AdminMessageText.Text = "Не удалось связаться с локальной службой."; }
        finally { AdminUnlockButton.IsEnabled = true; }
    }

    private async void AdminStop_Click(object sender, RoutedEventArgs e)
    {
        if (!primary || !AdminStopButton.IsEnabled) return;
        AdminStopButton.IsEnabled = false;
        AdminUnlockButton.IsEnabled = false;
        try
        {
            var response = await http.PostAsync("http://localhost:48731/public/exit",
                new StringContent(AdminPasswordInput.Password, Encoding.UTF8, "text/plain"));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var ok = document.RootElement.GetProperty("ok").GetBoolean();
            AdminPasswordInput.Clear();
            if (ok)
            {
                ClosePanels();
                CloseAuthorizedOverlay();
            }
            else AdminMessageText.Text = document.RootElement.GetProperty("error").GetString() ?? "Клиент не остановлен.";
        }
        catch { AdminMessageText.Text = "Не удалось связаться с локальной службой."; }
        finally
        {
            AdminStopButton.IsEnabled = true;
            AdminUnlockButton.IsEnabled = true;
        }
    }

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        if (!primary || sender is not System.Windows.Controls.Button button) return;
        pendingPower = button.Tag as string;
        var (title, description) = pendingPower switch
        {
            "restart" => ("Перезагрузить компьютер?", "Открытые программы закроются. После запуска защита продолжит работать."),
            "shutdown" => ("Выключить компьютер?", "Открытые программы закроются. Защита запустится снова при следующем включении."),
            "sleep" => ("Перевести в сон?", "Компьютер уснёт. Во время сна минуты банка не расходуются."),
            _ => ("", "")
        };
        if (title.Length == 0) return;
        PowerTitle.Text = title;
        PowerDescription.Text = description;
        ConfirmPowerButton.Content = pendingPower == "sleep" ? "Перейти в сон" : "Продолжить";
        ModalScrim.Visibility = PowerPanel.Visibility = Visibility.Visible;
        AdminPanel.Visibility = Visibility.Collapsed;
    }

    private void ConfirmPower_Click(object sender, RoutedEventArgs e)
    {
        if (!primary) return;
        var action = pendingPower;
        ClosePanels();
        try
        {
            if (action == "sleep")
            {
                if (!Forms.Application.SetSuspendState(Forms.PowerState.Suspend, false, false))
                    MessageText.Text = "Windows не удалось перевести в спящий режим.";
                return;
            }
            if (action is "restart" or "shutdown")
            {
                var argument = action == "restart" ? "/r" : "/s";
                Process.Start(new ProcessStartInfo("shutdown.exe", argument + " /t 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
        }
        catch (Exception ex) { MessageText.Text = "Действие не выполнено: " + ex.Message; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!canClose) e.Cancel = true;
        base.OnClosing(e);
    }
}
