using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace WDM.NativeHost;

/// <summary>
/// Registers and unregisters the WDM Native Messaging Host in the Windows Registry
/// for Google Chrome, Microsoft Edge, Brave, and Mozilla Firefox.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class HostInstaller
{
    public const string HostName = "com.wdm.downloader";

    private static readonly (string Browser, string SubKey)[] RegistryTargets =
    [
        ("Google Chrome", @"Software\Google\Chrome\NativeMessagingHosts"),
        ("Microsoft Edge", @"Software\Microsoft\Edge\NativeMessagingHosts"),
        ("Mozilla Firefox", @"Software\Mozilla\NativeMessagingHosts"),
        ("Mozilla Firefox (Wow6432)", @"Software\Wow6432Node\Mozilla\NativeMessagingHosts")
    ];

    public static string GetManifestPath()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM");
        Directory.CreateDirectory(appData);
        return Path.Combine(appData, $"{HostName}.json");
    }

    public static bool Install(string? customExePath = null)
    {
        var sourceExePath = customExePath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(sourceExePath) || !File.Exists(sourceExePath))
        {
            Console.Error.WriteLine("Error: Could not locate native host executable path.");
            return false;
        }

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM");
        var targetHostDir = Path.Combine(appData, "NativeHost");
        Directory.CreateDirectory(targetHostDir);

        // Copy binary and its dependencies to LocalAppData (outside OneDrive)
        var sourceDir = Path.GetDirectoryName(sourceExePath)!;
        var targetExePath = Path.Combine(targetHostDir, Path.GetFileName(sourceExePath));

        try
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*.*"))
            {
                var dest = Path.Combine(targetHostDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Notice] Could not mirror all files to {targetHostDir}: {ex.Message}");
            targetExePath = sourceExePath; // fallback
        }

        var manifestPath = GetManifestPath();

        // 1. Generate Manifest JSON
        var manifest = new
        {
            name = HostName,
            description = "WDM High-Speed Download Manager Native Messaging Host",
            path = targetExePath,
            type = "stdio",
            allowed_origins = new[]
            {
                "chrome-extension://*", // Accepts local unpacked development extensions or any ID
            },
            allowed_extensions = new[]
            {
                "wdm-downloader@local",
                "2a3e3cb2-c5c7-46b3-bc78-fae08fc0f578",
                "{2a3e3cb2-c5c7-46b3-bc78-fae08fc0f578}",
                "2a3e3cb2-c5c7-46b3-bc78-fae08fc0f578@temporary-addon"
            }
        };

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, json);

        // 2. Register in Windows Registry (HKCU)
        bool allSucceeded = true;
        foreach (var (browser, subKey) in RegistryTargets)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"{subKey}\{HostName}");
                key.SetValue(string.Empty, manifestPath, RegistryValueKind.String);
                Console.WriteLine($"[✓] Registered for {browser}: HKCU\\{subKey}\\{HostName}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[✗] Failed to register for {browser}: {ex.Message}");
                allSucceeded = false;
            }
        }

        Console.WriteLine($"[✓] Host Manifest generated at: {manifestPath}");
        Console.WriteLine($"[✓] Target binary deployed at: {targetExePath}");
        return allSucceeded;
    }

    public static bool Uninstall()
    {
        bool allSucceeded = true;
        foreach (var (browser, subKey) in RegistryTargets)
        {
            try
            {
                using var parentKey = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
                if (parentKey != null)
                {
                    parentKey.DeleteSubKeyTree(HostName, throwOnMissingSubKey: false);
                    Console.WriteLine($"[✓] Unregistered from {browser}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[✗] Failed to unregister from {browser}: {ex.Message}");
                allSucceeded = false;
            }
        }

        var manifestPath = GetManifestPath();
        if (File.Exists(manifestPath))
        {
            try
            {
                File.Delete(manifestPath);
                Console.WriteLine("[✓] Removed manifest file.");
            }
            catch { }
        }

        return allSucceeded;
    }
}
