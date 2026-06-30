using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;
using RyuoBrightnessFix.Models;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// System-tray presence via a WinForms <see cref="NotifyIcon"/> (the reliable way to
/// get a tray icon from a WPF app). Exposes events the window/app subscribe to.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public event EventHandler? OpenRequested;
    public event EventHandler? RestoreBrightnessRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = IconFactory.CreateTrayIcon(),
            Text = AppConstants.DisplayName,
            Visible = false,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Restore brightness now", null, (_, _) => RestoreBrightnessRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _notifyIcon.ContextMenuStrip = menu;

        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (!_notifyIcon.Visible) return;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }
}

/// <summary>
/// Supplies the system-tray icon. Prefers the checked-in <c>app.ico</c> (the cyan-dragon
/// emblem — the same icon used for the exe/taskbar/window, multi-res down to 16px) embedded
/// as a WPF resource; falls back to a simple icon drawn at runtime if it can't be loaded.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>The dragon emblem from the embedded <c>app.ico</c>, or the runtime fallback.</summary>
    public static Icon CreateTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/Icons/app.ico", UriKind.Absolute);
            var resource = System.Windows.Application.GetResourceStream(uri);
            if (resource is not null)
            {
                using var stream = resource.Stream;
                // NotifyIcon shows 16px in the tray; ask the .ico for its 16x16 frame.
                return new Icon(stream, new Size(16, 16));
            }
        }
        catch
        {
            // Resource missing or unreadable — fall through to the drawn fallback below.
        }

        return CreateAppIcon();
    }

    public static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(45, 127, 249)); // accent blue
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var font = new Font(FontFamily.GenericSansSerif, 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.White);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("R", font, fg, new RectangleF(0, 0, 32, 32), sf);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            // Clone so the managed Icon owns its own copy; then free the temporary HICON.
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
