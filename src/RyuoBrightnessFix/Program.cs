using System.Text.Json;
using RyuoBrightnessFix.Models;
using RyuoBrightnessFix.Services;
using RyuoBrightnessFix.Util;
using Serilog;
using Serilog.Events;

namespace RyuoBrightnessFix;

internal static class Program
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Console/headless entry point. Invoked by <see cref="App"/> when the first argument
    /// is a recognized verb (see <see cref="CliCommands"/>); otherwise the WPF GUI runs.
    /// </summary>
    public static int RunCli(string[] rawArgs)
    {
        var args = new ArgParser(rawArgs);

        // --- Serilog bootstrap (console + rolling file) ---
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        var level = args.HasFlag("verbose") ? LogEventLevel.Debug : LogEventLevel.Information;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(Path.Combine(logDir, "ryuo-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var log = Log.Logger;

        try
        {
            var command = CliCommands.TryParse(args.Command);
            if (command is null)
                return UnknownCommand(args.Command ?? "(none)");

            return command switch
            {
                CliCommand.ListDevices => ListDevices(args, log),
                CliCommand.TestRead => TestRead(args, log),
                CliCommand.SendCommand => SendCommand(args, log),
                CliCommand.SendSequence => SendSequence(args, log),
                CliCommand.SetBrightness100 => SetBrightness100(args, log),
                CliCommand.MonitorResume => MonitorResume(args, log),
                CliCommand.InstallTask => InstallTask(args, log),
                CliCommand.UninstallTask => UninstallTask(args, log),
                CliCommand.ParseHex => ParseHexCmd(args, log),
                CliCommand.Help => PrintHelp(),
                _ => UnknownCommand(args.Command ?? "(none)"),
            };
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Unhandled error running command '{Command}'.", args.Command);
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // ---------------------------------------------------------------- list-devices

    private static int ListDevices(ArgParser args, ILogger log)
    {
        var discovery = new DeviceDiscoveryService(log);

        var filter = new DeviceFilter
        {
            VendorId = args.Get("vid"),
            ProductId = args.Get("pid"),
            PathContains = args.Get("path-contains"),
        };
        bool hasFilter = filter.VendorIdValue is not null
                         || filter.ProductIdValue is not null
                         || filter.PathContains is not null;
        bool asusOnly = args.HasFlag("asus-only");

        var all = discovery.Enumerate();

        // Collapse to one entry per physical device for display.
        var perDevice = all
            .GroupBy(x => x.Info.DevicePath)
            .Select(g => g.First().Info)
            .ToList();

        var shown = perDevice.AsEnumerable();
        if (hasFilter)
            shown = shown.Where(i => MatchesDisplay(i, filter));
        if (asusOnly)
            shown = shown.Where(i => i.LikelyAsus);

        var shownList = shown.ToList();

        Console.WriteLine();
        Console.WriteLine($"Found {shownList.Count} HID device(s)"
                          + (hasFilter || asusOnly ? " (filtered)" : "") + ":");
        Console.WriteLine();

        int idx = 0;
        foreach (var info in shownList.OrderByDescending(i => i.LikelyAsus))
        {
            Console.WriteLine($"[{idx++}] {info.ProductName ?? "(unnamed)"}");
            Console.WriteLine(info.ToConsoleBlock());
            Console.WriteLine();
        }

        if (shownList.Count == 0)
            log.Warning("No devices matched. Try without filters, or check the ASUS device is connected.");

        // Persist a raw dump if requested.
        var jsonPath = args.Get("json");
        if (jsonPath is not null)
        {
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(shownList, PrettyJson));
            log.Information("Wrote {Count} device record(s) to {Path}", shownList.Count, jsonPath);
        }

        // Scaffold a starter config from a single best candidate, if requested.
        var scaffoldPath = args.Get("save-config");
        if (scaffoldPath is not null)
            ScaffoldConfig(scaffoldPath, shownList, log);

        return 0;
    }

    private static bool MatchesDisplay(DeviceInfo i, DeviceFilter f)
    {
        if (f.VendorIdValue is int vid && i.VendorId != vid) return false;
        if (f.ProductIdValue is int pid && i.ProductId != pid) return false;
        if (!string.IsNullOrWhiteSpace(f.PathContains) &&
            (i.DevicePath?.Contains(f.PathContains, StringComparison.OrdinalIgnoreCase) != true))
            return false;
        return true;
    }

    private static void ScaffoldConfig(string path, List<DeviceInfo> candidates, ILogger log)
    {
        var pick = candidates.FirstOrDefault(c => c.LikelyAsus) ?? candidates.FirstOrDefault();
        if (pick is null)
        {
            log.Warning("No candidate device to scaffold a config from.");
            return;
        }

        var cfg = new RyuoConfig
        {
            Device = new DeviceFilter
            {
                VendorId = pick.VendorIdHex,
                ProductId = pick.ProductIdHex,
                UsagePage = pick.UsagePage,
                Usage = pick.Usage,
                PathContains = null,
                SerialNumber = pick.SerialNumber,
            },
            Brightness100Sequence = new List<HidCommand>
            {
                new()
                {
                    Name = "Set LCD brightness 100 (REPLACE hex with captured bytes)",
                    ReportType = ReportType.Output,
                    ReportId = 0,
                    Hex = "00 00 00 00 00 00 00 00",
                    DelayMs = 250,
                },
            },
            ResumeDelayMs = 10_000,
            DryRun = true,
        };

        cfg.Save(path);
        log.Information("Scaffolded starter config at {Path} from device {Product} ({Vid}/{Pid}).",
            path, pick.ProductName, pick.VendorIdHex, pick.ProductIdHex);
        log.Warning("The hex payload is a PLACEHOLDER. Capture the real brightness command (README Step B) before executing.");
    }

    // ---------------------------------------------------------------- test-read

    private static int TestRead(ArgParser args, ILogger log)
    {
        var config = LoadConfigOrFail(args, log, out _);
        if (config is null) return 2;

        int seconds = args.GetInt("seconds") ?? 8;
        var discovery = new DeviceDiscoveryService(log);

        var (info, device) = discovery.ResolveSingle(config.Device);
        log.Information("Reading input reports from {Product} ({Path}) for {Seconds}s. " +
                        "Toggle the setting in Armoury Crate now to see what the device emits.",
            info.ProductName, info.DevicePath, seconds);

        using var session = new HidDeviceSession(device, info, log);
        try
        {
            session.Open();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Could not open the device for reading. It may be held exclusively by ASUS software.");
            return 1;
        }

        var reports = session.ReadInputReports(TimeSpan.FromSeconds(seconds));
        log.Information("Captured {Count} input report(s).", reports.Count);
        return 0;
    }

    // ------------------------------------------------ send-command / send-sequence / set-brightness-100

    private static int SendCommand(ArgParser args, ILogger log)
    {
        var config = LoadConfigOrFail(args, log, out _);
        if (config is null) return 2;

        var name = args.Get("name");
        if (name is null)
        {
            log.Error("send-command requires --name \"<command name>\".");
            return 2;
        }

        bool dryRun = ResolveDryRun(args, config);
        WarnIfWriting(dryRun, log);

        var fixer = new BrightnessFixer(new DeviceDiscoveryService(log), log);
        return fixer.SendSequence(config, dryRun, onlyName: name) ? 0 : 1;
    }

    private static int SendSequence(ArgParser args, ILogger log)
    {
        var config = LoadConfigOrFail(args, log, out _);
        if (config is null) return 2;

        bool dryRun = ResolveDryRun(args, config);
        WarnIfWriting(dryRun, log);

        var fixer = new BrightnessFixer(new DeviceDiscoveryService(log), log);
        return fixer.SendSequence(config, dryRun) ? 0 : 1;
    }

    private static int SetBrightness100(ArgParser args, ILogger log)
        // Semantically identical to send-sequence; named for the scheduled-task action and clarity.
        => SendSequence(args, log);

    // ---------------------------------------------------------------- monitor-resume

    private static int MonitorResume(ArgParser args, ILogger log)
    {
        var config = LoadConfigOrFail(args, log, out _);
        if (config is null) return 2;

        bool dryRun = ResolveDryRun(args, config);
        WarnIfWriting(dryRun, log);

        var fixer = new BrightnessFixer(new DeviceDiscoveryService(log), log);
        using var monitor = new ResumeMonitor(
            config.ResumeDelayMs,
            ct => fixer.SendSequence(config, dryRun, ct: ct),
            log);
        monitor.Start();
        log.Information("Listening for system resume. Press Ctrl+C to stop.");

        using var done = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // graceful
            log.Information("Ctrl+C received; stopping resume monitor.");
            done.Set();
        };

        done.Wait();
        return 0;
    }

    // ---------------------------------------------------------------- install-task / uninstall-task

    private static int InstallTask(ArgParser args, ILogger log)
    {
        var config = LoadConfigOrFail(args, log, out var configPath);
        if (config is null) return 2;

        var installer = new ScheduledTaskInstaller(log);
        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RyuoBrightnessFix.exe");
        var taskName = args.Get("task-name") ?? ScheduledTaskInstaller.DefaultTaskName;

        return installer.Install(exePath, configPath!, taskName, config.ResumeDelayMs) ? 0 : 1;
    }

    private static int UninstallTask(ArgParser args, ILogger log)
    {
        var installer = new ScheduledTaskInstaller(log);
        var taskName = args.Get("task-name") ?? ScheduledTaskInstaller.DefaultTaskName;
        return installer.Uninstall(taskName) ? 0 : 1;
    }

    // ---------------------------------------------------------------- parse-hex

    private static int ParseHexCmd(ArgParser args, ILogger log)
    {
        // Accept either positional text or --hex "...".
        var raw = args.Get("hex") ?? args.Positional;
        if (string.IsNullOrWhiteSpace(raw))
        {
            log.Error("Provide hex bytes: parse-hex \"00 AA BB\"  (or --hex \"00AABB\").");
            return 2;
        }

        try
        {
            var bytes = HexUtil.ParseHex(raw);
            Console.WriteLine();
            Console.WriteLine($"Parsed {bytes.Length} byte(s):");
            Console.WriteLine($"  Hex   : {HexUtil.ToHex(bytes)}");
            Console.WriteLine($"  First : 0x{(bytes.Length > 0 ? bytes[0] : 0):X2} (would be the report ID)");
            Console.WriteLine($"  Dec   : {string.Join(' ', bytes.Select(b => b.ToString()))}");
            return 0;
        }
        catch (FormatException ex)
        {
            log.Error("Invalid hex: {Message}", ex.Message);
            return 2;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static RyuoConfig? LoadConfigOrFail(ArgParser args, ILogger log, out string? path)
    {
        path = args.Get("config");
        if (path is null)
        {
            log.Error("This command requires --config <path-to-ryuo.json>.");
            return null;
        }
        try
        {
            var cfg = RyuoConfig.Load(path);
            log.Debug("Loaded config from {Path}", path);
            return cfg;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load config from {Path}", path);
            return null;
        }
    }

    /// <summary>--execute => false, --dry-run => true, otherwise the config's DryRun.</summary>
    private static bool ResolveDryRun(ArgParser args, RyuoConfig config)
    {
        if (args.HasFlag("execute")) return false;
        if (args.HasFlag("dry-run")) return true;
        return config.DryRun;
    }

    private static void WarnIfWriting(bool dryRun, ILogger log)
    {
        if (dryRun)
        {
            log.Information("DRY-RUN: no bytes will be written to the device. Pass --execute to actually send.");
            return;
        }

        if (OperatingSystem.IsWindows() && !AdminUtil.IsElevated())
            log.Debug("Not elevated. HID writes usually work unelevated, but some ASUS handles may require elevation.");

        log.Warning("EXECUTE MODE: real HID reports WILL be written to the device.");
        log.Warning("Wrong HID commands can confuse the device until you reboot or replug it. " +
                    "Only proceed with bytes you captured from Armoury Crate.");
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: '{command}'.");
        PrintHelp();
        return 2;
    }

    private static int PrintHelp()
    {
        Console.WriteLine(
"""
RyuoBrightnessFix — ASUS ROG Ryuo IV AIO LCD brightness investigator/fixer

USAGE:
  RyuoBrightnessFix <command> [options]

COMMANDS:
  list-devices            Enumerate HID devices. ASUS/ROG devices are highlighted.
      --vid 0x0B05          Filter by vendor ID (hex or decimal)
      --pid 0x1234          Filter by product ID
      --path-contains RYUO  Filter by device-path substring
      --asus-only           Show only likely-ASUS devices
      --json out.json       Dump matched devices to JSON
      --save-config ryuo.json   Scaffold a starter config from the best candidate

  test-read --config ryuo.json [--seconds 8]
                          Open the configured device and print any input reports
                          (toggle the setting in Armoury Crate while this runs).

  parse-hex "00 AA BB"    Parse/normalise a hex string (sanity-check captured bytes).

  send-command --config ryuo.json --name "NAME" [--dry-run | --execute]
                          Send a single named command from brightness100Sequence.

  send-sequence --config ryuo.json [--dry-run | --execute]
                          Send the whole brightness100Sequence.

  set-brightness-100 --config ryuo.json [--dry-run | --execute]
                          Alias of send-sequence; used by the scheduled task.

  monitor-resume --config ryuo.json [--dry-run | --execute]
                          Stay running; reapply the sequence whenever Windows resumes.

  install-task --config ryuo.json [--task-name NAME]   (requires Administrator)
                          Create a Scheduled Task that runs set-brightness-100 on resume
                          (System log, Power-Troubleshooter, Event ID 1).

  uninstall-task [--task-name NAME]                    (requires Administrator)
                          Remove the scheduled task.

GLOBAL OPTIONS:
  --verbose               Debug-level logging.

SAFETY:
  * Default is DRY-RUN. Nothing is written unless you pass --execute.
  * Validate the captured bytes first; wrong HID reports can confuse the device
    until reboot/replug.

See README.md for the full capture-and-replay workflow (USBPcap + Wireshark).
""");
        return 0;
    }
}
