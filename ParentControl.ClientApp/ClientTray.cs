using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using ParentControl.Core;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;

namespace ParentControl.ClientApp;

internal sealed class ClientTray : IDisposable
{
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly Mutex mutex;
    private readonly Icon? appIcon;
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem balance = new() { Enabled = false };
    private readonly Forms.ToolStripMenuItem pause = new("Сохранить минуты и заблокировать") { Enabled = false };
    private readonly Forms.ToolStripMenuItem endMaintenance = new("Завершить техобслуживание сейчас") { Enabled = false };
    private long lastWarningSequence;
    private WarningWindow? warningWindow;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool polling;
    private bool serviceSeen;

    private ClientTray(Mutex mutex)
    {
        this.mutex = mutex;
        appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(balance);
        menu.Items.Add(pause);
        pause.Click += async (_, _) => await Pause_Click();
        menu.Items.Add(endMaintenance);
        endMaintenance.Click += async (_, _) => await EndMaintenance_Click();
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Открыть настройки", null, (_, _) => OpenSetup());
        menu.Items.Add("Выйти из клиента…", null, (_, _) => new ExitDialog().ShowDialog());
        icon = new Forms.NotifyIcon
        {
            Icon = appIcon ?? SystemIcons.Application,
            Text = "ParentControl Client",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => OpenSetup();
        balance.Text = "Проверка остатка…";
        timer.Tick += async (_, _) => await Refresh();
        timer.Start();
        _ = Refresh();
    }

    public static ClientTray? TryStart()
    {
        var name = $"Global\\ParentControlClientTray_{Process.GetCurrentProcess().SessionId}";
        var mutex = new Mutex(true, name, out var created);
        if (created) return new ClientTray(mutex);
        mutex.Dispose();
        return null;
    }

    private async Task Refresh()
    {
        if (polling) return;
        polling = true;
        try
        {
            var json = await http.GetStringAsync("http://localhost:48731/public/status");
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("exiting", out var exiting) && exiting.GetBoolean())
            {
                Application.Current.Shutdown();
                return;
            }
            serviceSeen = true;
            var minutes = (document.RootElement.GetProperty("seconds").GetInt64() + 59) / 60;
            balance.Text = $"Осталось: {minutes} мин";
            var paused = document.RootElement.TryGetProperty("paused", out var pausedProperty) && pausedProperty.GetBoolean();
            icon.Text = paused ? $"ParentControl Client · пауза · {minutes} мин"
                : $"ParentControl Client · осталось {minutes} мин";
            var armed = document.RootElement.TryGetProperty("armed", out var armedProperty) && armedProperty.GetBoolean();
            var allowed = document.RootElement.GetProperty("allowed").GetBoolean();
            var maintenanceSeconds = document.RootElement.TryGetProperty("maintenanceSeconds", out var maintenanceProperty)
                ? maintenanceProperty.GetInt32() : 0;
            pause.Enabled = armed && allowed && !paused && minutes > 0 && maintenanceSeconds == 0;
            endMaintenance.Enabled = maintenanceSeconds > 0;
            if (document.RootElement.TryGetProperty("warningSequence", out var warningProperty) &&
                warningProperty.GetInt64() is var sequence && sequence > lastWarningSequence &&
                document.RootElement.TryGetProperty("warningActive", out var activeProperty) && activeProperty.GetBoolean())
            {
                lastWarningSequence = sequence;
                warningWindow?.Close();
                var reminder = new WarningWindow(minutes);
                reminder.Closed += (_, _) => { if (ReferenceEquals(warningWindow, reminder)) warningWindow = null; };
                warningWindow = reminder;
                reminder.Show();
            }
        }
        catch
        {
            if (serviceSeen) Application.Current.Shutdown();
            else
            {
                balance.Text = "Служба пока недоступна";
                icon.Text = "ParentControl Client · служба недоступна";
            }
        }
        finally { polling = false; }
    }

    private async Task Pause_Click()
    {
        pause.Enabled = false;
        try { await SetupOperations.Pause(); await Refresh(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "ParentControl Client"); }
    }

    private async Task EndMaintenance_Click()
    {
        endMaintenance.Enabled = false;
        try { await SetupOperations.EndMaintenance(); await Refresh(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "ParentControl Client"); }
    }

    internal static async Task<CommandResult> RequestExit(string password)
    {
        using var response = await http.PostAsync("http://localhost:48731/public/exit",
            new StringContent(password, Encoding.UTF8, "text/plain"));
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<CommandResult>(await response.Content.ReadAsStringAsync(), Wire.Json)
            ?? throw new InvalidDataException("Пустой ответ службы.");
    }

    private static void OpenSetup()
    {
        try
        {
            Process.Start(new ProcessStartInfo(SetupOperations.TargetExe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "ParentControl Client");
        }
    }

    public void Dispose()
    {
        timer.Stop();
        warningWindow?.Close();
        icon.Visible = false;
        icon.ContextMenuStrip?.Dispose();
        icon.Dispose();
        appIcon?.Dispose();
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
