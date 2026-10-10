using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clipstems.App.Tray;

public enum TrayState { Idle, Recording, Paused, Problem }

/// <summary>
/// Draws the tray and window icons: three stacked bars (separate tracks)
/// with a state badge. Drawn in code so every state stays consistent and
/// sharp at any size the shell asks for.
/// </summary>
public static class TrayIcons
{
    private static readonly Dictionary<TrayState, BitmapSource> Cache = new();
    private static readonly Dictionary<TrayState, System.Drawing.Icon> TrayCache = new();

    public static BitmapSource For(TrayState state)
    {
        if (Cache.TryGetValue(state, out var cached)) return cached;
        var icon = Render(state, 64);
        Cache[state] = icon;
        return icon;
    }

    /// <summary>
    /// The notification-area icon, drawn at the shell's small-icon size. The tray
    /// takes an HICON, so the bitmap goes in a one-image .ico (PNG inside, which
    /// Windows has read since Vista).
    /// </summary>
    public static System.Drawing.Icon TrayIcon(TrayState state)
    {
        if (TrayCache.TryGetValue(state, out var cached)) return cached;
        int size = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(state, size)));
        using var png = new MemoryStream();
        encoder.Save(png);

        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)0); // reserved
            w.Write((ushort)1); // type: icon
            w.Write((ushort)1); // one image
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); // no palette
            w.Write((byte)0);
            w.Write((ushort)1); // planes
            w.Write((ushort)32); // bits per pixel
            w.Write((uint)png.Length);
            w.Write((uint)22); // data offset: 6-byte header + one 16-byte entry
            w.Write(png.ToArray());
        }
        ico.Position = 0;
        var icon = new System.Drawing.Icon(ico, size, size);
        TrayCache[state] = icon;
        return icon;
    }

    private const int SmCxSmIcon = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static BitmapSource Render(TrayState state, int size)
    {
        double s = size / 32.0;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var tile = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB));
            dc.DrawRoundedRectangle(state == TrayState.Idle ? new SolidColorBrush(Color.FromRgb(0x55, 0x5B, 0x66)) : tile,
                null, new Rect(1 * s, 1 * s, 30 * s, 30 * s), 7 * s, 7 * s);

            var bar = Brushes.White;
            dc.DrawRoundedRectangle(bar, null, new Rect(6 * s, 7 * s, 20 * s, 4 * s), 2 * s, 2 * s);
            dc.DrawRoundedRectangle(bar, null, new Rect(6 * s, 14 * s, 14 * s, 4 * s), 2 * s, 2 * s);
            dc.DrawRoundedRectangle(bar, null, new Rect(6 * s, 21 * s, 9 * s, 4 * s), 2 * s, 2 * s);

            var badgeCenter = new Point(24 * s, 24 * s);
            var ring = new Pen(Brushes.White, 1.5 * s);
            switch (state)
            {
                case TrayState.Recording:
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), ring, badgeCenter, 6 * s, 6 * s);
                    break;
                case TrayState.Paused:
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), ring, badgeCenter, 6.5 * s, 6.5 * s);
                    dc.DrawRectangle(Brushes.White, null, new Rect(21.3 * s, 20.8 * s, 2 * s, 6.4 * s));
                    dc.DrawRectangle(Brushes.White, null, new Rect(24.7 * s, 20.8 * s, 2 * s, 6.4 * s));
                    break;
                case TrayState.Problem:
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)), ring, badgeCenter, 6.5 * s, 6.5 * s);
                    dc.DrawRectangle(Brushes.Black, null, new Rect(23 * s, 20 * s, 2 * s, 5 * s));
                    dc.DrawRectangle(Brushes.Black, null, new Rect(23 * s, 26 * s, 2 * s, 2 * s));
                    break;
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
