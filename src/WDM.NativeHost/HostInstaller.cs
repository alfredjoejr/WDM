using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private static readonly (string Browser, string SubKey, bool IsFirefox)[] RegistryTargets =
    [
        ("Google Chrome", @"Software\Google\Chrome\NativeMessagingHosts", false),
        ("Microsoft Edge", @"Software\Microsoft\Edge\NativeMessagingHosts", false),
        ("Brave Browser", @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts", false),
        ("Mozilla Firefox", @"Software\Mozilla\NativeMessagingHosts", true),
        ("Mozilla Firefox (Wow6432)", @"Software\Wow6432Node\Mozilla\NativeMessagingHosts", true)
    ];

    public static string GetAppDataDirectory()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM");
        Directory.CreateDirectory(appData);
        return appData;
    }

    public static string GetChromiumManifestPath() => Path.Combine(GetAppDataDirectory(), $"{HostName}.json");

    public static string GetFirefoxManifestPath() => Path.Combine(GetAppDataDirectory(), $"{HostName}.firefox.json");

    public static string GetConfigPath() => Path.Combine(GetAppDataDirectory(), "host_config.json");

    public static bool Install(string? customExePath = null)
    {
        var sourceExePath = customExePath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(sourceExePath) || !File.Exists(sourceExePath))
        {
            Console.Error.WriteLine("Error: Could not locate native host executable path.");
            return false;
        }

        var appData = GetAppDataDirectory();
        var targetHostDir = Path.Combine(appData, "NativeHost");
        Directory.CreateDirectory(targetHostDir);

        // 1. Copy NativeHost binary and dependencies to LocalAppData (outside OneDrive)
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourceExePath))!;
        var targetExePath = Path.Combine(targetHostDir, Path.GetFileName(sourceExePath));

        if (!string.Equals(sourceDir.TrimEnd('\\'), targetHostDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                CopyDirectoryRecursive(sourceDir, targetHostDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Notice] Could not mirror all NativeHost files to {targetHostDir}: {ex.Message}");
                if (!File.Exists(targetExePath))
                {
                    targetExePath = sourceExePath;
                }
            }
        }

        // 2. Locate and deploy WDM.UI to LocalAppData\WDM\UI so "Open WDM Dashboard" always works
        var sourceUiExe = FindSourceUiExecutable(sourceDir);
        var targetUiDir = Path.Combine(appData, "UI");
        var targetUiExe = Path.Combine(targetUiDir, "WDM.UI.exe");

        if (!string.IsNullOrWhiteSpace(sourceUiExe) && File.Exists(sourceUiExe))
        {
            var sourceUiDir = Path.GetDirectoryName(sourceUiExe)!;
            if (!string.Equals(sourceUiDir.TrimEnd('\\'), targetUiDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.CreateDirectory(targetUiDir);
                    CopyDirectoryRecursive(sourceUiDir, targetUiDir);
                    Console.WriteLine($"[✓] UI Dashboard deployed to: {targetUiExe}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Notice] Could not mirror WDM.UI to {targetUiDir}: {ex.Message}");
                }
            }
        }

        // 3. Locate extension folder, deploy permanently to LocalAppData\WDM\Extension, and package .xpi / .zip
        var extensionDir = FindExtensionDirectory(sourceDir);
        var targetExtensionDir = Path.Combine(appData, "Extension");
        string? targetXpiPath = null;
        string? targetAmoZipPath = null;

        if (!string.IsNullOrWhiteSpace(extensionDir) && Directory.Exists(extensionDir))
        {
            try
            {
                Directory.CreateDirectory(targetExtensionDir);
                CopyDirectoryRecursive(extensionDir, targetExtensionDir);
                Console.WriteLine($"[✓] Extension files deployed permanently to: {targetExtensionDir}");

                targetXpiPath = Path.Combine(targetExtensionDir, "wdm-downloader@local.xpi");
                targetAmoZipPath = Path.Combine(targetExtensionDir, "wdm-downloader-amo.zip");

                PackageExtension(targetExtensionDir, targetXpiPath);
                PackageExtension(targetExtensionDir, targetAmoZipPath);
                Console.WriteLine($"[✓] Firefox XPI package created: {targetXpiPath}");
                Console.WriteLine($"[✓] AMO distribution package created: {targetAmoZipPath}");

                // Prefer a Mozilla-signed XPI if the user saved one (kept outside Extension\ so re-installs never overwrite it)
                var signedXpiPath = Path.Combine(appData, "Signed", "wdm-downloader-signed.xpi");
                if (File.Exists(signedXpiPath))
                {
                    Console.WriteLine($"[✓] Using Mozilla-signed XPI for Firefox: {signedXpiPath}");
                    TryInstallFirefoxDistribution(signedXpiPath);
                }
                else
                {
                    TryInstallFirefoxDistribution(targetXpiPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Notice] Extension packaging notice: {ex.Message}");
            }
        }

        // 4. Locate and deploy icon.ico
        var sourceIcon = FindSourceIcon(sourceDir);
        var targetIcon = Path.Combine(appData, "icon.ico");
        if (!string.IsNullOrWhiteSpace(sourceIcon) && File.Exists(sourceIcon))
        {
            try
            {
                File.Copy(sourceIcon, targetIcon, overwrite: true);
            }
            catch { }
        }

        // 5. Create Desktop and Start Menu Shortcuts
        if (File.Exists(targetUiExe))
        {
            CreateShortcuts(targetUiExe, File.Exists(targetIcon) ? targetIcon : targetUiExe);
        }

        // 6. Register in Windows Programs & Features (Uninstall support)
        RegisterInWindowsProgramsAndFeatures(appData, targetExePath, targetUiExe, targetIcon);

        var config = new
        {
            installedUiExePath = File.Exists(targetUiExe) ? targetUiExe : null,
            sourceUiExePath = sourceUiExe,
            extensionDir = Directory.Exists(targetExtensionDir) ? targetExtensionDir : extensionDir,
            sourceExtensionDir = extensionDir,
            xpiPath = targetXpiPath,
            updatedAt = DateTime.UtcNow.ToString("O")
        };
        File.WriteAllText(GetConfigPath(), JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

        // 7. Discover all valid Chromium extension origins (exact 32-char IDs; Chromium forbids wildcards)
        var allowedOrigins = DiscoverChromiumAllowedOrigins(Directory.Exists(targetExtensionDir) ? targetExtensionDir : extensionDir);
        var allowedFirefoxExtensions = DiscoverFirefoxAllowedExtensions();
        var chromiumManifestPath = GetChromiumManifestPath();
        var firefoxManifestPath = GetFirefoxManifestPath();
        var targetHostChromiumManifest = Path.Combine(targetHostDir, $"{HostName}.json");
        var targetHostFirefoxManifest = Path.Combine(targetHostDir, $"{HostName}.firefox.json");

        // Update repo extension/com.wdm.downloader.json if present for reference
        var chromiumManifest = new
        {
            name = HostName,
            description = "WDM High-Speed Download Manager Native Messaging Host",
            path = targetExePath,
            type = "stdio",
            allowed_origins = allowedOrigins
        };

        var firefoxManifest = new
        {
            name = HostName,
            description = "WDM High-Speed Download Manager Native Messaging Host",
            path = targetExePath,
            type = "stdio",
            allowed_extensions = allowedFirefoxExtensions
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var chromiumJson = JsonSerializer.Serialize(chromiumManifest, jsonOptions);
        var firefoxJson = JsonSerializer.Serialize(firefoxManifest, jsonOptions);

        // Write manifests to both LocalAppData\WDM and LocalAppData\WDM\NativeHost
        File.WriteAllText(chromiumManifestPath, chromiumJson);
        File.WriteAllText(firefoxManifestPath, firefoxJson);
        File.WriteAllText(targetHostChromiumManifest, chromiumJson);
        File.WriteAllText(targetHostFirefoxManifest, firefoxJson);

        if (!string.IsNullOrWhiteSpace(extensionDir) && Directory.Exists(extensionDir))
        {
            try
            {
                File.WriteAllText(Path.Combine(extensionDir, $"{HostName}.json"), chromiumJson);
                File.WriteAllText(Path.Combine(extensionDir, $"{HostName}.firefox.json"), firefoxJson);
            }
            catch { }
        }

        if (Directory.Exists(targetExtensionDir))
        {
            try
            {
                File.WriteAllText(Path.Combine(targetExtensionDir, $"{HostName}.json"), chromiumJson);
                File.WriteAllText(Path.Combine(targetExtensionDir, $"{HostName}.firefox.json"), firefoxJson);
            }
            catch { }
        }

        // 8. Register in Windows Registry (HKCU)
        bool allSucceeded = true;
        foreach (var (browser, subKey, isFirefox) in RegistryTargets)
        {
            try
            {
                // Register the manifest located directly alongside the executable for optimal working directory and process spawning
                var manifestToRegister = isFirefox ? targetHostFirefoxManifest : targetHostChromiumManifest;
                using var key = Registry.CurrentUser.CreateSubKey($@"{subKey}\{HostName}");
                key.SetValue(string.Empty, manifestToRegister, RegistryValueKind.String);
                Console.WriteLine($"[✓] Registered for {browser}: HKCU\\{subKey}\\{HostName} -> {manifestToRegister}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[✗] Failed to register for {browser}: {ex.Message}");
                allSucceeded = false;
            }
        }

        Console.WriteLine($"[✓] Allowed Chromium Origins ({allowedOrigins.Length}): {string.Join(", ", allowedOrigins)}");
        Console.WriteLine($"[✓] Allowed Firefox Extensions ({allowedFirefoxExtensions.Length}): {string.Join(", ", allowedFirefoxExtensions)}");
        Console.WriteLine($"[✓] Target NativeHost binary deployed at: {targetExePath}");
        return allSucceeded;
    }

    public static bool Uninstall()
    {
        bool allSucceeded = true;
        foreach (var (browser, subKey, _) in RegistryTargets)
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

        foreach (var path in new[] { GetChromiumManifestPath(), GetFirefoxManifestPath() })
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    Console.WriteLine($"[✓] Removed manifest file: {path}");
                }
                catch { }
            }
        }

        // Remove shortcuts
        try
        {
            var desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "WDM.lnk");
            var startMenuShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "WDM.lnk");
            if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);
            if (File.Exists(startMenuShortcut)) File.Delete(startMenuShortcut);
            Console.WriteLine("[✓] Removed Desktop and Start Menu shortcuts");
        }
        catch { }

        // Remove Uninstall registry entry
        try
        {
            using var uninstParent = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
            uninstParent?.DeleteSubKeyTree("WDM", throwOnMissingSubKey: false);
            Console.WriteLine("[✓] Removed WDM from Windows Installed Apps");
        }
        catch { }

        return allSucceeded;
    }

    private static string? FindSourceUiExecutable(string hostDir)
    {
        var candidates = new[]
        {
            Path.Combine(hostDir, "..", "WDM.UI", "WDM.UI.exe"),
            Path.Combine(hostDir, "..", "UI", "WDM.UI.exe"),
            Path.Combine(hostDir, "..", "WDM.UI.exe"),
            Path.Combine(hostDir, "WDM.UI.exe"),
            Path.Combine(hostDir, "..", "..", "bin", "WDM.UI", "WDM.UI.exe"),
            Path.Combine(hostDir, "..", "..", "dist", "WDM-v1.0.0-windows-x64", "WDM.UI.exe"),
            Path.Combine(hostDir, "..", "..", "src", "WDM.UI", "bin", "Release", "net10.0-windows", "win-x64", "WDM.UI.exe"),
            Path.Combine(hostDir, "..", "..", "src", "WDM.UI", "bin", "Release", "net10.0-windows", "WDM.UI.exe"),
            Path.Combine(hostDir, "..", "..", "src", "WDM.UI", "bin", "Debug", "net10.0-windows", "WDM.UI.exe")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
            }
            catch { }
        }

        return null;
    }

    private static string? FindExtensionDirectory(string hostDir)
    {
        // 1. Check host_config.json if previously saved
        var configPath = GetConfigPath();
        if (File.Exists(configPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("sourceExtensionDir", out var sProp) && sProp.ValueKind == JsonValueKind.String)
                {
                    var sPath = sProp.GetString();
                    if (!string.IsNullOrWhiteSpace(sPath) && Directory.Exists(sPath) && File.Exists(Path.Combine(sPath, "manifest.json")))
                    {
                        return sPath;
                    }
                }
            }
            catch { }
        }

        var candidates = new List<string>
        {
            Path.Combine(Directory.GetCurrentDirectory(), "extension"),
            Path.Combine(AppContext.BaseDirectory, "extension"),
            Path.Combine(hostDir, "..", "..", "extension"),
            Path.Combine(hostDir, "..", "extension"),
            Path.Combine(hostDir, "extension")
        };

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(userProfile, "OneDrive", "Documents", "Github", "WDM", "extension"));
        candidates.Add(Path.Combine(userProfile, "Documents", "Github", "WDM", "extension"));

        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate).TrimEnd('\\', '/');
                if (Directory.Exists(full) && File.Exists(Path.Combine(full, "manifest.json")))
                {
                    return full;
                }
            }
            catch { }
        }

        return null;
    }

    private static string[] DiscoverChromiumAllowedOrigins(string? extensionDir)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Known unpacked path ID for repo extension folder
            "aemgoennlnmdpdcmbnejahkiimidblga"
        };

        // 1. Compute IDs from extension directory path and manifest.json "key" (if present)
        if (!string.IsNullOrWhiteSpace(extensionDir) && Directory.Exists(extensionDir))
        {
            var normalized = Path.GetFullPath(extensionDir).TrimEnd('\\', '/');
            ids.Add(ComputeChromiumIdFromBytes(Encoding.Unicode.GetBytes(normalized)));

            if (normalized.Length >= 2 && normalized[1] == ':')
            {
                var upperDrive = char.ToUpperInvariant(normalized[0]) + normalized[1..];
                var lowerDrive = char.ToLowerInvariant(normalized[0]) + normalized[1..];
                ids.Add(ComputeChromiumIdFromBytes(Encoding.Unicode.GetBytes(upperDrive)));
                ids.Add(ComputeChromiumIdFromBytes(Encoding.Unicode.GetBytes(lowerDrive)));
            }

            var manifestFile = Path.Combine(normalized, "manifest.json");
            if (File.Exists(manifestFile))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifestFile));
                    if (doc.RootElement.TryGetProperty("key", out var keyProp) && keyProp.ValueKind == JsonValueKind.String)
                    {
                        var keyStr = keyProp.GetString();
                        if (!string.IsNullOrWhiteSpace(keyStr))
                        {
                            var keyBytes = Convert.FromBase64String(keyStr);
                            ids.Add(ComputeChromiumIdFromBytes(keyBytes));
                        }
                    }
                }
                catch { }
            }
        }

        // 2. Scan Chrome, Edge, and Brave User Data Preferences / Secure Preferences for installed WDM extensions
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var browserUserDataDirs = new[]
        {
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
            Path.Combine(localAppData, "Google", "Chrome", "User Data"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data")
        };

        foreach (var userDataDir in browserUserDataDirs)
        {
            if (!Directory.Exists(userDataDir)) continue;

            try
            {
                foreach (var prefFile in Directory.EnumerateFiles(userDataDir, "*Preferences", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(prefFile);
                    if (!string.Equals(fileName, "Preferences", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(fileName, "Secure Preferences", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var content = File.ReadAllText(prefFile);
                        if (!content.Contains("WDM", StringComparison.OrdinalIgnoreCase)) continue;

                        using var doc = JsonDocument.Parse(content);
                        if (doc.RootElement.TryGetProperty("extensions", out var extObj) &&
                            extObj.TryGetProperty("settings", out var settingsObj) &&
                            settingsObj.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in settingsObj.EnumerateObject())
                            {
                                if (prop.Name.Length == 32 && IsValidChromiumId(prop.Name))
                                {
                                    var raw = prop.Value.GetRawText();
                                    if (raw.Contains("WDM", StringComparison.OrdinalIgnoreCase) ||
                                        raw.Contains("Windows Download Manager", StringComparison.OrdinalIgnoreCase))
                                    {
                                        ids.Add(prop.Name.ToLowerInvariant());
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        return ids
            .Where(IsValidChromiumId)
            .Select(id => $"chrome-extension://{id.ToLowerInvariant()}/")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] DiscoverFirefoxAllowedExtensions()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wdm-downloader@local",
            "wdm-downloader@wdm.local",
            "{2a3e3cb2-c5c7-46b3-bc78-fae08fc0f578}",
            "2a3e3cb2-c5c7-46b3-bc78-fae08fc0f578@temporary-addon"
        };

        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var firefoxProfilesDir = Path.Combine(appData, "Mozilla", "Firefox", "Profiles");
            if (Directory.Exists(firefoxProfilesDir))
            {
                foreach (var profileDir in Directory.GetDirectories(firefoxProfilesDir))
                {
                    // 1. Scan prefs.js for registered extension IDs
                    var prefsFile = Path.Combine(profileDir, "prefs.js");
                    if (File.Exists(prefsFile))
                    {
                        try
                        {
                            foreach (var line in File.ReadLines(prefsFile))
                            {
                                if (line.Contains("extensions.webextensions.uuids", StringComparison.OrdinalIgnoreCase))
                                {
                                    var match = Regex.Match(line, @"user_pref\(""extensions\.webextensions\.uuids"",\s*""(.*?)""\);");
                                    if (match.Success)
                                    {
                                        var jsonEscaped = match.Groups[1].Value.Replace(@"\""", "\"");
                                        using var doc = JsonDocument.Parse(jsonEscaped);
                                        foreach (var prop in doc.RootElement.EnumerateObject())
                                        {
                                            var key = prop.Name;
                                            var val = prop.Value.GetString();
                                            if (key.Contains("wdm", StringComparison.OrdinalIgnoreCase) ||
                                                (val != null && val.Contains("wdm", StringComparison.OrdinalIgnoreCase)))
                                            {
                                                if (IsValidFirefoxExtensionId(key))
                                                {
                                                    ids.Add(key);
                                                }
                                                if (!string.IsNullOrWhiteSpace(val))
                                                {
                                                    var cleanGuid = val.Trim('{', '}');
                                                    var bracedGuid = $"{{{cleanGuid}}}";
                                                    var tempAddonId = $"{cleanGuid}@temporary-addon";
                                                    if (IsValidFirefoxExtensionId(bracedGuid)) ids.Add(bracedGuid);
                                                    if (IsValidFirefoxExtensionId(tempAddonId)) ids.Add(tempAddonId);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    // 2. Scan extensions.json for installed addons
                    var extJsonFile = Path.Combine(profileDir, "extensions.json");
                    if (File.Exists(extJsonFile))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(extJsonFile));
                            if (doc.RootElement.TryGetProperty("addons", out var addons) && addons.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var addon in addons.EnumerateArray())
                                {
                                    var id = addon.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                                    var name = addon.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
                                    if (!string.IsNullOrWhiteSpace(id) &&
                                        (id.Contains("wdm", StringComparison.OrdinalIgnoreCase) ||
                                         (name != null && name.Contains("WDM", StringComparison.OrdinalIgnoreCase))))
                                    {
                                        if (IsValidFirefoxExtensionId(id))
                                        {
                                            ids.Add(id);
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }

        return ids.Where(IsValidFirefoxExtensionId).ToArray();
    }

    private static bool IsValidFirefoxExtensionId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        // Firefox NativeHostManifest schema requires ExtensionID to match either:
        // 1. ^{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}}$
        // 2. ^[a-zA-Z0-9-._]*@[a-zA-Z0-9-._]+$
        return Regex.IsMatch(id, @"^(\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}|[a-zA-Z0-9\-._]+@[a-zA-Z0-9\-._]+)$");
    }

    public static string ComputeChromiumIdFromBytes(byte[] input)
    {
        var hash = SHA256.HashData(input);
        Span<char> chars = stackalloc char[32];
        for (int i = 0; i < 16; i++)
        {
            byte b = hash[i];
            chars[i * 2] = (char)('a' + ((b >> 4) & 0xF));
            chars[i * 2 + 1] = (char)('a' + (b & 0xF));
        }
        return new string(chars);
    }

    private static bool IsValidChromiumId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length != 32) return false;
        foreach (var ch in id)
        {
            if (ch < 'a' || ch > 'p') return false;
        }
        return true;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, destSubDir);
        }
    }

    public static void PackageExtension(string sourceExtensionDir, string outputZipPath)
    {
        try
        {
            if (File.Exists(outputZipPath)) File.Delete(outputZipPath);

            using var zipStream = new FileStream(outputZipPath, FileMode.Create, FileAccess.Write);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

            foreach (var filePath in Directory.EnumerateFiles(sourceExtensionDir, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(filePath);
                if (fileName.EndsWith(".xpi", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".crx", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(sourceExtensionDir, filePath).Replace('\\', '/');
                archive.CreateEntryFromFile(filePath, relativePath, CompressionLevel.Optimal);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Notice] Could not package {Path.GetFileName(outputZipPath)}: {ex.Message}");
        }
    }

    private static void TryInstallFirefoxDistribution(string xpiPath)
    {
        try
        {
            var firefoxDirs = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox")
            };

            foreach (var ffDir in firefoxDirs)
            {
                if (!Directory.Exists(ffDir)) continue;

                var distDir = Path.Combine(ffDir, "distribution");
                var extDir = Path.Combine(distDir, "extensions");

                // Try copying .xpi directly into distribution\extensions\
                try
                {
                    Directory.CreateDirectory(extDir);
                    var targetFfXpi = Path.Combine(extDir, "wdm-downloader@local.xpi");
                    File.Copy(xpiPath, targetFfXpi, overwrite: true);
                    Console.WriteLine($"[✓] Deployed extension into Firefox distribution: {targetFfXpi}");
                }
                catch { }

                // Try creating/merging policies.json in distribution\
                try
                {
                    Directory.CreateDirectory(distDir);
                    var policiesFile = Path.Combine(distDir, "policies.json");
                    var uriPath = "file:///" + xpiPath.Replace('\\', '/');

                    var policiesObj = new
                    {
                        policies = new
                        {
                            ExtensionSettings = new Dictionary<string, object>
                            {
                                ["wdm-downloader@local"] = new
                                {
                                    installation_mode = "normal_installed",
                                    install_url = uriPath
                                }
                            }
                        }
                    };

                    File.WriteAllText(policiesFile, JsonSerializer.Serialize(policiesObj, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine($"[✓] Deployed Firefox policies.json into: {policiesFile}");
                }
                catch { }
            }
        }
        catch { }
    }

    private static string? FindSourceIcon(string hostDir)
    {
        var candidates = new[]
        {
            Path.Combine(hostDir, "icon.ico"),
            Path.Combine(hostDir, "..", "assets", "icon.ico"),
            Path.Combine(hostDir, "..", "..", "assets", "icon.ico"),
            Path.Combine(hostDir, "..", "icon.ico"),
            Path.Combine(hostDir, "..", "..", "src", "WDM.UI", "assets", "icon.ico")
        };

        foreach (var c in candidates)
        {
            try
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    private static void CreateShortcuts(string targetExePath, string iconPath)
    {
        try
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");

            var desktopShortcut = Path.Combine(desktopDir, "WDM.lnk");
            var startMenuShortcut = Path.Combine(startMenuDir, "WDM.lnk");

            CreateShortcutInternal(desktopShortcut, targetExePath, iconPath, "WDM - Windows Download Manager");
            CreateShortcutInternal(startMenuShortcut, targetExePath, iconPath, "WDM - Windows Download Manager");

            Console.WriteLine($"[✓] Created Desktop shortcut: {desktopShortcut}");
            Console.WriteLine($"[✓] Created Start Menu shortcut: {startMenuShortcut}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Notice] Could not create shortcuts: {ex.Message}");
        }
    }

    private static void CreateShortcutInternal(string shortcutPath, string targetExePath, string iconPath, string description)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetExePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetExePath);
            shortcut.Description = description;
            if (File.Exists(iconPath))
            {
                shortcut.IconLocation = $"{iconPath},0";
            }
            shortcut.Save();
        }
        catch { }
    }

    private static void RegisterInWindowsProgramsAndFeatures(string appData, string nativeHostExe, string uiExe, string iconPath)
    {
        try
        {
            const string uninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WDM";
            using var key = Registry.CurrentUser.CreateSubKey(uninstallKeyPath);
            if (key == null) return;

            key.SetValue("DisplayName", "WDM - Windows Download Manager", RegistryValueKind.String);
            key.SetValue("DisplayVersion", "1.0.0", RegistryValueKind.String);
            key.SetValue("Publisher", "WDM Project", RegistryValueKind.String);
            key.SetValue("InstallLocation", appData, RegistryValueKind.String);
            key.SetValue("UninstallString", $"\"{nativeHostExe}\" --uninstall", RegistryValueKind.String);
            key.SetValue("QuietUninstallString", $"\"{nativeHostExe}\" --uninstall", RegistryValueKind.String);
            if (File.Exists(iconPath)) key.SetValue("DisplayIcon", iconPath, RegistryValueKind.String);
            else if (File.Exists(uiExe)) key.SetValue("DisplayIcon", uiExe, RegistryValueKind.String);
            key.SetValue("HelpLink", "https://github.com/alfredjoejr/WDM", RegistryValueKind.String);
            key.SetValue("URLInfoAbout", "https://github.com/alfredjoejr/WDM", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", 85000, RegistryValueKind.DWord);

            Console.WriteLine("[✓] Registered WDM in Windows Installed Apps (Programs and Features)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Notice] Could not register in Windows Programs and Features: {ex.Message}");
        }
    }
}
