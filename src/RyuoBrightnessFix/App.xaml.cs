using System.Windows;
using RyuoBrightnessFix.Models;
using RyuoBrightnessFix.Services;
using RyuoBrightnessFix.ViewModels;
using RyuoBrightnessFix.Views;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RyuoBrightnessFix;

/// <summary>Application entry point — a single-instance WPF tray app.</summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private UiLogSink? _uiSink;
    private LoggingLevelSwitch? _levelSwitch;
    private MainViewModel? _viewModel;
    private TrayIconService? _tray;
    private MainWindow? _window;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Capture any unhandled GUI exception so a crash leaves a diagnosable trail.
        DispatcherUnhandledException += (_, ev) =>
        {
            LogCrash(ev.Exception);
            ev.Handled = true;
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => LogCrash(ev.ExceptionObject as Exception);

        // --- Single instance for the GUI ---
        _singleInstanceMutex = new Mutex(initiallyOwned: true, AppConstants.SingleInstanceMutex, out bool createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show($"{AppConstants.DisplayName} is already running (check the system tray).",
                AppConstants.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            StartGui();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to start: " + ex.Message, AppConstants.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartGui()
    {
        // Logger: rolling file + the in-app log pane sink.
        var logDir = AppConstants.LogDir;
        Directory.CreateDirectory(logDir);
        _uiSink = new UiLogSink();

        var settings = AppSettings.Load();

        // Runtime-switchable level so the Debug-logging checkbox can crank detail up/down
        // without restarting the app. Verbose when debugging, Information otherwise.
        _levelSwitch = new LoggingLevelSwitch(
            settings.DebugLogging ? LogEventLevel.Verbose : LogEventLevel.Information);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_levelSwitch)
            .WriteTo.File(Path.Combine(logDir, "ryuo-.log"), rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .WriteTo.UiSink(_uiSink)
            .CreateLogger();

        Log.Information("===== {App} starting. Debug logging = {Debug}. Log folder: {Dir} =====",
            AppConstants.DisplayName, settings.DebugLogging, logDir);

        var startup = new StartupRegistrationService(Log.Logger);

        // Keep the saved "start with windows" flag honest with the registry's actual state.
        if (settings.StartWithWindows != startup.IsRegistered())
        {
            startup.Set(settings.StartWithWindows);
        }

        _viewModel = new MainViewModel(Log.Logger, _uiSink, settings, startup, _levelSwitch);

        // Tray icon (WinForms NotifyIcon).
        _tray = new TrayIconService { Visible = settings.ShowTrayIcon };
        _tray.OpenRequested += (_, _) => Dispatcher.Invoke(ShowWindow);
        _tray.RestoreBrightnessRequested += (_, _) => _viewModel.RestoreToTarget();
        _tray.ExitRequested += (_, _) => Dispatcher.Invoke(ExitApplication);

        _viewModel.TrayVisibilityRequested += visible =>
        {
            if (_tray is not null) _tray.Visible = visible;
            // If the tray is being hidden, make sure the window is reachable.
            if (!visible && _window is { IsVisible: false }) Dispatcher.Invoke(ShowWindow);
        };

        _window = new MainWindow(_viewModel);

        // Honour "start minimized": only stay hidden if there's a tray icon to restore from.
        bool startHidden = settings.StartMinimized && settings.ShowTrayIcon;
        if (startHidden)
        {
            _tray?.ShowBalloon(AppConstants.DisplayName, "Running in the tray. Double-click to open.");
            Log.Information("Started minimized to tray.");
        }
        else
        {
            ShowWindow();
            if (settings.StartMinimized) _window.WindowState = WindowState.Minimized;
        }
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        // Only un-minimize — never force Normal, or we'd clobber a restored Maximized window.
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void ExitApplication()
    {
        _window?.ForceClose();
        Shutdown();
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(AppConstants.AppDataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.txt"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* last-resort logging; never throw from here */ }

        try { Log.Error(ex, "Unhandled GUI exception."); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _tray?.Dispose();
        Log.CloseAndFlush();

        // Only the owning instance may release the mutex (and only from the thread that
        // acquired it — which is this one). A second instance never owned it.
        if (_ownsMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { /* not owned / already released — ignore */ }
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
