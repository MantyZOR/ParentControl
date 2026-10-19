using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ParentControl.Core;

namespace ParentControl.Client;

public static class Program
{
    private static ServiceHost? host;
    private static IntPtr statusHandle;
    private static Native.Handler? handler;
    private static Native.ServiceMain? main;
    private static readonly ManualResetEventSlim stopped = new(false);

    private static int Main(string[] args) => Run(args);

    public static int Run(string[] args)
    {
        try
        {
            if (args.Contains("--init"))
            {
                Directory.CreateDirectory(Store.DirectoryPath);
                if (File.Exists(Store.KeyPath)) { Console.WriteLine("Client already initialized. Existing key is not printed again."); return 0; }
                var key = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(Store.KeyPath, LocalSecret.Protect(key, true));
                Console.WriteLine("PAIRING_KEY=" + Convert.ToBase64String(key));
                return 0;
            }
            if (args.Contains("--console"))
            {
                using var manual = new ServiceHost();
                manual.Start();
                Console.WriteLine("Client running. Press Enter to stop.");
                Console.ReadLine();
                return 0;
            }
            main = ServiceEntry;
            var table = new[] { new Native.ServiceTableEntry { Name = "ParentControlClient", Proc = main }, new Native.ServiceTableEntry() };
            if (Native.StartServiceCtrlDispatcher(table)) return 0;
            var error = Marshal.GetLastWin32Error();
            if (error == 1063)
            {
                Console.Error.WriteLine("ParentControl.Client.exe is a Windows service. Install ParentControl.ClientApp.exe on the managed PC.");
                return 2;
            }
            throw new System.ComponentModel.Win32Exception(error);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Error(ex);
            return 1;
        }
    }

    private static void ServiceEntry(int argc, IntPtr argv)
    {
        handler = Control;
        statusHandle = Native.RegisterServiceCtrlHandlerEx("ParentControlClient", handler, IntPtr.Zero);
        SetStatus(2);
        try { host = new ServiceHost(); host.Start(); SetStatus(4); stopped.Wait(); }
        catch (Exception ex) { Error(ex); }
        finally { host?.Dispose(); SetStatus(1); }
    }

    internal static void RequestStop()
    {
        SetStatus(3);
        stopped.Set();
    }

    private static int Control(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control is 1 or 5) { SetStatus(3); stopped.Set(); }
        if (control == 14) host?.SessionChanged(eventType);
        return 0;
    }

    private static void SetStatus(int state)
    {
        if (statusHandle == IntPtr.Zero) return;
        var status = new Native.ServiceStatus
        { ServiceType = 0x10, CurrentState = state, ControlsAccepted = state == 4 ? 0x45 : 0, WaitHint = state is 2 or 3 ? 10000 : 0 };
        Native.SetServiceStatus(statusHandle, ref status);
    }

    internal static void Error(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(Store.DirectoryPath, "service-error.log"), DateTime.UtcNow.ToString("O") + " " + ex + Environment.NewLine); } catch { }
    }
}

internal static class Native
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ServiceMain(int argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate int Handler(int control, int eventType, IntPtr eventData, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct ServiceTableEntry { public string? Name; public ServiceMain? Proc; }
    [StructLayout(LayoutKind.Sequential)] internal struct ServiceStatus { public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool StartServiceCtrlDispatcher(ServiceTableEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr RegisterServiceCtrlHandlerEx(string name, Handler handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
    [DllImport("kernel32.dll")] internal static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] internal static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
    internal static bool? ActiveSessionUnlocked()
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == uint.MaxValue || !WTSQuerySessionInformation(IntPtr.Zero, session, 25, out var buffer, out var bytes)) return null;
        try
        {
            // WTSINFOEXW: Level (4), 4-byte alignment, Level1 SessionId (4), State (4), SessionFlags (4).
            if (bytes < 20 || Marshal.ReadInt32(buffer) != 1) return null;
            return Marshal.ReadInt32(buffer, 16) switch { 0 => false, 1 => true, _ => null };
        }
        finally { WTSFreeMemory(buffer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
    {
        public int Size; public string? Reserved; public string? Desktop; public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2; public IntPtr Reserved2Ptr, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcessAsUser(IntPtr token, string application, string? commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, int flags, IntPtr environment, string currentDirectory,
        ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
}
