using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ludo.Presentation.Wpf.Views.Board;

/// <summary>The four wizards each player fields: token 0 is the King, 1 the Queen, 2 the Bishop, 3 the Knight.</summary>
internal enum Piece
{
    King,
    Queen,
    Bishop,
    Knight,
}

/// <summary>
/// Draws each pawn as a wizard from the sprite sheet, in the seat's colour, with a shadow on the board that
/// shrinks as the wizard jumps. Spell effects live in <see cref="SpellEffects"/>.
/// </summary>
internal static class PawnPainter
{
    /// <summary>How tall a wizard stands, in multiples of its size, for hit testing and stacking.</summary>
    public const double Height = 1;

    private static readonly Brush ShadowBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)));
    private static readonly Pen HighlightPen = Frozen(new Pen(Brushes.White, 0.07));

    /// <summary>Which role a token plays. Tokens 0 to 3 are the King, Queen, Bishop and Knight.</summary>
    public static Piece PieceOf(int token) => (Piece)(token % 4);

    /// <param name="foot">Where the wizard's feet touch the board.</param>
    /// <param name="size">Wizard size in DIPs; it stands <see cref="Height"/> × size tall.</param>
    /// <param name="lift">How far above the board it is, in DIPs.</param>
    /// <param name="squash">0 at rest, 1 fully squashed on landing.</param>
    /// <param name="tilt">Rotation about the feet in radians; negative topples the wizard to the left.</param>
    /// <param name="alpha">1 when fully visible; lower while a struck wizard fades out.</param>
    /// <param name="pinch">0 at rest; towards 1 the wizard is squeezed narrower, as by vines.</param>
    public static void Draw(DrawingContext dc, Point foot, double size, BitmapSource sprite, double lift, double squash, double tilt, double alpha, double pinch = 0)
    {
        var airborne = Math.Clamp(lift / size, 0, 1);
        var shadow = (1 - (airborne * 0.55)) * (1 + (squash * 0.15));
        if (alpha >= 1)
        {
            dc.PushOpacity(1 - (airborne * 0.5));
            dc.DrawEllipse(ShadowBrush, null, foot, size * 0.3 * shadow, size * 0.1 * shadow);
            dc.Pop();
        }

        // Rotate in unit space first so the wizard topples around its feet, then squash and place it.
        var aspect = sprite.PixelWidth / (double)sprite.PixelHeight;
        var transform = Matrix.Identity;
        transform.Rotate(tilt * 180 / Math.PI);
        transform.Scale(size * (1 + (0.14 * squash)) * (1 - (0.45 * pinch)), size * (1 - (0.2 * squash)));
        transform.Translate(foot.X, foot.Y - lift);
        dc.PushTransform(new MatrixTransform(transform));
        dc.PushOpacity(alpha);
        dc.DrawImage(sprite, new Rect(-aspect / 2, -Height, aspect, Height));
        dc.Pop();
        dc.Pop();
    }

    /// <summary>A ring of the wizard's colour that marks it reappearing in its yard, growing over <paramref name="t"/> (0 to 1).</summary>
    public static void DrawPop(DrawingContext dc, Point at, double size, int arm, double t)
    {
        var radius = size * (0.2 + (0.6 * t));
        dc.PushOpacity(1 - t);
        dc.DrawEllipse(null, new Pen(SeatPalette.Brush(arm), size * 0.07), at, radius, radius * 0.4);
        dc.Pop();
    }

    /// <summary>The pulsing ring under a wizard you can move.</summary>
    public static void DrawMoveRing(DrawingContext dc, Point foot, double size, double pulse)
    {
        var transform = Matrix.Identity;
        var grow = 1 + (pulse * 0.25);
        transform.Scale(size * grow, size * grow * 0.4);
        transform.Translate(foot.X, foot.Y);
        dc.PushTransform(new MatrixTransform(transform));
        dc.PushOpacity(0.95 - (pulse * 0.6));
        dc.DrawEllipse(null, HighlightPen, new Point(0, 0), 0.4, 0.4);
        dc.Pop();
        dc.Pop();
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
