using System.Diagnostics;
using System.Runtime.Versioning;
using WDM.NativeHost;

namespace WDM.Setup;

[SupportedOSPlatform("windows")]
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "WDM - Windows Download Manager Setup";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
===================================================================
        WDM (Windows Download Manager) v1.0.0 Setup Wizard
===================================================================");
        Console.ResetColor();

        Console.WriteLine("\n[1/3] Deploying application binaries, native host, and extension assets...");
        var ok = HostInstaller.Install();
        if (!ok)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Error] Installation encountered issues. Please try running as Administrator.");
            Console.ResetColor();
            return 1;
        }

        var appData = HostInstaller.GetAppDataDirectory();
        var extDir = Path.Combine(appData, "Extension");
        var xpiPath = Path.Combine(extDir, "wdm-downloader@local.xpi");
        var amoZipPath = Path.Combine(extDir, "wdm-downloader-amo.zip");
        var desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "WDM.lnk");
        var startMenuShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "WDM.lnk");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(@"
===================================================================
              WDM INSTALLATION COMPLETED SUCCESSFULLY!
===================================================================");
        Console.ResetColor();

        Console.WriteLine($@"
Installed Directory:
  {appData}

Shortcuts Created:
  [✓] Desktop: {desktopShortcut}
  [✓] Start Menu: {startMenuShortcut}

Windows Settings:
  [✓] Registered in Windows Apps & Features (Add or Remove Programs)
");

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("-------------------------------------------------------------------");
        Console.WriteLine("              PERMANENT BROWSER EXTENSION SETUP GUIDE");
        Console.WriteLine("-------------------------------------------------------------------");
        Console.ResetColor();

        Console.WriteLine($@"
1. MICROSOFT EDGE / GOOGLE CHROME / BRAVE:
   Permanent Extension Location:
   {extDir}

   Instructions:
   • In Edge: Open edge://extensions (In Chrome: chrome://extensions)
   • Toggle ON 'Developer mode' (top or left menu)
   • Click 'Load unpacked'
   • Select folder: {extDir}
   -> Done! The extension is permanently active across all restarts.

2. MOZILLA FIREFOX:
   Packaged XPI Location:
   {xpiPath}
   AMO Submission Archive (for 1-click official signature):
   {amoZipPath}

   Option A (Official Free Permanent Signing via Mozilla AMO):
   • Go to: https://addons.mozilla.org/developers/addon/submit/distribution
   • Choose 'On your own' (Unlisted distribution)
   • Upload 'wdm-downloader-amo.zip'
   • Mozilla auto-signs it in 2-3 minutes (no review needed)
   • Download and drag the signed .xpi into Firefox -> Permanent forever!

   Option B (Firefox Developer / ESR Mode):
   • Set 'xpinstall.signatures.required' to false in about:config
   • In about:addons, click the gear icon -> 'Install Add-on From File...'
   • Select 'wdm-downloader@local.xpi' -> Permanent!
");

        Console.Write("Would you like to launch WDM now? (Y/n): ");
        var key = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(key) || key.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            var uiExe = Path.Combine(appData, "UI", "WDM.UI.exe");
            if (File.Exists(uiExe))
            {
                Process.Start(new ProcessStartInfo(uiExe) { UseShellExecute = true });
            }
        }

        return 0;
    }
}
