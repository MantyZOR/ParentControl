using MessageBox = System.Windows.MessageBox;
using System.Configuration;
using System.Data;
using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using Application = System.Windows.Application;

namespace ParentControl.Admin;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParentControl");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "admin-error.log"), DateTime.UtcNow.ToString("O") + " " + e.Exception + Environment.NewLine);
            }
            catch { }
            MessageBox.Show("Ошибка Admin-приложения. Подробности записаны в %LocalAppData%\\ParentControl\\admin-error.log.",
                "ParentControl Admin", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            Shutdown(1);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (e.Args.Length == 2 && e.Args[0].Equals("--uninstall-worker", StringComparison.OrdinalIgnoreCase))
            {
                AdminInstaller.FinishUninstall(e.Args[1]);
                Shutdown();
                return;
            }
            if (!IsAdministrator())
            {
                RelaunchElevated(e.Args);
                Shutdown();
                return;
            }
            if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            {
                AdminInstaller.Uninstall();
                MessageBox.Show("Удаление ParentControl Admin запущено. Настройки и журнал будут сохранены.",
                    "Удаление ParentControl Admin", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            if (AdminInstaller.InstallAndRelaunchIfNeeded())
            {
                Shutdown();
                return;
            }
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось установить или запустить Admin:\n\n" + ex.Message,
                "ParentControl Admin", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchElevated(string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Не найден EXE Admin."))
        { UseShellExecute = true, Verb = "runas" };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try { _ = Process.Start(start) ?? throw new InvalidOperationException("Windows не запустила Admin с правами администратора."); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show("Запуск отменён: для Admin требуются права администратора Windows.",
                "ParentControl Admin", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
