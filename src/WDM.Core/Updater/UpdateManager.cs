using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace WDM.Core.Updater;

public record UpdateCheckResult
{
    public string CurrentVersion { get; init; } = "1.0.0";
    public string LatestVersion { get; init; } = "1.0.0";
    public bool IsUpdateAvailable { get; init; }
    public string? ReleaseTitle { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ReleasePageUrl { get; init; }
    public string? PublishedAt { get; init; }
    public bool HasLocalRepo { get; init; }
    public string? LocalRepoPath { get; init; }
    public bool HasLocalChanges { get; init; }
    public string? LocalLastModifiedText { get; init; }
    public string? CheckError { get; init; }
}

public class UpdateManager
{
    public const string DefaultGitHubRepo = "alfredjoejr/WDM";
    public const string AppVersion = "1.0.0";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WDM-Updater", AppVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static string GetCurrentVersion()
    {
        try
        {
            var entryVer = Assembly.GetEntryAssembly()?.GetName().Version;
            if (entryVer != null && (entryVer.Major > 0 || entryVer.Minor > 0))
            {
                return $"{entryVer.Major}.{entryVer.Minor}.{Math.Max(0, entryVer.Build)}";
            }
        }
        catch { }
        return AppVersion;
    }

    public static string GetAppDataDirectory()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Locates the local git/source repository on this machine (if present) so the user can hot-sync code edits without reinstalling.
    /// </summary>
    public static string? DiscoverLocalRepositoryPath()
    {
        var candidates = new List<string>();

        // 1. Check host_config.json saved by HostInstaller
        try
        {
            var configPath = Path.Combine(GetAppDataDirectory(), "host_config.json");
            if (File.Exists(configPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                var root = doc.RootElement;

                if (root.TryGetProperty("repoRootDir", out var repoProp) && repoProp.ValueKind == JsonValueKind.String)
                {
                    var r = repoProp.GetString();
                    if (!string.IsNullOrWhiteSpace(r)) candidates.Add(r);
                }

                if (root.TryGetProperty("sourceExtensionDir", out var extProp) && extProp.ValueKind == JsonValueKind.String)
                {
                    var ext = extProp.GetString();
                    if (!string.IsNullOrWhiteSpace(ext))
                    {
                        var parent = Directory.GetParent(ext.TrimEnd('\\', '/'))?.FullName;
                        if (!string.IsNullOrWhiteSpace(parent)) candidates.Add(parent);
                    }
                }

                if (root.TryGetProperty("sourceUiExePath", out var uiProp) && uiProp.ValueKind == JsonValueKind.String)
                {
                    var ui = uiProp.GetString();
                    if (!string.IsNullOrWhiteSpace(ui))
                    {
                        var dir = Path.GetDirectoryName(ui);
                        if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(dir);
                    }
                }
            }
        }
        catch { }

        // 2. Walk up from current BaseDirectory (if running directly from bin/Debug or bin/Release)
        candidates.Add(AppDomain.CurrentDomain.BaseDirectory);

        // 3. Common user GitHub folders
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(userProfile, "OneDrive", "Documents", "Github", "WDM"));
        candidates.Add(Path.Combine(userProfile, "Documents", "Github", "WDM"));
        candidates.Add(Path.Combine(userProfile, "source", "repos", "WDM"));

        foreach (var startPath in candidates)
        {
            if (string.IsNullOrWhiteSpace(startPath)) continue;
            try
            {
                var current = new DirectoryInfo(startPath);
                for (int depth = 0; depth < 6 && current != null; depth++)
                {
                    var slnPath = Path.Combine(current.FullName, "WDM.sln");
                    var uiProjPath = Path.Combine(current.FullName, "src", "WDM.UI", "WDM.UI.csproj");
                    if (File.Exists(slnPath) && File.Exists(uiProjPath))
                    {
                        return current.FullName;
                    }
                    current = current.Parent;
                }
            }
            catch { }
        }

        return null;
    }

    public static (bool HasChanges, string? LastModifiedText) DetectUnbuiltLocalChanges(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (false, null);

        try
        {
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
                return (false, null);

            var exeWriteTime = File.GetLastWriteTimeUtc(currentExe);
            DateTime latestSourceWrite = DateTime.MinValue;
            string? latestFileName = null;

            var dirsToScan = new[]
            {
                Path.Combine(repoPath, "src"),
                Path.Combine(repoPath, "extension")
            };

            foreach (var scanDir in dirsToScan)
            {
                if (!Directory.Exists(scanDir)) continue;
                foreach (var file in Directory.EnumerateFiles(scanDir, "*.*", SearchOption.AllDirectories))
                {
                    if (file.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) ||
                        file.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".xpi", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var wt = File.GetLastWriteTimeUtc(file);
                    if (wt > latestSourceWrite)
                    {
                        latestSourceWrite = wt;
                        latestFileName = Path.GetFileName(file);
                    }
                }
            }

            if (latestSourceWrite > exeWriteTime.AddSeconds(5))
            {
                var diff = DateTime.UtcNow - latestSourceWrite;
                string ago = diff.TotalMinutes < 1 ? "just now"
                    : diff.TotalHours < 1 ? $"{(int)diff.TotalMinutes}m ago"
                    : diff.TotalDays < 1 ? $"{(int)diff.TotalHours}h ago"
                    : $"{(int)diff.TotalDays}d ago";

                return (true, $"Modified {latestFileName} ({ago})");
            }
        }
        catch { }

        return (false, null);
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string repoSlug = DefaultGitHubRepo, CancellationToken ct = default)
    {
        var currentVerStr = GetCurrentVersion();
        var localRepo = DiscoverLocalRepositoryPath();
        var (hasLocalChanges, localModText) = DetectUnbuiltLocalChanges(localRepo);

        try
        {
            var url = $"https://api.github.com/repos/{repoSlug}/releases/latest";
            using var response = await Http.GetAsync(url, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // No GitHub release published yet
                return new UpdateCheckResult
                {
                    CurrentVersion = currentVerStr,
                    LatestVersion = currentVerStr,
                    IsUpdateAvailable = false,
                    ReleaseTitle = "Up to date (No newer GitHub releases published yet)",
                    ReleasePageUrl = $"https://github.com/{repoSlug}/releases",
                    HasLocalRepo = !string.IsNullOrWhiteSpace(localRepo),
                    LocalRepoPath = localRepo,
                    HasLocalChanges = hasLocalChanges,
                    LocalLastModifiedText = localModText
                };
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? currentVerStr : currentVerStr;
            var cleanLatest = tagName.TrimStart('v', 'V').Split('-')[0];
            var releaseName = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : tagName;
            var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : $"https://github.com/{repoSlug}/releases";
            var publishedAt = root.TryGetProperty("published_at", out var pubProp) ? pubProp.GetString() : null;

            string? zipDownloadUrl = null;
            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var anProp) ? anProp.GetString() ?? "" : "";
                    var dlUrl = asset.TryGetProperty("browser_download_url", out var dlProp) ? dlProp.GetString() : null;
                    if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(dlUrl))
                    {
                        zipDownloadUrl = dlUrl;
                        break;
                    }
                }
            }

            bool isNewer = false;
            if (Version.TryParse(NormalizeVersion(cleanLatest), out var latestVersion) &&
                Version.TryParse(NormalizeVersion(currentVerStr), out var currentVersion))
            {
                isNewer = latestVersion > currentVersion;
            }
            else if (!string.Equals(cleanLatest, currentVerStr, StringComparison.OrdinalIgnoreCase))
            {
                isNewer = true;
            }

            return new UpdateCheckResult
            {
                CurrentVersion = currentVerStr,
                LatestVersion = cleanLatest,
                IsUpdateAvailable = isNewer,
                ReleaseTitle = releaseName,
                ReleaseNotes = body,
                DownloadUrl = zipDownloadUrl,
                ReleasePageUrl = htmlUrl,
                PublishedAt = publishedAt,
                HasLocalRepo = !string.IsNullOrWhiteSpace(localRepo),
                LocalRepoPath = localRepo,
                HasLocalChanges = hasLocalChanges,
                LocalLastModifiedText = localModText
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                CurrentVersion = currentVerStr,
                LatestVersion = currentVerStr,
                IsUpdateAvailable = false,
                HasLocalRepo = !string.IsNullOrWhiteSpace(localRepo),
                LocalRepoPath = localRepo,
                HasLocalChanges = hasLocalChanges,
                LocalLastModifiedText = localModText,
                CheckError = $"GitHub check offline or rate-limited: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Downloads a GitHub Release ZIP bundle, stages it, spawns a background self-swap script, and restarts WDM.UI without losing settings or extension registrations.
    /// </summary>
    public async Task<(bool Success, string Message)> DownloadAndApplyGitHubUpdateAsync(
        string downloadUrl,
        Action<double, string>? onProgress = null,
        CancellationToken ct = default)
    {
        try
        {
            var appData = GetAppDataDirectory();
            var updatesDir = Path.Combine(appData, "Updates");
            var stagingDir = Path.Combine(updatesDir, "github_staging");
            var zipPath = Path.Combine(updatesDir, "wdm_release_update.zip");

            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);

            onProgress?.Invoke(5, "Connecting to GitHub Release server...");

            using (var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1L;

                await using var netStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    readTotal += read;
                    if (totalBytes > 0)
                    {
                        var pct = 10 + (readTotal / (double)totalBytes) * 65;
                        onProgress?.Invoke(Math.Min(75, pct), $"Downloading update package ({readTotal / 1024 / 1024.0:F1} MB)...");
                    }
                }
            }

            onProgress?.Invoke(80, "Extracting release archive...");
            ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);

            onProgress?.Invoke(92, "Preparing in-place hot-swap installer...");
            LaunchSelfSwapScriptFromReleaseStaging(stagingDir, appData);

            onProgress?.Invoke(100, "Restarting WDM with new update...");
            return (true, "Update staged. Restarting application...");
        }
        catch (Exception ex)
        {
            return (false, $"Update failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Compiles local source code changes from the developer repository into a staging folder,
    /// then hot-swaps the installed binaries in %LOCALAPPDATA%\WDM and restarts WDM.UI automatically.
    /// </summary>
    public async Task<(bool Success, string Message)> BuildAndHotSyncFromLocalRepoAsync(
        string? customRepoPath = null,
        Action<double, string>? onProgress = null,
        CancellationToken ct = default)
    {
        var repoPath = customRepoPath ?? DiscoverLocalRepositoryPath();
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return (false, "Could not locate local WDM source repository.");
        }

        try
        {
            var appData = GetAppDataDirectory();
            var updatesDir = Path.Combine(appData, "Updates");
            var stagingUiDir = Path.Combine(updatesDir, "local_staging", "UI");
            var stagingHostDir = Path.Combine(updatesDir, "local_staging", "NativeHost");
            var sourceExtDir = Path.Combine(repoPath, "extension");

            if (Directory.Exists(Path.Combine(updatesDir, "local_staging")))
            {
                try { Directory.Delete(Path.Combine(updatesDir, "local_staging"), true); } catch { }
            }
            Directory.CreateDirectory(stagingUiDir);
            Directory.CreateDirectory(stagingHostDir);

            onProgress?.Invoke(15, "Building WDM.NativeHost (Release)...");
            var hostProj = Path.Combine(repoPath, "src", "WDM.NativeHost", "WDM.NativeHost.csproj");
            var (hostOk, hostErr) = await RunDotnetPublishAsync(hostProj, stagingHostDir, ct);
            if (!hostOk)
            {
                return (false, $"Failed building WDM.NativeHost: {hostErr}");
            }

            onProgress?.Invoke(55, "Building WDM.UI Dashboard (Release)...");
            var uiProj = Path.Combine(repoPath, "src", "WDM.UI", "WDM.UI.csproj");
            var (uiOk, uiErr) = await RunDotnetPublishAsync(uiProj, stagingUiDir, ct);
            if (!uiOk)
            {
                return (false, $"Failed building WDM.UI: {uiErr}");
            }

            onProgress?.Invoke(90, "Applying hot-swap update & syncing browser extension...");
            LaunchSelfSwapScriptFromLocalStaging(stagingUiDir, stagingHostDir, sourceExtDir, appData);

            onProgress?.Invoke(100, "Restarting WDM Dashboard...");
            return (true, "Local build succeeded! Restarting WDM...");
        }
        catch (Exception ex)
        {
            return (false, $"Hot-sync failed: {ex.Message}");
        }
    }

    private static async Task<(bool Success, string Output)> RunDotnetPublishAsync(string projectPath, string outputDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{projectPath}\" -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o \"{outputDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return (false, "Could not start dotnet CLI.");

        var stdOutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return (proc.ExitCode == 0, string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr);
    }

    private static void LaunchSelfSwapScriptFromLocalStaging(string stagingUiDir, string stagingHostDir, string sourceExtDir, string appData)
    {
        var targetUiDir = Path.Combine(appData, "UI");
        var targetHostDir = Path.Combine(appData, "NativeHost");
        var targetExtDir = Path.Combine(appData, "Extension");
        var scriptPath = Path.Combine(appData, "Updates", "apply_local_sync.bat");

        var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(targetUiDir, "WDM.UI.exe");
        var currentDir = Path.GetDirectoryName(currentExe) ?? targetUiDir;

        var bat = $@"@echo off
timeout /t 1 /nobreak >nul
taskkill /F /IM WDM.UI.exe >nul 2>&1
taskkill /F /IM WDM.NativeHost.exe >nul 2>&1
timeout /t 1 /nobreak >nul

xcopy /E /I /Y ""{stagingHostDir}\*"" ""{targetHostDir}\"" >nul 2>&1
xcopy /E /I /Y ""{stagingUiDir}\*"" ""{targetUiDir}\"" >nul 2>&1
if exist ""{sourceExtDir}"" (
    xcopy /E /I /Y ""{sourceExtDir}\*"" ""{targetExtDir}\"" >nul 2>&1
)

if /I not ""{currentDir.TrimEnd('\\')}""==""{targetUiDir.TrimEnd('\\')}"" (
    xcopy /E /I /Y ""{stagingUiDir}\*"" ""{currentDir}\"" >nul 2>&1
)

if exist ""{targetHostDir}\WDM.NativeHost.exe"" (
    ""{targetHostDir}\WDM.NativeHost.exe"" --install >nul 2>&1
)

start """" ""{Path.Combine(targetUiDir, "WDM.UI.exe")}""
exit
";
        File.WriteAllText(scriptPath, bat);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{scriptPath}\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static void LaunchSelfSwapScriptFromReleaseStaging(string stagingDir, string appData)
    {
        // Find where WDM.UI.exe is inside the extracted staging folder (in case the zip has a root folder)
        var uiExeMatch = Directory.EnumerateFiles(stagingDir, "WDM.UI.exe", SearchOption.AllDirectories).FirstOrDefault();
        var extractedRoot = uiExeMatch != null ? Path.GetDirectoryName(uiExeMatch)! : stagingDir;

        var targetUiDir = Path.Combine(appData, "UI");
        var targetHostDir = Path.Combine(appData, "NativeHost");
        var targetExtDir = Path.Combine(appData, "Extension");
        var scriptPath = Path.Combine(appData, "Updates", "apply_github_update.bat");

        var extractedHostDir = Path.Combine(extractedRoot, "NativeHost");
        var extractedExtDir = Path.Combine(extractedRoot, "extension");

        var bat = $@"@echo off
timeout /t 1 /nobreak >nul
taskkill /F /IM WDM.UI.exe >nul 2>&1
taskkill /F /IM WDM.NativeHost.exe >nul 2>&1
timeout /t 1 /nobreak >nul

xcopy /E /I /Y ""{extractedRoot}\*"" ""{targetUiDir}\"" >nul 2>&1
if exist ""{extractedHostDir}"" (
    xcopy /E /I /Y ""{extractedHostDir}\*"" ""{targetHostDir}\"" >nul 2>&1
)
if exist ""{extractedExtDir}"" (
    xcopy /E /I /Y ""{extractedExtDir}\*"" ""{targetExtDir}\"" >nul 2>&1
)

if exist ""{targetHostDir}\WDM.NativeHost.exe"" (
    ""{targetHostDir}\WDM.NativeHost.exe"" --install >nul 2>&1
)

start """" ""{Path.Combine(targetUiDir, "WDM.UI.exe")}""
exit
";
        File.WriteAllText(scriptPath, bat);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{scriptPath}\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static string NormalizeVersion(string v)
    {
        var parts = v.Trim().Split('.');
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => $"{parts[0]}.{parts[1]}.{parts[2]}"
        };
    }
}

