using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ParentControl.Admin;

internal static class AdminInstaller
{
    private const string AppName = "ParentControl Admin";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ParentControlAdmin";

    public static string InstalledExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "ParentControlAdmin", "ParentControl.Admin.exe");

    private static string Shortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    private static string DesktopShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    public static bool AutostartEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("ParentControlAdmin") != null; }
    }

    public static void SetAutostart(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            key.SetValue("ParentControlAdmin", $"\"{InstalledExe}\"", RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue("ParentControlAdmin", false);
        }
    }

    public static bool InstallAndRelaunchIfNeeded()
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь EXE Admin.");
        if (SamePath(source, InstalledExe))
        {
            RegisterApp();
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(InstalledExe)!);
        File.Copy(source, InstalledExe, true);
        RegisterApp();
        _ = Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(InstalledExe)! })
            ?? throw new InvalidOperationException("Не удалось открыть установленный Admin.");
        return true;
    }

    private static void RegisterApp()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Не найдена служба ярлыков Windows.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            CreateShortcut(shell, Shortcut);
            CreateShortcut(shell, DesktopShortcut);
        }
        finally { Marshal.FinalReleaseComObject(shell); }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, true);
        key.SetValue("DisplayName", AppName);
        key.SetValue("Publisher", "ParentControl");
        key.SetValue("DisplayVersion", "1.0.0");
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", Path.GetDirectoryName(InstalledExe)!);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void CreateShortcut(dynamic shell, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        dynamic shortcut = shell.CreateShortcut(path);
        try
        {
            shortcut.TargetPath = InstalledExe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(InstalledExe)!;
            shortcut.IconLocation = InstalledExe + ",0";
            shortcut.Description = "Управление временем компьютеров в локальной сети";
            shortcut.Save();
        }
        finally { Marshal.FinalReleaseComObject(shortcut); }
    }

    public static void Uninstall()
    {
        if (!SamePath(Environment.ProcessPath ?? "", InstalledExe))
            throw new InvalidOperationException("Удаление можно запустить только из установленной копии.");
        var helper = Path.Combine(Path.GetTempPath(), "ParentControlAdminUninstall-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(InstalledExe, helper);
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--uninstall-worker");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        _ = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить удаление.");
    }

    public static void FinishUninstall(string parentProcessId)
    {
        if (!int.TryParse(parentProcessId, out var pid)) throw new InvalidOperationException("Неверный идентификатор удаления.");
        try
        {
            using var parent = Process.GetProcessById(pid);
            parent.WaitForExit(120_000);
            if (!parent.HasExited) throw new TimeoutException("Admin не закрылся за две минуты.");
        }
        catch (ArgumentException) { }
        SetAutostart(false);
        if (File.Exists(Shortcut)) File.Delete(Shortcut);
        if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut);
        Registry.CurrentUser.DeleteSubKey(UninstallKey, false);
        if (File.Exists(InstalledExe)) File.Delete(InstalledExe);
        var directory = Path.GetDirectoryName(InstalledExe)!;
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private static bool SamePath(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

}
