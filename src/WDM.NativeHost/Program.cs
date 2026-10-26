namespace WDM.NativeHost;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class Program
{
    private static readonly string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM", "native_host.log");

    public static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFile);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public static async Task<int> Main(string[] args)
    {
        // 1. Command-line installation / management mode
        if (args.Length > 0)
        {
            var command = args[0].ToLowerInvariant();
            switch (command)
            {
                case "--install" or "-i" or "install":
                    Console.WriteLine("Installing WDM Native Messaging Host into Windows Registry...");
                    var installed = HostInstaller.Install();
                    return installed ? 0 : 1;

                case "--uninstall" or "-u" or "uninstall":
                    Console.WriteLine("Uninstalling WDM Native Messaging Host from Windows Registry...");
                    var uninstalled = HostInstaller.Uninstall();
                    return uninstalled ? 0 : 1;

                case "--help" or "-h":
                    Console.WriteLine("WDM Native Messaging Host for Chrome, Edge, and Firefox");
                    Console.WriteLine("Usage:");
                    Console.WriteLine("  WDM.NativeHost --install     Register host in Windows Registry");
                    Console.WriteLine("  WDM.NativeHost --uninstall   Unregister host from Windows Registry");
                    return 0;
            }
        }

        // 2. Browser Native Messaging Host mode (STDIO pipe communication)
        Log("NativeHost started by browser.");
        var service = new HostService();
        using var cts = new CancellationTokenSource();

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var message = await HostProtocol.ReadMessageAsync(cts.Token);
                if (!message.HasValue)
                {
                    Log("Stdio pipe closed by browser.");
                    break;
                }

                var msgPayload = message.Value;
                Log($"Received message: {msgPayload.GetRawText()}");
                _ = Task.Run(() => service.HandleMessageAsync(msgPayload, cts.Token), cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Log("NativeHost canceled / browser exited.");
        }
        catch (Exception ex)
        {
            Log($"Fatal NativeHost error: {ex}");
            await Console.Error.WriteLineAsync($"Fatal NativeHost error: {ex.Message}");
            return 1;
        }

        Log("NativeHost exiting normally.");
        return 0;
    }
}
