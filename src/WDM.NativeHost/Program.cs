namespace WDM.NativeHost;

public static class Program
{
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
        var service = new HostService();
        using var cts = new CancellationTokenSource();

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var message = await HostProtocol.ReadMessageAsync(cts.Token);
                if (!message.HasValue)
                {
                    // Stdio pipe was closed by the browser
                    break;
                }

                await service.HandleMessageAsync(message.Value, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Browser exited
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Fatal NativeHost error: {ex.Message}");
            return 1;
        }

        return 0;
    }
}
