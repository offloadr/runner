using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Color = System.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;

namespace Offloadr.Runner.Desktop;

/// <summary>What the dot on the tray icon says at a glance.</summary>
internal enum TrayBadge
{
    None,
    Running,
    Busy,
    Problem,
}

/// <summary>
/// The app icon with a status dot in its lower-right corner, rendered once per badge at the
/// notification area's icon size. Owns the icon handles it creates.
/// </summary>
internal sealed class TrayStateIcons : IDisposable
{
    private readonly Dictionary<TrayBadge, IntPtr> _icons = [];

    public TrayStateIcons(string iconPath, int size)
    {
        using var appIcon = new Icon(iconPath, size, size);
        foreach (var badge in Enum.GetValues<TrayBadge>())
        {
            _icons[badge] = Render(appIcon, badge, size);
        }
    }

    /// <summary>The icon for a badge; zero once disposed (a state change still queued while the app quits).</summary>
    public IntPtr this[TrayBadge badge] => _icons.GetValueOrDefault(badge);

    public static TrayBadge BadgeFor(SupervisorState state) => state switch
    {
        SupervisorState.Running or SupervisorState.RestartPending => TrayBadge.Running,
        SupervisorState.Faulted => TrayBadge.Problem,
        SupervisorState.Stopped => TrayBadge.None,
        _ => TrayBadge.Busy,
    };

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            DestroyIcon(icon);
        }

        _icons.Clear();
    }

    private static IntPtr Render(Icon appIcon, TrayBadge badge, int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawIcon(appIcon, new Rectangle(0, 0, size, size));
            if (badge != TrayBadge.None)
            {
                var diameter = Math.Max(6f, size * 0.5f);
                var dot = new RectangleF(size - diameter, size - diameter, diameter, diameter);
                // A light ring keeps the dot readable on both light and dark taskbars.
                using var ring = new SolidBrush(Color.White);
                graphics.FillEllipse(ring, dot);
                dot.Inflate(-Math.Max(1f, size / 16f), -Math.Max(1f, size / 16f));
                using var fill = new SolidBrush(badge switch
                {
                    TrayBadge.Running => Color.FromArgb(0x16, 0xA3, 0x4A),
                    TrayBadge.Busy => Color.FromArgb(0xF5, 0x9E, 0x0B),
                    _ => Color.FromArgb(0xDC, 0x26, 0x26),
                });
                graphics.FillEllipse(fill, dot);
            }
        }

        return bitmap.GetHicon();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
