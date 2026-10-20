using System.Windows.Media;

namespace GameWake.App.Themes;

/// <summary>Colours shared by XAML (via x:Static), view models and the GDI tray icon. Matched to SonaFX.</summary>
public static class Palette
{
    public static readonly Color Background = Color.FromRgb(0x12, 0x13, 0x16);
    public static readonly Color Rail = Color.FromRgb(0x06, 0x06, 0x07);
    public static readonly Color Card = Color.FromRgb(0x12, 0x13, 0x16);
    public static readonly Color CardHover = Color.FromRgb(0x18, 0x19, 0x1C);
    public static readonly Color Inset = Color.FromRgb(0x06, 0x06, 0x07);
    public static readonly Color Border = Color.FromArgb(0x26, 0xF2, 0xF1, 0xEE);
    public static readonly Color BorderStrong = Color.FromArgb(0x66, 0xF2, 0xF1, 0xEE);
    public static readonly Color TextPrimary = Color.FromRgb(0xF2, 0xF1, 0xEE);
    public static readonly Color TextSecondary = Color.FromArgb(0x99, 0xF2, 0xF1, 0xEE);
    public static readonly Color TextTertiary = Color.FromArgb(0x61, 0xF2, 0xF1, 0xEE);
    public static readonly Color Invert = Color.FromRgb(0xFF, 0xFF, 0xFF);
    public static readonly Color InvertInk = Color.FromRgb(0x0B, 0x0B, 0x0C);

    public static readonly Color Purple = Color.FromRgb(0x2D, 0x81, 0xFF);
    public static readonly Color PurpleLight = Color.FromRgb(0x74, 0xAA, 0xFF);
    public static readonly Color Blue = Color.FromRgb(0x74, 0xAA, 0xFF);
    public static readonly Color Amber = Color.FromRgb(0xE8, 0xC5, 0x47);
    public static readonly Color Green = Color.FromRgb(0x3F, 0xD6, 0xB8);
    public static readonly Color Grey = Color.FromArgb(0x99, 0xF2, 0xF1, 0xEE);
    public static readonly Color Red = Color.FromRgb(0xFF, 0x62, 0x59);

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Tint(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}

public enum Tone
{
    Neutral,
    Active,
    Working,
    Warning,
    Success,
}

public static class ToneBrushes
{
    private static readonly Dictionary<Tone, (SolidColorBrush Fore, SolidColorBrush Back)> Map = new()
    {
        [Tone.Neutral] = (Palette.Brush(Palette.Grey), Palette.Tint(Palette.TextPrimary, 0x14)),
        [Tone.Active] = (Palette.Brush(Palette.PurpleLight), Palette.Tint(Palette.Purple, 0x33)),
        [Tone.Working] = (Palette.Brush(Palette.Blue), Palette.Tint(Palette.Blue, 0x2E)),
        [Tone.Warning] = (Palette.Brush(Palette.Amber), Palette.Tint(Palette.Amber, 0x2B)),
        [Tone.Success] = (Palette.Brush(Palette.Green), Palette.Tint(Palette.Green, 0x26)),
    };

    public static SolidColorBrush Foreground(Tone tone) => Map[tone].Fore;

    public static SolidColorBrush Background(Tone tone) => Map[tone].Back;
}
