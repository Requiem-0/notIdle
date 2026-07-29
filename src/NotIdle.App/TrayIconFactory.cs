using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using NotIdle.Core;

namespace NotIdle.App;

/// <summary>
/// Builds tray icons from the embedded app photo: the image clipped to a circle,
/// with a small state-colored status dot in the corner so the current mode stays
/// glanceable. Icons are cached per state; GDI handles are released on dispose.
/// </summary>
internal sealed class TrayIconFactory : IDisposable
{
    private readonly Dictionary<AppState, Icon> _cache = new();
    private readonly Bitmap? _photo = LoadEmbeddedPhoto();

    public Icon For(AppState state)
    {
        if (_cache.TryGetValue(state, out Icon? cached)) return cached;
        Icon icon = _photo is null ? BuildFallback(ColorFor(state)) : BuildFromPhoto(state);
        _cache[state] = icon;
        return icon;
    }

    /// <summary>The multi-resolution app icon (exe/taskbar/window). Caller owns it.</summary>
    public static Icon LoadAppIcon()
    {
        Assembly asm = typeof(TrayIconFactory).Assembly;
        using Stream? s = asm.GetManifestResourceStream("NotIdle.App.Resources.notidle.ico");
        return s is not null ? new Icon(s) : SystemIcons.Application;
    }

    private static Bitmap? LoadEmbeddedPhoto()
    {
        try
        {
            Assembly asm = typeof(TrayIconFactory).Assembly;
            using Stream? s = asm.GetManifestResourceStream("NotIdle.App.Resources.app.png");
            return s is null ? null : new Bitmap(s);
        }
        catch
        {
            return null; // Fall back to a plain colored dot if the asset is missing.
        }
    }

    private static Color ColorFor(AppState state) => state switch
    {
        AppState.Monitoring => Color.FromArgb(0x2E, 0xCC, 0x71),   // green  — watching
        AppState.Idle => Color.FromArgb(0xF1, 0xC4, 0x0F),          // amber  — counting down
        AppState.ActionRunning => Color.FromArgb(0x34, 0x98, 0xDB), // blue   — acting
        _ => Color.FromArgb(0x95, 0xA5, 0xA6),                       // gray   — paused
    };

    private Icon BuildFromPhoto(AppState state)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Clip the photo to a circle.
            using (var clip = new GraphicsPath())
            {
                clip.AddEllipse(0, 0, size, size);
                g.SetClip(clip);
                g.DrawImage(_photo!, 0, 0, size, size);
                g.ResetClip();
            }

            // State dot with a contrasting ring, bottom-right.
            const int d = 13;
            var dot = new Rectangle(size - d - 1, size - d - 1, d, d);
            using var ring = new SolidBrush(Color.White);
            g.FillEllipse(ring, dot.X - 2, dot.Y - 2, d + 4, d + 4);
            using var fill = new SolidBrush(ColorFor(state));
            g.FillEllipse(fill, dot);
        }
        return ToIcon(bmp);
    }

    private static Icon BuildFallback(Color fill)
    {
        using var bmp = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, 2, 2, 12, 12);
        }
        return ToIcon(bmp);
    }

    private static Icon ToIcon(Bitmap bmp)
    {
        nint hicon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hicon);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hicon);
        }
    }

    public void Dispose()
    {
        foreach (Icon icon in _cache.Values) icon.Dispose();
        _cache.Clear();
        _photo?.Dispose();
    }
}
