using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using GameWake.App.Themes;

namespace GameWake.App.Services;

public enum TrayState
{
    Idle,
    Paused,
    Downloading,
    Installing,
    Issue,
}

/// <summary>
/// Draws the tray icon at the exact size Windows asks for. The glyph matches assets/GameWake.ico
/// (tools/New-AppIcon.ps1) on a 256-unit grid; only the colour (and the glyph for issues/pause) changes.
/// </summary>
public static class TrayIconFactory
{
    public static Icon Create(TrayState state, int size)
    {
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 256f, size / 256f);

            using (var background = RoundedRect(8, 8, 240, 240, 56))
            using (var fill = new SolidBrush(ColorFor(state)))
            {
                g.FillPath(fill, background);
            }

            using var white = new SolidBrush(Color.White);
            switch (state)
            {
                case TrayState.Issue:
                    using (var bar = RoundedRect(110, 44, 36, 118, 14))
                    {
                        g.FillPath(white, bar);
                    }

                    g.FillEllipse(white, 106, 178, 44, 44);
                    break;
                case TrayState.Paused:
                    using (var left = RoundedRect(76, 60, 38, 136, 10))
                    using (var right = RoundedRect(142, 60, 38, 136, 10))
                    {
                        g.FillPath(white, left);
                        g.FillPath(white, right);
                    }

                    break;
                default:
                    using (var shaft = RoundedRect(110, 48, 36, 92, 10))
                    {
                        g.FillPath(white, shaft);
                    }

                    g.FillPolygon(white, [new PointF(66, 122), new PointF(190, 122), new PointF(128, 186)]);
                    using (var baseBar = RoundedRect(66, 200, 124, 24, 12))
                    {
                        g.FillPath(white, baseBar);
                    }

                    break;
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    // Slightly deeper than the UI accents so the white glyph stays legible at 16 px.
    private static Color ColorFor(TrayState state) => state switch
    {
        TrayState.Downloading => Color.FromArgb(Palette.Purple.R, Palette.Purple.G, Palette.Purple.B),
        TrayState.Installing => Color.FromArgb(Palette.PurpleLight.R, Palette.PurpleLight.G, Palette.PurpleLight.B),
        TrayState.Issue => Color.FromArgb(Palette.Amber.R, Palette.Amber.G, Palette.Amber.B),
        _ => Color.FromArgb(0x6B, 0x6B, 0x74),
    };

    private static GraphicsPath RoundedRect(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
