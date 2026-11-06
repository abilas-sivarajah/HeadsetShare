using System.Windows;
using System.Windows.Media;

namespace HeadsetShare;

public enum DropZone
{
    None,
    Source,
    Stage,
    Tray,
}

/// <summary>Zeichnet den Hörkreis: Quelle in der Mitte, Wellen zu jedem Gerät, Ringe im Takt des Pegels.</summary>
public sealed class StageView : FrameworkElement
{
    public const double OrbRadius = 76;

    private const int WavePoints = 48;

    private static readonly Color Coral = Color.FromRgb(0xFF, 0x7A, 0x59);
    private static readonly Color Amber = Color.FromRgb(0xFF, 0xC8, 0x6B);
    private static readonly Color Teal = Color.FromRgb(0x38, 0xD6, 0xB5);
    private static readonly Color Slate = Color.FromRgb(0x5A, 0x62, 0x75);

    private readonly List<(double Radius, double Strength)> _ripples = [];
    private double _phase;
    private double _sinceRipple;
    private double _breath;

    public IReadOnlyList<TargetCard> Cards { get; set; } = [];

    public double CardWidth { get; set; }

    public double CardHeight { get; set; }

    public double SourceLevel { get; set; }

    public bool Live { get; set; }

    public bool Dragging { get; set; }

    public DropZone HoverZone { get; set; }

    public Point Center => new(ActualWidth / 2, ActualHeight / 2);

    public void Advance(double seconds)
    {
        _phase += seconds * (Live ? 5.2 : 1.0);
        _breath += seconds;
        _sinceRipple += seconds;

        if (Live && SourceLevel > 0.04 && _sinceRipple > 0.42)
        {
            _ripples.Add((OrbRadius, Math.Min(1, 0.3 + SourceLevel)));
            _sinceRipple = 0;
        }

        var maximum = Math.Min(ActualWidth, ActualHeight) * 0.62;
        for (var i = _ripples.Count - 1; i >= 0; i--)
        {
            var (radius, strength) = _ripples[i];
            radius += seconds * 92;
            if (radius > maximum)
            {
                _ripples.RemoveAt(i);
            }
            else
            {
                _ripples[i] = (radius, strength);
            }
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        // Sonst greift der Mausklick auf der leeren Fläche ins Leere.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var center = Center;
        DrawGuideRings(context, center);
        DrawRipples(context, center);
        foreach (var card in Cards)
        {
            DrawWave(context, center, card);
        }

        DrawOrb(context, center);
    }

    private void DrawGuideRings(DrawingContext context, Point center)
    {
        var stageHot = Dragging && HoverZone == DropZone.Stage;
        var alpha = stageHot ? 150 : Dragging ? 70 : 26;
        var color = Dragging ? Teal : Slate;
        var pen = Frozen(new Pen(Frozen(new SolidColorBrush(WithAlpha(color, alpha))), stageHot ? 2 : 1)
        {
            DashStyle = new DashStyle([3, 5], 0),
        });

        var limit = Math.Min(ActualWidth, ActualHeight) / 2 - 18;
        foreach (var factor in new[] { 0.5, 0.75, 1.0 })
        {
            var radius = limit * factor;
            if (radius > OrbRadius + 12)
            {
                context.DrawEllipse(null, pen, center, radius, radius);
            }
        }
    }

    private void DrawRipples(DrawingContext context, Point center)
    {
        var maximum = Math.Min(ActualWidth, ActualHeight) * 0.62;
        foreach (var (radius, strength) in _ripples)
        {
            var fade = 1 - ((radius - OrbRadius) / (maximum - OrbRadius));
            var alpha = (byte)(90 * fade * fade * strength);
            var pen = new Pen(new SolidColorBrush(WithAlpha(Coral, alpha)), 1.6);
            context.DrawEllipse(null, pen, center, radius, radius);
        }
    }

    private void DrawWave(DrawingContext context, Point center, TargetCard card)
    {
        var end = new Point(
            card.X + (CardWidth * card.Scale / 2),
            card.Y + (CardHeight * card.Scale / 2));
        var vector = end - center;
        var length = vector.Length;
        if (length < OrbRadius + 4)
        {
            return;
        }

        var unit = vector / length;
        var normal = new Vector(-unit.Y, unit.X);
        var start = center + (unit * (OrbRadius - 2));
        var span = length - OrbRadius + 2;
        var connected = card.State is TargetState.Synced or TargetState.Warning && Live;
        var fromColor = connected ? Coral : Slate;
        var toColor = connected ? Teal : Slate;

        // Ruhige Grundlinie, auch ohne Ton.
        var basePen = new Pen(new LinearGradientBrush(
            WithAlpha(fromColor, 60), WithAlpha(toColor, 60), start, end)
        { MappingMode = BrushMappingMode.Absolute }, 1.2);
        if (!connected)
        {
            basePen.DashStyle = new DashStyle([2, 4], 0);
        }

        context.DrawLine(basePen, start, end);
        if (!connected)
        {
            return;
        }

        var loudness = Math.Clamp(card.Level, 0, 1);
        var amplitude = 2 + (loudness * 16);
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            for (var i = 0; i <= WavePoints; i++)
            {
                var progress = (double)i / WavePoints;
                var envelope = Math.Sin(progress * Math.PI);
                var offset = Math.Sin((progress * 15) - _phase) * amplitude * envelope;
                var point = start + (unit * (span * progress)) + (normal * offset);
                if (i == 0)
                {
                    stream.BeginFigure(point, false, false);
                }
                else
                {
                    stream.LineTo(point, true, false);
                }
            }
        }

        geometry.Freeze();
        var alpha = (byte)(120 + (135 * loudness));
        var wavePen = new Pen(new LinearGradientBrush(
            WithAlpha(fromColor, alpha), WithAlpha(toColor, alpha), start, end)
        { MappingMode = BrushMappingMode.Absolute }, 2)
        {
            LineJoin = PenLineJoin.Round,
        };
        context.DrawGeometry(null, wavePen, geometry);
    }

    private void DrawOrb(DrawingContext context, Point center)
    {
        var hot = Dragging && HoverZone == DropZone.Source;
        var inner = Live ? Amber : Color.FromRgb(0x7D, 0x85, 0x96);
        var outer = Live ? Coral : Color.FromRgb(0x3B, 0x41, 0x50);
        var pulse = Live ? SourceLevel : (Math.Sin(_breath * 1.4) + 1) * 0.05;

        var glowRadius = OrbRadius + 34 + (pulse * 34);
        var glow = new RadialGradientBrush(WithAlpha(outer, (byte)(Live ? 95 + (pulse * 80) : 50)), WithAlpha(outer, 0));
        context.DrawEllipse(glow, null, center, glowRadius, glowRadius);

        var body = new RadialGradientBrush(
        [
            new GradientStop(WithAlpha(inner, 255), 0),
            new GradientStop(WithAlpha(outer, 255), 0.72),
            new GradientStop(Darken(outer, 0.72), 1),
        ])
        {
            GradientOrigin = new Point(0.38, 0.28),
            Center = new Point(0.45, 0.4),
            RadiusX = 0.72,
            RadiusY = 0.72,
        };
        context.DrawEllipse(body, null, center, OrbRadius, OrbRadius);

        if (Dragging)
        {
            var pen = new Pen(new SolidColorBrush(WithAlpha(Colors.White, (byte)(hot ? 240 : 90))), hot ? 3 : 1.5);
            context.DrawEllipse(null, pen, center, OrbRadius + 6, OrbRadius + 6);
        }
    }

    private static Color WithAlpha(Color color, int alpha) => Color.FromArgb((byte)alpha, color.R, color.G, color.B);

    private static Color Darken(Color color, double factor)
        => Color.FromRgb((byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
