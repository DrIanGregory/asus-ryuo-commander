using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RyuoBrightnessFix.ViewModels;

namespace RyuoBrightnessFix.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _savePlacementTimer;
    private bool _forceClose;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // Debounce placement saves so dragging/resizing doesn't hammer the disk.
        _savePlacementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _savePlacementTimer.Tick += (_, _) => { _savePlacementTimer.Stop(); PersistPlacement(); };

        SourceInitialized += (_, _) => RestorePlacement();
        LocationChanged += (_, _) => QueueSavePlacement();
        SizeChanged += (_, _) => QueueSavePlacement();
        StateChanged += (_, _) => QueueSavePlacement();

        // The preview uses Manual behavior (required so the loop handler may call Play),
        // and with Manual a new Source never opens by itself — kick playback on every
        // binding change. Verified live: without this the preview stays black.
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(MediaElement.SourceProperty, typeof(MediaElement))
            .AddValueChanged(PreviewPlayer, (_, _) =>
            {
                try { if (PreviewPlayer.Source is not null) PreviewPlayer.Play(); }
                catch (Exception ex) { ShowPreviewError(ex); }
            });
    }

    /// <summary>Close the window for real (used by the tray "Exit" / app shutdown).</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Capture the latest geometry before we hide or exit.
        PersistPlacement();

        // X button: hide to tray when configured, otherwise exit the whole app.
        if (!_forceClose && _vm.MinimizeToTrayOnClose && _vm.ShowTrayIcon)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
        if (!_forceClose)
            Application.Current.Shutdown();
    }

    // ---------------------------------------------------------------- placement persistence

    private void RestorePlacement()
    {
        var p = _vm.GetWindowPlacement();
        if (p.Width is double w && p.Height is double h && w > 0 && h > 0)
        {
            Width = w;
            Height = h;

            if (p.Left is double l && p.Top is double t && IsOnScreen(l, t, w, h))
            {
                Left = l;
                Top = t;
            }
            else
            {
                CenterOnScreen();
            }
        }
        else
        {
            CenterOnScreen();
        }

        if (p.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void PersistPlacement()
    {
        // Don't capture a minimized window's (meaningless) bounds.
        if (WindowState == WindowState.Minimized) return;

        MainViewModel.WindowPlacement placement;
        if (WindowState == WindowState.Maximized)
        {
            var r = RestoreBounds; // the "normal" bounds to return to on un-maximize
            placement = new MainViewModel.WindowPlacement(r.Left, r.Top, r.Width, r.Height, true);
        }
        else
        {
            placement = new MainViewModel.WindowPlacement(Left, Top, ActualWidth, ActualHeight, false);
        }

        // A window that was never shown (started minimized to the tray) still has NaN
        // Left/Top, and RestoreBounds can be infinite. JSON cannot store non-finite
        // numbers — saving them makes the ENTIRE settings write throw, losing every
        // setting changed that session. Keep the previously saved placement instead.
        if (!HasFiniteBounds(placement)) return;

        _vm.SaveWindowPlacement(placement);
    }

    private static bool HasFiniteBounds(MainViewModel.WindowPlacement p) =>
        p.Left is double l && double.IsFinite(l) &&
        p.Top is double t && double.IsFinite(t) &&
        p.Width is double w && double.IsFinite(w) && w > 0 &&
        p.Height is double h && double.IsFinite(h) && h > 0;

    private void QueueSavePlacement()
    {
        _savePlacementTimer.Stop();
        _savePlacementTimer.Start();
    }

    /// <summary>True when most of the saved rectangle lands on the current virtual desktop
    /// (guards against a monitor that's since been disconnected).</summary>
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var vsLeft = SystemParameters.VirtualScreenLeft;
        var vsTop = SystemParameters.VirtualScreenTop;
        var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
        var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

        // Require the title bar area to be visible so the window stays draggable.
        double visibleX = Math.Min(left + width, vsRight) - Math.Max(left, vsLeft);
        double visibleTop = top;
        return visibleX > 80 && visibleTop >= vsTop - 1 && visibleTop < vsBottom - 20;
    }

    private void CenterOnScreen()
    {
        Left = SystemParameters.VirtualScreenLeft + (SystemParameters.VirtualScreenWidth - Width) / 2;
        Top = SystemParameters.VirtualScreenTop + (SystemParameters.VirtualScreenHeight - Height) / 2;
    }

    private void LogTextBox_TextChanged(object sender, RoutedEventArgs e)
    {
        // Keep the newest log line in view.
        LogTextBox.ScrollToEnd();
    }

    // ---------------------------------------------------------------- video preview

    private void PreviewPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        // LoadedBehavior is Manual (required so MediaEnded may call Play), so playback
        // must be started explicitly once the media is ready.
        PreviewError.Visibility = Visibility.Collapsed;
        try { PreviewPlayer.Play(); } catch (Exception ex) { ShowPreviewError(ex); }
    }

    private void PreviewPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        // Loop the preview like the panel loops the real video.
        try
        {
            PreviewPlayer.Position = TimeSpan.Zero;
            PreviewPlayer.Play();
        }
        catch (Exception ex) { ShowPreviewError(ex); }
    }

    private void ShowPreviewError(Exception ex)
    {
        PreviewError.Text = "Preview playback error: " + ex.Message;
        PreviewError.Visibility = Visibility.Visible;
    }

    private void PreviewPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        // The source hasn't been transcoded yet, so exotic codecs may not preview —
        // the panel upload can still succeed (ffmpeg handles far more than WPF does).
        PreviewError.Text = "Preview unavailable for this file (" + e.ErrorException.Message +
                            "). The video can still be set — ffmpeg supports more formats than the preview does.";
        PreviewError.Visibility = Visibility.Visible;
    }
}
