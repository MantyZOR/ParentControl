using MessageBox = System.Windows.MessageBox;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using Application = System.Windows.Application;

namespace ParentControl.ClientApp;

public partial class App : Application
{
    private ClientTray? tray;
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParentControl");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "client-ui-error.log"), DateTime.UtcNow.ToString("O") + " " + e.Exception + Environment.NewLine);
            }
            catch { }
            MessageBox.Show("Ошибка интерфейса клиента. Подробности в %LocalAppData%\\ParentControl\\client-ui-error.log.",
                "ParentControl Client", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            Shutdown(1);
        };
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-check"))
        {
            try
            {
                _ = new ParentControl.Overlay.MainWindow();
                _ = new MainWindow();
                Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--service"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(ParentControl.Client.Program.Run(e.Args));
            return;
        }
        if (e.Args.Contains("--tray"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            tray = ClientTray.TryStart();
            if (tray is null) Shutdown();
            return;
        }
        if (e.Args.Contains("--overlay"))
        {
            MainWindow = new ParentControl.Overlay.MainWindow();
            MainWindow.Show();
            return;
        }
        if (!IsAdministrator())
        {
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                foreach (var arg in e.Args) start.ArgumentList.Add(arg);
                _ = Process.Start(start);
            }
            catch (Win32Exception ex)
            {
                MessageBox.Show("Для установки клиента нужны права администратора Windows.\n\n" + ex.Message,
                    "ParentControl Client", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0].Equals("--uninstall-worker", StringComparison.OrdinalIgnoreCase))
        {
            try { SetupOperations.FinishUninstall(e.Args[1]); Shutdown(); }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось удалить Client:\n\n" + ex.Message, "ParentControl Client", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                SetupOperations.Uninstall();
                MessageBox.Show("Удаление Client запущено. Данные банка и ключ будут сохранены.",
                    "ParentControl Client", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось начать удаление Client:\n\n" + ex.Message, "ParentControl Client", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }
        try
        {
            if (!SetupOperations.IsInstalled())
            {
                SetupOperations.Install();
                var source = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден EXE Client.");
                if (!Path.GetFullPath(source).Equals(Path.GetFullPath(SetupOperations.TargetExe), StringComparison.OrdinalIgnoreCase))
                {
                    _ = Process.Start(new ProcessStartInfo(SetupOperations.TargetExe) { UseShellExecute = false })
                        ?? throw new InvalidOperationException("Не удалось открыть установленный Client.");
                    Shutdown();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось установить Client:\n\n" + ex.Message,
                "ParentControl Client", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        base.OnExit(e);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
