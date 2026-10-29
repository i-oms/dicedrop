using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ludo.Presentation.Wpf.Views.Board;

/// <summary>The element a seat's colour stands for. Its spells look and act according to it.</summary>
internal enum Element
{
    /// <summary>Red: fire engulfs the victim and burns it away to ash.</summary>
    Fire,

    /// <summary>Green: vines burst from the ground and drag the victim underground.</summary>
    Forest,

    /// <summary>Yellow: a small sun hovers over the victim and burns it to ash.</summary>
    Light,

    /// <summary>Blue: a huge wave sweeps the victim away.</summary>
    Water,
}

/// <summary>How a struck wizard moves during the impact: how far it topples, fades, sinks and flattens.</summary>
/// <param name="Tilt">Toppling angle in radians, negative to the left.</param>
/// <param name="Alpha">Opacity.</param>
/// <param name="Sink">How far it is pulled into the ground, in cells.</param>
/// <param name="Squash">Extra flattening, 0 at rest.</param>
/// <param name="Pinch">How narrowly the wizard is squeezed, 0 at rest and 1 crushed.</param>
/// <param name="Drift">How far it is swept sideways, in cells.</param>
internal readonly record struct ImpactPose(double Tilt, double Alpha, double Sink, double Squash, double Pinch = 0, double Drift = 0);

/// <summary>When each stage of a spell starts and ends, as shares of its length, and how long it lasts in seconds.</summary>
internal readonly record struct SpellTiming(double Seconds, double ChargeEnd, double FlightEnd, double ImpactEnd, double AftermathEnd);

/// <summary>
/// Draws each element's spell: the charge in the caster's hand, the flight to the victim, the impact that
/// strikes it, and the aftermath that lingers on its square. Colours follow the element, not the seat.
/// </summary>
internal static class SpellEffects
{
    private static readonly Brush Scorch = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x3A, 0x0E, 0x06)));
    private static readonly Brush SunGlow = Orb(Colors.White, Color.FromRgb(0xFF, 0xD8, 0x5A));

    private static readonly Brush FireOrb = Orb(Color.FromRgb(0xFF, 0xE2, 0x7A), Color.FromRgb(0xE0, 0x28, 0x1E));
    private static readonly Brush WaterOrb = Orb(Color.FromRgb(0xE6, 0xF9, 0xFF), Color.FromRgb(0x3F, 0xA9, 0xF5));
    private static readonly Brush LightOrb = Orb(Colors.White, Color.FromRgb(0xFF, 0xE0, 0x66));
    private static readonly Brush ForestOrb = Orb(Color.FromRgb(0xE6, 0xFF, 0xD0), Color.FromRgb(0x2E, 0x9E, 0x4A));

    private static readonly Brush Ember = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x1F)));
    private static readonly Brush Foam = Frozen(new SolidColorBrush(Color.FromRgb(0xDF, 0xF4, 0xFF)));
    private static readonly Brush Gold = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x5A)));
    private static readonly Brush Leaf = Frozen(new SolidColorBrush(Color.FromRgb(0x3C, 0xB0, 0x5A)));
    private static readonly Brush LeafDark = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x6B, 0x35)));
    private static readonly Brush Light = Frozen(new SolidColorBrush(Colors.White));

    /// <summary>The element of a seat's colour: red, green, yellow and blue are fire, forest, light and water.</summary>
    public static Element ElementOf(int arm) => (Element)(((arm % 4) + 4) % 4);

    /// <summary>When each stage of each element's spell starts and ends.</summary>
    public static SpellTiming TimingOf(Element element) => element switch
    {
        Element.Forest => new SpellTiming(2.4, 0.1, 0.15, 0.6, 0.85),
        Element.Fire => new SpellTiming(2.4, 0.15, 0.22, 0.6, 0.8),
        Element.Water => new SpellTiming(2.4, 0.15, 0.22, 0.7, 0.9),
        Element.Light => new SpellTiming(2.4, 0.15, 0.3, 0.7, 0.9),
        _ => throw new ArgumentOutOfRangeException(nameof(element)),
    };

    /// <summary>The charge gathering in the caster's hand, growing over <paramref name="t"/> (0 to 1).</summary>
    public static void DrawCharge(DrawingContext dc, Element element, Point hand, double size, double t)
    {
        var radius = size * (0.04 + (0.2 * t));
        dc.DrawEllipse(OrbFor(element), null, hand, radius, radius);

        switch (element)
        {
            case Element.Fire:
                for (var i = 0; i < 3; i++)
                {
                    var angle = (i * 2.1) + (t * 6);
                    var spark = hand + (new Vector(Math.Cos(angle), Math.Sin(angle)) * radius * 1.4);
                    dc.DrawEllipse(Ember, null, spark, size * 0.03, size * 0.03);
                }

                break;
            case Element.Forest:
                for (var i = 0; i < 3; i++)
                {
                    var angle = (i * 2.1) + (t * 5);
                    var leaf = hand + (new Vector(Math.Cos(angle), Math.Sin(angle)) * size * 0.22 * t);
                    dc.DrawEllipse(i % 2 == 0 ? Leaf : LeafDark, null, leaf, size * 0.05, size * 0.025);
                }

                break;
            case Element.Light:
                // The sun gathers in the caster's hand as its swirl turns and grows.
                DrawSprite(dc, SpellSprites.LightCharge[(int)(t * 16) % SpellSprites.LightCharge.Length], hand, size * (0.25 + (0.35 * t)), 0);
                break;
            case Element.Water:
                for (var i = 0; i < 2; i++)
                {
                    var angle = (i * Math.PI) + (t * 7);
                    var drop = hand + (new Vector(Math.Cos(angle), Math.Sin(angle) * 0.5) * radius * 1.5);
                    dc.DrawEllipse(Foam, null, drop, size * 0.03, size * 0.03);
                }

                break;
        }
    }

    /// <summary>
    /// The spell flying from <paramref name="from"/> to <paramref name="to"/>, at <paramref name="t"/> (0 to 1).
    /// </summary>
    public static void DrawProjectile(DrawingContext dc, Element element, Point from, Point to, double size, double t)
    {
        var tip = Lerp(from, to, t);
        switch (element)
        {
            case Element.Fire:
                DrawSprite(dc, SpellSprites.Fireballs[(int)(t * 12) % SpellSprites.Fireballs.Length], tip, size * 1.2, Math.Atan2(to.Y - from.Y, to.X - from.X));
                break;
            case Element.Water:
                // The whirlpool travels with the spell, its frames turning as it goes.
                DrawSprite(dc, SpellSprites.Whirlpools[(int)(t * 16) % SpellSprites.Whirlpools.Length], tip, size * 1.0, 0);
                break;
            case Element.Light:
                // A ray of light leaves the caster's hand and lances towards the victim, its burning head at the tip.
                dc.DrawLine(new Pen(Gold, size * 0.12 * t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, from, tip);
                dc.DrawLine(new Pen(Light, size * 0.04 * t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, from, tip);
                DrawSprite(dc, SpellSprites.LightSun[(int)(t * 12) % SpellSprites.LightSun.Length], tip, size * 0.9, 0);
                break;
            case Element.Forest:
                // A coil of vine from the sheet's climbing frames, turned along the flight and cycling as it goes.
                DrawSprite(dc, SpellSprites.Vines[2 + ((int)(t * 12) % 4)], tip, size * 0.9, Math.Atan2(to.Y - from.Y, to.X - from.X));
                break;
        }
    }

    /// <summary>What a struck wizard does during the impact, at <paramref name="t"/> (0 to 1).</summary>
    public static ImpactPose Impact(Element element, double t)
    {
        var eased = Ease(t);
        return element switch
        {
            Element.Fire => new ImpactPose(0, 1 - Ease(Math.Clamp((t - 0.5) / 0.5, 0, 1)), 0, 0.3 * t),
            Element.Water => WaterImpact(t),
            Element.Light => new ImpactPose(0, 1 - Ease(Math.Clamp((t - 0.3) / 0.6, 0, 1)), 0, 0.5 * t),
            Element.Forest => ForestImpact(t),
            _ => throw new ArgumentOutOfRangeException(nameof(element)),
        };
    }

    /// <summary>The impact itself, on the victim's square, at <paramref name="t"/> (0 to 1).</summary>
    public static void DrawImpact(DrawingContext dc, Element element, Point foot, double size, double t)
    {
        var eased = Ease(t);
        switch (element)
        {
            case Element.Fire:
            {
                // The ground under the victim scorches black while the flames rise around it (see DrawImpactFront).
                dc.DrawEllipse(Scorch, null, foot, size * 0.7 * eased, size * 0.28 * eased);
                break;
            }

            case Element.Light:
            {
                // The ground beneath the wizard blazes white-gold as the heat builds.
                dc.DrawEllipse(SunGlow, null, foot, size * (0.5 + (0.6 * eased)), size * (0.2 + (0.2 * eased)));
                break;
            }

        }
    }

    /// <summary>The part of the impact that passes in front of the wizard, drawn over it, at <paramref name="t"/> (0 to 1).</summary>
    public static void DrawImpactFront(DrawingContext dc, Element element, Point foot, double size, double t)
    {
        if (element == Element.Fire)
        {
            DrawFlames(dc, foot, size, t);
        }
        else if (element == Element.Water)
        {
            DrawWhirlpool(dc, foot, size, t);
        }
        else if (element == Element.Light)
        {
            DrawIncineration(dc, foot, size, t);
        }
        else if (element == Element.Forest)
        {
            DrawVineSprite(dc, foot, size, t);
        }
    }

    private static ImpactPose ForestImpact(double t)
    {
        var pinch = ForestPinch(t);
        var sink = 0.9 * Ease(Math.Clamp((t - 0.7) / 0.3, 0, 1));
        var fade = 1 - Ease(Math.Clamp((t - 0.85) / 0.15, 0, 1));
        return new ImpactPose(0.05 * Math.Sin(t * 40) * pinch, fade, sink, 0, pinch);
    }

    /// <summary>How tightly the vines have closed round the wizard: loose at first, crushing by the end.</summary>
    private static double ForestPinch(double t) => Ease(Math.Clamp((t - 0.3) / 0.5, 0, 1));

    /// <summary>
    /// The vine sheet's frames, one after another across the impact: sprouting from the ground, closing round
    /// the wizard, then bursting into leaves. Drawn over the wizard, fading once it is gone.
    /// </summary>
    private static void DrawVineSprite(DrawingContext dc, Point foot, double size, double t)
    {
        var appear = Ease(Math.Clamp(t / 0.1, 0, 1));
        var fade = 1 - Ease(Math.Clamp((t - 0.85) / 0.15, 0, 1));
        if (appear <= 0 || fade <= 0)
        {
            return;
        }

        var frame = Math.Min(SpellSprites.Vines.Length - 1, (int)(t * SpellSprites.Vines.Length));
        var sprite = SpellSprites.Vines[frame];
        var width = size * 1.8;
        var height = width * sprite.PixelHeight / sprite.PixelWidth;
        dc.PushOpacity(appear * fade);
        DrawSprite(dc, sprite, foot - new Vector(0, height / 2), width, 0);
        dc.Pop();
    }


    /// <summary>
    /// The sun's blast strikes the wizard and incinerates it: a burst flares over the square, then dissolves into
    /// embers and smoke. Drawn over the wizard.
    /// </summary>
    private static void DrawIncineration(DrawingContext dc, Point foot, double size, double t)
    {
        var appear = Ease(Math.Clamp(t / 0.15, 0, 1));
        var fade = 1 - Ease(Math.Clamp((t - 0.8) / 0.2, 0, 1));
        if (appear <= 0 || fade <= 0)
        {
            return;
        }

        var frames = SpellSprites.LightBurst;
        var sprite = frames[Math.Min(frames.Length - 1, (int)(t * frames.Length))];
        var width = size * 1.8 * appear;
        var height = width * sprite.PixelHeight / sprite.PixelWidth;
        dc.PushOpacity(fade);
        DrawSprite(dc, sprite, foot - new Vector(0, height / 2), width, 0);
        dc.Pop();
    }

    /// <summary>
    /// What a wizard caught by the whirlpool does: it is drawn down into the spinning centre, turning as it goes,
    /// squeezed narrow and gone as the water closes over it.
    /// </summary>
    private static ImpactPose WaterImpact(double t)
    {
        var pull = Ease(Math.Clamp((t - 0.1) / 0.7, 0, 1));
        var swallow = Ease(Math.Clamp((t - 0.5) / 0.4, 0, 1));
        return new ImpactPose(0.25 * Math.Sin(t * 18) * pull, 1 - swallow, 0.9 * pull, 0, swallow);
    }

    /// <summary>
    /// The whirlpool from the sprite sheet: it swells over the square, spins while the wizard is pulled under,
    /// then shrinks away. Drawn over the wizard.
    /// </summary>
    private static void DrawWhirlpool(DrawingContext dc, Point foot, double size, double t)
    {
        var swell = Ease(Math.Clamp(t / 0.25, 0, 1));
        var fade = 1 - Ease(Math.Clamp((t - 0.7) / 0.3, 0, 1));
        if (swell <= 0 || fade <= 0)
        {
            return;
        }

        var sprite = SpellSprites.Whirlpools[(int)(t * 40) % SpellSprites.Whirlpools.Length];
        var width = size * 1.9 * swell;
        var height = width * sprite.PixelHeight / sprite.PixelWidth;
        dc.PushOpacity(fade);
        DrawSprite(dc, sprite, foot - new Vector(0, height * 0.2), width, 0);
        dc.Pop();
    }

    /// <summary>The red fire that engulfs the wizard, rising from the square as the wizard burns. Drawn over the wizard.</summary>
    private static void DrawFlames(DrawingContext dc, Point foot, double size, double t)
    {
        var rise = Ease(Math.Clamp(t / 0.35, 0, 1));
        var fade = 1 - Ease(Math.Clamp((t - 0.75) / 0.25, 0, 1));
        if (rise <= 0 || fade <= 0)
        {
            return;
        }

        var sprite = SpellSprites.Flames[(int)(t * 12) % SpellSprites.Flames.Length];
        var width = size * 1.6 * rise * (1 + (0.05 * Math.Sin(t * 30)));
        var height = width * sprite.PixelHeight / sprite.PixelWidth;
        dc.PushOpacity(fade);
        DrawSprite(dc, sprite, foot - new Vector(0, height / 2), width, 0);
        dc.Pop();
    }

    /// <summary>Draws a sprite centred on <paramref name="centre"/>, turned by <paramref name="angle"/> radians.</summary>
    private static void DrawSprite(DrawingContext dc, BitmapSource sprite, Point centre, double width, double angle)
    {
        var height = width * sprite.PixelHeight / sprite.PixelWidth;
        dc.PushTransform(new RotateTransform(angle * 180 / Math.PI, centre.X, centre.Y));
        dc.DrawImage(sprite, new Rect(centre.X - (width / 2), centre.Y - (height / 2), width, height));
        dc.Pop();
    }

    /// <summary>What lingers on the square after the impact, at <paramref name="t"/> (0 to 1).</summary>
    public static void DrawAftermath(DrawingContext dc, Element element, Point foot, double size, double t)
    {
        switch (element)
        {
            case Element.Light:
            {
                // Smoke from the burnt wizard hangs over the square, then thins out.
                var smoke = SpellSprites.LightAsh[(int)(t * 8) % SpellSprites.LightAsh.Length];
                dc.PushOpacity(1 - t);
                DrawSprite(dc, smoke, foot - new Vector(0, size * 0.3), size * 1.4, 0);
                dc.Pop();
                break;
            }
            case Element.Forest:
                for (var i = 0; i < 6; i++)
                {
                    var angle = (i * Math.PI / 3) + (t * 3);
                    var leaf = foot + new Vector(Math.Cos(angle) * size * (0.3 + (0.5 * t)), (Math.Sin(angle) * size * 0.2) - (size * 0.8 * t));
                    dc.PushOpacity(1 - t);
                    dc.DrawEllipse(i % 2 == 0 ? Leaf : LeafDark, null, leaf, size * 0.08, size * 0.045);
                    dc.Pop();
                }

                break;
        }
    }


    private static Brush OrbFor(Element element) => element switch
    {
        Element.Fire => FireOrb,
        Element.Water => WaterOrb,
        Element.Light => LightOrb,
        Element.Forest => ForestOrb,
        _ => throw new ArgumentOutOfRangeException(nameof(element)),
    };

    private static Brush Orb(Color core, Color edge) => Frozen(new RadialGradientBrush
    {
        GradientStops =
        [
            new GradientStop(core, 0),
            new GradientStop(edge, 0.5),
            new GradientStop(Color.FromArgb(0x00, edge.R, edge.G, edge.B), 1),
        ],
    });

    private static Point Lerp(Point a, Point b, double t) => a + ((b - a) * t);

    private static double Ease(double t) => t * t * (3 - (2 * t));

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
