using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NotIdle.Core;

namespace NotIdle.App;

/// <summary>
/// The handful of colours and fonts the UI uses. Centralised so the dashboard and
/// the tray icon agree on what each state looks like.
/// </summary>
internal static class Theme
{
    public static readonly Color Canvas = Color.FromArgb(0xF4, 0xF5, 0xF7);
    public static readonly Color Surface = Color.White;
    public static readonly Color Border = Color.FromArgb(0xE3, 0xE6, 0xEA);
    public static readonly Color Ink = Color.FromArgb(0x1B, 0x1F, 0x24);
    public static readonly Color Muted = Color.FromArgb(0x6B, 0x72, 0x80);
    public static readonly Color Accent = Color.FromArgb(0x2D, 0x6C, 0xDF);

    public static readonly Font Body = new("Segoe UI", 9F);
    public static readonly Font BodyStrong = new("Segoe UI", 9F, FontStyle.Bold);
    public static readonly Font Pill = new("Segoe UI Semibold", 10F);

    /// <summary>The colour that stands for a given state, everywhere in the app.</summary>
    public static Color ColorFor(ActivityState state) => state switch
    {
        ActivityState.Active => Color.FromArgb(0x2E, 0xA0, 0x53),     // green  — user is here
        ActivityState.Idle => Color.FromArgb(0xD8, 0x99, 0x14),        // amber  — counting down
        ActivityState.KeepAwake => Color.FromArgb(0x2D, 0x6C, 0xDF),   // blue   — holding awake
        ActivityState.Suspended => Color.FromArgb(0x8B, 0x5C, 0xF6),   // violet — standing down
        ActivityState.Locked => Color.FromArgb(0x4B, 0x5D, 0x73),      // slate  — session locked
        _ => Color.FromArgb(0x8A, 0x92, 0x9B),                          // grey   — off
    };

    /// <summary>Rounded-rectangle path, used for pills and card outlines.</summary>
    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d - 1, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d - 1, bounds.Bottom - d - 1, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d - 1, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>A rounded, solid-filled status badge. Purely presentational.</summary>
internal sealed class StatePill : Control
{
    private Color _pillColor = Theme.Muted;

    public StatePill()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        ForeColor = Color.White;
        Font = Theme.Pill;
        Height = 30;
    }

    /// <summary>Fill colour of the badge. <see cref="Control.BackColor"/> paints behind its corners.</summary>
    /// <remarks>Set from code only; this control never appears in the designer.</remarks>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PillColor
    {
        get => _pillColor;
        set
        {
            if (_pillColor == value) return;
            _pillColor = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // The badge is rounded, so the corners must show the surface behind it.
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = Theme.RoundedRect(bounds, Height / 2))
        using (var fill = new SolidBrush(_pillColor))
        {
            e.Graphics.FillPath(fill, path);
        }

        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
