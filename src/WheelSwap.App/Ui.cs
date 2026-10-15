using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using WheelSwap.Core;

namespace WheelSwap.App;

/// <summary>
/// Text with letter-spacing (WPF has none): wide-tracked caps in the RM Designs style. Font, size, weight and
/// colour are inherited like a TextBlock's; <see cref="Tracking"/> is extra space after each letter, in em.
/// </summary>
public sealed class Tracked : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(Tracked),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(nameof(Tracking), typeof(double), typeof(Tracked),
        new FrameworkPropertyMetadata(0.14, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpperProperty = DependencyProperty.Register(nameof(Upper), typeof(bool), typeof(Tracked),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(Tracked),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(Tracked),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(Tracked),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Tracked),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }
    public bool Upper { get => (bool)GetValue(UpperProperty); set => SetValue(UpperProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    string Shown => Upper ? (Text ?? "").ToUpperInvariant() : Text ?? "";

    IEnumerable<(FormattedText Glyph, double X)> Layout()
    {
        var tf = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip, x = 0;
        foreach (var ch in Shown)
        {
            var ft = new FormattedText(ch.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, FontSize, Foreground, dip);
            yield return (ft, x);
            x += ft.WidthIncludingTrailingWhitespace + Tracking * FontSize;
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = 0, h = 0;
        foreach (var (g, x) in Layout()) { w = x + g.WidthIncludingTrailingWhitespace; h = Math.Max(h, g.Height); }
        if (h == 0) h = FontSize * 1.2;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var (g, x) in Layout()) dc.DrawText(g, new Point(x, 0));
    }
}

/// <summary>Team colours from the RM Designs team strip (FOM uses the My Team yellow). Keyed by donor id; a 2025
/// car uses its team's colour unless listed (Kick Sauber has its own green).</summary>
public static class TeamColors
{
    static readonly Dictionary<string, (string Fill, bool DarkText)> Map = new()
    {
        ["fom_car"] = ("#FFD966", true),
        ["force_india"] = ("#006C63", false),
        ["ferrari"] = ("#EC2128", false),
        ["gm_cadillac"] = ("#FFFFFF", true),
        ["haas"] = ("#EFEFEF", true),
        ["lotus"] = ("#0185CA", false),
        ["mclaren"] = ("#EB862B", true),
        ["mercedes"] = ("#01B1AA", false),
        ["redbull"] = ("#142CCA", false),
        ["sauber"] = ("#FF4200", false),
        ["toro_rosso"] = ("#1B4ED5", false),
        ["williams"] = ("#0240FF", false),
        ["sauber@2025"] = ("#52E252", true),
    };

    static readonly BrushConverter Bc = new();
    static Brush B(string hex) { var b = (Brush)Bc.ConvertFromString(hex)!; b.Freeze(); return b; }

    static (string Fill, bool DarkText)? Find(string id) =>
        Map.TryGetValue(id, out var t) ? t : Map.TryGetValue(GameData.ParseDonor(id).Token, out t) ? t : null;

    public static Brush Fill(string id) => B(Find(id)?.Fill ?? "#4300AD");
    public static Brush Text(string id) => B(Find(id)?.DarkText == true ? "#111111" : "#F9F9F9");
}

/// <summary>Team token -> its fill (parameter "text": its text colour).</summary>
public sealed class TeamBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string tok ? (parameter as string == "text" ? TeamColors.Text(tok) : TeamColors.Fill(tok)) : Brushes.Transparent;
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
