using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ParentControl.Core;

namespace ParentControl.ClientApp;

internal static class SetupOperations
{
    private const string ServiceName = "ParentControlClient";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ParentControlClient";
    private const string AppName = "ParentControl Client";
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ParentControl");
    public static string TargetExe => Path.Combine(InstallDirectory, "ParentControl.ClientApp.exe");
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ParentControl");
    public static string KeyFile => Path.Combine(DataDirectory, "key.dat");
    public static string StateFile => Path.Combine(DataDirectory, "state.dat");
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk");
    private static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk");

    public static string? PairingKey => File.Exists(KeyFile) ? Convert.ToBase64String(LocalSecret.Unprotect(File.ReadAllBytes(KeyFile))) : null;
    private static ClientState? SavedState => File.Exists(StateFile)
        ? System.Text.Json.JsonSerializer.Deserialize<ClientState>(LocalSecret.Unprotect(File.ReadAllBytes(StateFile)), Wire.Json) : null;
    public static long? BalanceSeconds => SavedState?.BalanceSeconds;
    public static bool? ProtectionArmed => SavedState?.ProtectionArmed != false;
    public static bool UserPaused => SavedState?.UserPaused == true;

    public static string IpAddresses()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString()).Distinct().ToArray();
        return addresses.Length == 0 ? "IP не найден" : string.Join("  ·  ", addresses);
    }

    public static bool IsInstalled() => Status() != null;
    public static bool IsRunning() => Status() == 4;

    private static int? Status()
    {
        var manager = OpenSCManager(null, null, 0x0001);
        if (manager == IntPtr.Zero) return null;
        try
        {
            var service = OpenService(manager, ServiceName, 0x0004);
            if (service == IntPtr.Zero) return null;
            try { return QueryServiceStatus(service, out var status) ? status.CurrentState : null; }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public static void Install()
    {
        var wasInstalled = IsInstalled();
        var resumeService = IsRunning();
        if (resumeService)
        {
            RunTool("sc.exe", false, "stop", ServiceName);
            for (var i = 0; i < 60 && Status() != 1; i++) Thread.Sleep(500);
            if (Status() != 1) throw new InvalidOperationException("Не удалось остановить прежнюю службу для обновления.");
        }
        Directory.CreateDirectory(InstallDirectory);
        Directory.CreateDirectory(DataDirectory);
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь EXE.");
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(TargetExe), StringComparison.OrdinalIgnoreCase))
        {
            for (var attempt = 0; ; attempt++)
            {
                try { File.Copy(source, TargetExe, true); break; }
                catch (IOException) when (attempt < 30) { Thread.Sleep(500); }
            }
        }
        RunTool("icacls.exe", true, DataDirectory, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F");
        if (!File.Exists(KeyFile)) File.WriteAllBytes(KeyFile, LocalSecret.Protect(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), true));
        var binary = $"\"{TargetExe}\" --service";
        if (wasInstalled) RunTool("sc.exe", true, "config", ServiceName, "binPath=", binary, "start=", "auto");
        else RunTool("sc.exe", true, "create", ServiceName, "binPath=", binary, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "ParentControl Client");
        RunTool("sc.exe", true, "failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/5000");
        RunTool("netsh.exe", false, "advfirewall", "firewall", "delete", "rule", "name=ParentControl LAN");
        RunTool("netsh.exe", true, "advfirewall", "firewall", "add", "rule", "name=ParentControl LAN", "dir=in", "action=allow", "protocol=TCP", "localport=48731", "remoteip=localsubnet", "profile=private");
        RunTool("netsh.exe", false, "advfirewall", "firewall", "delete", "rule", "name=ParentControl Discovery");
        RunTool("netsh.exe", true, "advfirewall", "firewall", "add", "rule", "name=ParentControl Discovery", "dir=in", "action=allow", "protocol=UDP", "localport=48732", "remoteip=localsubnet", "profile=private");
        RegisterApp();
        if (resumeService || !wasInstalled) Start();
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

        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey, true);
        key.SetValue("DisplayName", AppName);
        key.SetValue("Publisher", "ParentControl");
        key.SetValue("DisplayVersion", "1.0.0");
        key.SetValue("DisplayIcon", TargetExe);
        key.SetValue("InstallLocation", InstallDirectory);
        key.SetValue("UninstallString", $"\"{TargetExe}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void CreateShortcut(dynamic shell, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        dynamic shortcut = shell.CreateShortcut(path);
        try
        {
            shortcut.TargetPath = TargetExe;
            shortcut.WorkingDirectory = InstallDirectory;
            shortcut.IconLocation = TargetExe + ",0";
            shortcut.Description = "Настройка клиента ParentControl";
            shortcut.Save();
        }
        finally { Marshal.FinalReleaseComObject(shortcut); }
    }

    public static void Uninstall()
    {
        if (!Path.GetFullPath(Environment.ProcessPath ?? "").Equals(Path.GetFullPath(TargetExe), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Удаление можно запустить только из установленной копии Client.");
        var helper = Path.Combine(Path.GetTempPath(), "ParentControlClientUninstall-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(TargetExe, helper);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--uninstall-worker");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        _ = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить удаление Client.");
    }

    public static void FinishUninstall(string parentProcessId)
    {
        if (!int.TryParse(parentProcessId, out var pid)) throw new InvalidOperationException("Неверный идентификатор удаления.");
        try
        {
            using var parent = Process.GetProcessById(pid);
            parent.WaitForExit(120_000);
            if (!parent.HasExited) throw new TimeoutException("Окно Client не закрылось за две минуты.");
        }
        catch (ArgumentException) { }

        if (IsRunning())
        {
            RunTool("sc.exe", false, "stop", ServiceName);
            for (var i = 0; i < 30 && IsRunning(); i++) Thread.Sleep(500);
            if (IsRunning()) throw new InvalidOperationException("Не удалось остановить службу Client.");
        }
        if (IsInstalled()) RunTool("sc.exe", true, "delete", ServiceName);
        RunTool("netsh.exe", false, "advfirewall", "firewall", "delete", "rule", "name=ParentControl LAN");
        RunTool("netsh.exe", false, "advfirewall", "firewall", "delete", "rule", "name=ParentControl Discovery");

        foreach (var process in Process.GetProcessesByName("ParentControl.ClientApp"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (process.MainModule?.FileName is not { } processPath
                        || !Path.GetFullPath(processPath).Equals(Path.GetFullPath(TargetExe), StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
            }
        }
        if (File.Exists(TargetExe)) File.Delete(TargetExe);
        if (File.Exists(Shortcut)) File.Delete(Shortcut);
        if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut);
        Registry.LocalMachine.DeleteSubKey(UninstallKey, false);
        if (Directory.Exists(InstallDirectory) && !Directory.EnumerateFileSystemEntries(InstallDirectory).Any()) Directory.Delete(InstallDirectory);
    }

    public static void Start()
    {
        if (!IsInstalled()) throw new InvalidOperationException("Сначала установите клиент.");
        if (IsRunning()) return;
        RunTool("sc.exe", true, "start", ServiceName);
        for (var i = 0; i < 20 && !IsRunning(); i++) Thread.Sleep(500);
        if (!IsRunning()) throw new InvalidOperationException("Служба не запустилась. Проверьте журнал в %ProgramData%\\ParentControl\\service-error.log.");
    }

    public static async Task Arm()
    {
        if (!IsRunning()) throw new InvalidOperationException("Сначала запустите службу Client.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await http.PostAsync("http://localhost:48731/public/arm", new StringContent(""));
        response.EnsureSuccessStatusCode();
        var result = System.Text.Json.JsonSerializer.Deserialize<CommandResult>(await response.Content.ReadAsStringAsync(), Wire.Json);
        if (result?.Ok != true) throw new InvalidOperationException(result?.Error ?? "Служба не подтвердила включение защиты.");
    }

    public static async Task Pause()
    {
        if (!IsRunning()) throw new InvalidOperationException("Служба Client не запущена.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await http.PostAsync("http://localhost:48731/public/pause", new StringContent(""));
        response.EnsureSuccessStatusCode();
        var result = System.Text.Json.JsonSerializer.Deserialize<CommandResult>(await response.Content.ReadAsStringAsync(), Wire.Json);
        if (result?.Ok != true) throw new InvalidOperationException(result?.Error ?? "Не удалось сохранить минуты.");
    }

    public static async Task EndMaintenance()
    {
        if (!IsRunning()) throw new InvalidOperationException("Служба Client не запущена.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await http.PostAsync("http://localhost:48731/public/end-maintenance", new StringContent(""));
        response.EnsureSuccessStatusCode();
    }

    private static void RunTool(string executable, bool required, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), executable))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(executable + " could not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30000)) { process.Kill(); throw new TimeoutException(executable + " timed out"); }
        if (required && process.ExitCode != 0) throw new InvalidOperationException(executable + ": " + stdout + " " + stderr);
    }

    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, int desiredAccess);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, int desiredAccess);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}
