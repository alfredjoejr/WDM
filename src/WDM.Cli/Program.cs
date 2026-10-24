using System.Diagnostics;
using Spectre.Console;
using WDM.Core.Engine;
using WDM.Core.Media;
using WDM.Core.Models;
using WDM.Core.Network;
using WDM.Core.Security;

namespace WDM.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0)
        {
            RenderBanner();
            RenderHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "download" or "d" => await HandleDownloadAsync(args[1..]),
                "probe" or "p" => await HandleProbeAsync(args[1..]),
                "hash" or "h" => await HandleHashAsync(args[1..]),
                "media" or "m" => await HandleMediaAsync(args[1..]),
                "benchmark" or "b" => await HandleBenchmarkAsync(args[1..]),
                "--help" or "-h" or "help" => HandleHelp(),
                _ => HandleUnknown(command)
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static void RenderBanner()
    {
        var rule = new Rule("[bold cyan]WDM[/] • [bold white]Next-Gen Windows Download Manager[/]")
        {
            Style = Style.Parse("cyan dim")
        };
        AnsiConsole.Write(rule);
        AnsiConsole.MarkupLine("[dim]Engine: .NET 10 | Protocol: HTTP/3 & Multi-Chunk | Zero-Stitch I/O[/]\n");
    }

    private static int HandleHelp()
    {
        RenderBanner();
        RenderHelp();
        return 0;
    }

    private static int HandleUnknown(string cmd)
    {
        AnsiConsole.MarkupLine($"[bold red]Unknown command:[/] [yellow]{cmd}[/]");
        RenderHelp();
        return 1;
    }

    private static void RenderHelp()
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1);
        table.AddColumn("[bold cyan]Command[/]");
        table.AddColumn("[bold cyan]Arguments[/]");
        table.AddColumn("[bold cyan]Description[/]");

        table.AddRow("download", Markup.Escape("<url> [-c 16] [-o dir] [-f name] [-l 5MB]"), "Download file with multi-connection parallel chunking");
        table.AddRow("probe", Markup.Escape("<url>"), "Inspect server capabilities, range support, HTTP version & size");
        table.AddRow("hash", Markup.Escape("<filepath> [sha256|sha1|md5]"), "Calculate and verify cryptographic checksum");
        table.AddRow("media", Markup.Escape("<url>"), "Inspect streaming media formats (YouTube, HLS, DASH, etc.)");
        table.AddRow("benchmark", Markup.Escape("<url>"), "Compare 1 vs 4 vs 16 vs 32 parallel connections");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Examples:[/]");
        AnsiConsole.MarkupLine("  wdm download https://proof.ovh.net/files/100Mb.dat -c 16");
        AnsiConsole.MarkupLine("  wdm probe https://speed.cloudflare.com/__down?bytes=100000000");
        AnsiConsole.MarkupLine("  wdm hash C:\\Users\\user\\Downloads\\file.zip sha256");
    }

    private static async Task<int> HandleProbeAsync(string[] args)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] wdm probe <url>");
            return 1;
        }

        var url = args[0];
        RenderBanner();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Probing target: [cyan]{url}[/]...", async _ =>
            {
                var probe = new HttpProbe();
                var meta = await probe.ProbeAsync(url);

                var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Green);
                table.AddColumn("[bold green]Property[/]");
                table.AddColumn("[bold white]Value[/]");

                table.AddRow("Resolved File Name", $"[bold]{Markup.Escape(meta.FileName)}[/]");
                table.AddRow("Total Size", meta.TotalBytes.HasValue ? DownloadProgressSnapshot.FormatBytes(meta.TotalBytes.Value) : "Unknown (Streaming)");
                table.AddRow("Accepts Ranges", meta.AcceptsRanges ? "[bold green]YES (Multi-thread chunking enabled)[/]" : "[bold yellow]NO (Single-stream fallback)[/]");
                table.AddRow("HTTP Version", meta.HttpVersion?.ToString() ?? "Default");
                table.AddRow("Content-Type", meta.ContentType ?? "Unknown");
                table.AddRow("ETag", meta.ETag ?? "None");
                table.AddRow("Last-Modified", meta.LastModified?.ToString("yyyy-MM-dd HH:mm:ss") ?? "None");
                table.AddRow("Final URL", Markup.Escape(meta.FinalUrl));

                AnsiConsole.Write(table);
            });

        return 0;
    }

    private static async Task<int> HandleDownloadAsync(string[] args)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] wdm download <url> [-c connections] [-o outputDir] [-f filename] [-l limit]");
            return 1;
        }

        var url = args[0];
        int connections = 16;
        string? outputDir = null;
        string? fileName = null;
        long? speedLimit = null;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "-c" && i + 1 < args.Length && int.TryParse(args[i + 1], out var c))
            {
                connections = Math.Clamp(c, 1, 64);
                i++;
            }
            else if (args[i] == "-o" && i + 1 < args.Length)
            {
                outputDir = args[i + 1];
                i++;
            }
            else if (args[i] == "-f" && i + 1 < args.Length)
            {
                fileName = args[i + 1];
                i++;
            }
            else if (args[i] == "-l" && i + 1 < args.Length)
            {
                speedLimit = ParseSpeedLimit(args[i + 1]);
                i++;
            }
        }

        RenderBanner();

        var options = new DownloadOptions
        {
            Url = url,
            Connections = connections,
            DestinationDirectory = outputDir,
            CustomFileName = fileName,
            MaxBytesPerSecond = speedLimit
        };

        var engine = new DownloadEngine();

        engine.LogMessage += msg =>
        {
            AnsiConsole.MarkupLine($"[grey][[{DateTime.Now:HH:mm:ss}]][/] {Markup.Escape(msg)}");
        };

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            AnsiConsole.MarkupLine("[bold yellow]\nCancelling download and saving state...[/]");
            cts.Cancel();
        };

        string finalPath = string.Empty;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var mainTask = ctx.AddTask($"[cyan]Initializing...[/]", maxValue: 100);
                var chunkTasks = new Dictionary<int, ProgressTask>();

                engine.ProgressChanged += snapshot =>
                {
                    if (snapshot.TotalBytes.HasValue)
                    {
                        var speedText = snapshot.SpeedFormatted;
                        var transferred = $"{snapshot.DownloadedFormatted} / {snapshot.TotalFormatted}";
                        mainTask.Description = $"[cyan]{Markup.Escape(engine.Metadata?.FileName ?? "File")}[/] [bold green]{speedText}[/] [dim]({transferred})[/]";
                        mainTask.Value = snapshot.ProgressPercentage;
                    }
                    else
                    {
                        mainTask.Description = $"[cyan]Downloading (Stream)...[/] [bold green]{snapshot.SpeedFormatted}[/]";
                    }
                };

                finalPath = await engine.StartAsync(options, cts.Token);
                mainTask.Value = 100;
            });

        AnsiConsole.MarkupLine($"\n[bold green]Download Complete![/] [white]{Markup.Escape(finalPath)}[/]");

        // Auto-compute SHA-256
        if (File.Exists(finalPath))
        {
            var hash = await ChecksumVerifier.ComputeHashAsync(finalPath, HashAlgorithmType.Sha256);
            AnsiConsole.MarkupLine($"[dim]SHA-256:[/] [cyan]{hash}[/]");
        }

        return 0;
    }

    private static async Task<int> HandleHashAsync(string[] args)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] wdm hash <filepath> [sha256|sha1|md5]");
            return 1;
        }

        var path = args[0];
        var algoName = args.Length > 1 ? args[1].ToLowerInvariant() : "sha256";

        var algo = algoName switch
        {
            "sha1" => HashAlgorithmType.Sha1,
            "md5" => HashAlgorithmType.Md5,
            "sha512" => HashAlgorithmType.Sha512,
            _ => HashAlgorithmType.Sha256
        };

        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {Markup.Escape(path)}");
            return 1;
        }

        RenderBanner();

        await AnsiConsole.Status().StartAsync($"Computing {algo}...", async _ =>
        {
            var hash = await ChecksumVerifier.ComputeHashAsync(path, algo);
            AnsiConsole.MarkupLine($"[bold]File:[/] {Markup.Escape(Path.GetFileName(path))}");
            AnsiConsole.MarkupLine($"[bold]{algo}:[/] [bold green]{hash}[/]");
        });

        return 0;
    }

    private static async Task<int> HandleMediaAsync(string[] args)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] wdm media <url>");
            return 1;
        }

        var url = args[0];
        RenderBanner();

        var media = new MediaExtractor();
        if (!media.IsSupported(url))
        {
            AnsiConsole.MarkupLine("[yellow]Note:[/] URL does not match known direct stream patterns, attempting media extraction anyway...");
        }

        var available = await media.IsEngineAvailableAsync();
        if (!available)
        {
            AnsiConsole.MarkupLine("[bold red]yt-dlp is not installed or not in PATH.[/]");
            AnsiConsole.MarkupLine("[dim]To enable full YouTube, HLS, and social video sniffing, install yt-dlp via: winget install yt-dlp[/]");
            return 1;
        }

        await AnsiConsole.Status().StartAsync("Sniffing media streams...", async _ =>
        {
            var metadata = await media.ExtractMetadataAsync(url);
            if (metadata == null)
            {
                AnsiConsole.MarkupLine("[red]Could not extract media metadata.[/]");
                return;
            }

            AnsiConsole.MarkupLine($"[bold cyan]Title:[/] {Markup.Escape(metadata.Title)}");
            if (metadata.Uploader != null) AnsiConsole.MarkupLine($"[bold]Uploader:[/] {Markup.Escape(metadata.Uploader)}");
            if (metadata.Duration.HasValue) AnsiConsole.MarkupLine($"[bold]Duration:[/] {metadata.Duration.Value:hh\\:mm\\:ss}");

            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Blue);
            table.AddColumn("[bold]Format ID[/]");
            table.AddColumn("[bold]Resolution[/]");
            table.AddColumn("[bold]Extension[/]");
            table.AddColumn("[bold]Estimated Size[/]");
            table.AddColumn("[bold]Notes[/]");

            foreach (var f in metadata.Formats.Take(15))
            {
                var size = f.FileSizeBytes.HasValue ? DownloadProgressSnapshot.FormatBytes(f.FileSizeBytes.Value) : "Dynamic";
                table.AddRow(f.FormatId, f.Resolution, f.Extension ?? "mp4", size, Markup.Escape(f.Note ?? "-"));
            }

            AnsiConsole.Write(table);
        });

        return 0;
    }

    private static async Task<int> HandleBenchmarkAsync(string[] args)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] wdm benchmark <url>");
            return 1;
        }

        var url = args[0];
        RenderBanner();
        AnsiConsole.MarkupLine($"[bold yellow]Starting Multi-Connection Speed Benchmark on:[/] [cyan]{url}[/]\n");

        int[] connectionCounts = [1, 4, 8, 16];
        var results = new List<(int Connections, double SpeedMBs, double TimeSec)>();

        var tempDir = Path.Combine(Path.GetTempPath(), "wdm_benchmark_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            foreach (var conn in connectionCounts)
            {
                AnsiConsole.MarkupLine($"[cyan]Testing with {conn} parallel connection(s)...[/]");
                var sw = Stopwatch.StartNew();

                var engine = new DownloadEngine();
                double peakSpeed = 0;

                engine.ProgressChanged += s =>
                {
                    if (s.SpeedBytesPerSecond > peakSpeed) peakSpeed = s.SpeedBytesPerSecond;
                };

                var options = new DownloadOptions
                {
                    Url = url,
                    Connections = conn,
                    DestinationDirectory = tempDir,
                    CustomFileName = $"bench_{conn}.tmp",
                    OverwriteExisting = true
                };

                await engine.StartAsync(options);
                sw.Stop();

                var speedMBs = peakSpeed / (1024.0 * 1024.0);
                results.Add((conn, speedMBs, sw.Elapsed.TotalSeconds));
                AnsiConsole.MarkupLine($"  -> Finished in [bold white]{sw.Elapsed.TotalSeconds:0.##}s[/] (Peak Speed: [bold green]{speedMBs:0.##} MB/s[/])\n");
            }

            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Green);
            table.AddColumn("[bold]Connections[/]");
            table.AddColumn("[bold]Total Time (s)[/]");
            table.AddColumn("[bold]Peak Speed (MB/s)[/]");
            table.AddColumn("[bold]Speedup vs 1 Conn[/]");

            var baseTime = results.First().TimeSec;

            foreach (var r in results)
            {
                var speedup = baseTime / r.TimeSec;
                table.AddRow(
                    r.Connections.ToString(), 
                    $"{r.TimeSec:0.##}s", 
                    $"{r.SpeedMBs:0.##} MB/s", 
                    r.Connections == 1 ? "1.0x (Baseline)" : $"[bold green]{speedup:0.##}x faster[/]");
            }

            AnsiConsole.Write(table);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }

        return 0;
    }

    private static long? ParseSpeedLimit(string limit)
    {
        limit = limit.Trim().ToUpperInvariant();
        if (limit.EndsWith("MB") && double.TryParse(limit[..^2], out var mb))
        {
            return (long)(mb * 1024 * 1024);
        }
        if (limit.EndsWith("KB") && double.TryParse(limit[..^2], out var kb))
        {
            return (long)(kb * 1024);
        }
        if (long.TryParse(limit, out var raw))
        {
            return raw;
        }
        return null;
    }
}
