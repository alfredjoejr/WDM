using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace WDM.UI;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "Global\\WDM_SingleInstance_Mutex_AlfredJoe";
        _singleInstanceMutex = new Mutex(true, mutexName, out bool isNewInstance);

        if (!isNewInstance)
        {
            // Another instance is already running. Bring it to the front and exit cleanly.
            BringExistingInstanceToFront();
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    private static void BringExistingInstanceToFront()
    {
        try
        {
            var currentProc = Process.GetCurrentProcess();
            var running = Process.GetProcessesByName(currentProc.ProcessName);
            foreach (var proc in running)
            {
                if (proc.Id != currentProc.Id && proc.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(proc.MainWindowHandle);
                    break;
                }
            }
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}

