using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ludo.Application.Session;

namespace Ludo.Presentation.Wpf.Views.Board;

/// <summary>
/// Draws a <see cref="GameStateView"/> as a classic Ludo board, animates pawns between snapshots and turns
/// clicks on movable pawns into the move command. The board is turned so your own yard is bottom left.
/// </summary>
public sealed class BoardCanvas : FrameworkElement
{
    public static readonly DependencyProperty GameProperty = DependencyProperty.Register(
        nameof(Game),
        typeof(GameStateView),
        typeof(BoardCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnGameChanged));

    public static readonly DependencyProperty MoveCommandProperty = DependencyProperty.Register(
        nameof(MoveCommand),
        typeof(ICommand),
        typeof(BoardCanvas));

    /// <summary>A pawn stands this far below its square's centre, so it looks centred on the square.</summary>
    private const double FootOffset = 0.3;

    /// <summary>Scale of a wizard in cells; it stands about <see cref="PawnPainter.Height"/> times this tall.</summary>
    private const double PawnSize = 1.0;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TokenAnimator _animator = new();
    private readonly WizardPoses _poses = new();
    private readonly List<(Point Center, double Radius, int Token)> _targets = [];
    private BoardGeometry? _geometry;
    private Drawing? _board;
    private (Size Size, BoardLayoutView Layout, int ViewerArm, double Dpi, string Arms)? _boardKey;
    private bool _ticking;

    public BoardCanvas()
    {
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => SetTicking(Game is not null);
        Unloaded += (_, _) => SetTicking(false);
    }

    public GameStateView? Game
    {
        get => (GameStateView?)GetValue(GameProperty);
        set => SetValue(GameProperty, value);
    }

    public ICommand? MoveCommand
    {
        get => (ICommand?)GetValue(MoveCommandProperty);
        set => SetValue(MoveCommandProperty, value);
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    protected override void OnRender(DrawingContext dc)
    {
        _targets.Clear();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Game is not { } game || RenderSize.Width < 100 || RenderSize.Height < 100)
        {
            return;
        }

        var now = Now;
        _animator.Tick(now);
        var geometry = EnsureBoard(game);
        dc.DrawDrawing(_board);
        DrawTurnGlow(dc, game, geometry, now);
        DrawPawns(dc, game, geometry, now);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (FindTarget(e.GetPosition(this)) is { } token && MoveCommand?.CanExecute(token) == true)
        {
            MoveCommand.Execute(token);
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = FindTarget(e.GetPosition(this)) is null ? null : Cursors.Hand;
    }

    private static void OnGameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (BoardCanvas)d;
        var previous = (GameStateView?)e.OldValue;
        var next = (GameStateView?)e.NewValue;

        // A different board (or a fresh game) starts from where the snapshot says, without animating.
        if (next is null || previous is null || previous.Board != next.Board || previous.Seats.Count != next.Seats.Count)
        {
            canvas._animator.Reset(next);
        }
        else
        {
            canvas._animator.Update(next, canvas.Now);
        }

        canvas.SetTicking(next is not null && canvas.IsLoaded);
    }

    private BoardGeometry EnsureBoard(GameStateView game)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var viewerArm = (game.Seats.FirstOrDefault(s => s.IsLocal) ?? game.Seats[0]).Arm;
        var arms = string.Concat(game.Seats.Select(s => s.Standing == SeatStanding.Left ? 'x' : 'o'));
        var key = (RenderSize, game.Board, viewerArm, dpi, arms);
        if (_geometry is null || _board is null || _boardKey != key)
        {
            _geometry = new BoardGeometry(game.Board, RenderSize, viewerArm, dpi);
            _board = BoardPainter.Paint(_geometry, game);
            _boardKey = key;
        }

        return _geometry;
    }

    /// <summary>The yard of whoever's turn it is breathes with a soft white glow.</summary>
    private static void DrawTurnGlow(DrawingContext dc, GameStateView game, BoardGeometry geometry, double now)
    {
        if (game.Phase == GamePhaseView.Over || game.Seats.FirstOrDefault(s => s.IsCurrent) is not { } current)
        {
            return;
        }

        var pulse = 0.5 + (0.5 * Math.Sin(now * 4));
        var yard = BoardGeometry.Polygon(geometry.Yard(current.Arm, 0.94));
        var glow = new SolidColorBrush(Color.FromArgb((byte)(90 + (110 * pulse)), 0xFF, 0xFF, 0xFF));
        dc.DrawGeometry(null, new Pen(glow, geometry.Cell * 0.16) { LineJoin = PenLineJoin.Round }, yard);
    }

    private void DrawPawns(DrawingContext dc, GameStateView game, BoardGeometry geometry, double now)
    {
        var cell = geometry.Cell;
        var canClick = !_animator.IsBusy;
        var pawns = new List<Pawn>();
        foreach (var seat in game.Seats.Where(s => s.Standing != SeatStanding.Left))
        {
            foreach (var token in seat.Tokens)
            {
                var pose = _animator.Pose(new TokenKey(seat.Seat, token.Index), p => geometry.PathPoint(seat, token.Index, p), now);
                var finished = !pose.IsMoving && token.Location == TokenLocation.Finished;
                var sprite = _poses.Pick(new TokenKey(seat.Seat, token.Index), PawnPainter.PieceOf(token.Index), seat.Arm, pose.IsMoving, now);
                pawns.Add(new Pawn(seat.Arm, token.Index, pose, finished ? 0.62 : 1, token.CanMove && canClick) { Sprite = sprite });
            }
        }

        // Pawns sharing a square huddle together at a smaller size so every one stays visible and clickable.
        foreach (var square in pawns.Where(p => !p.Pose.IsMoving).GroupBy(p => (Math.Round(p.Pose.Point.X, 1), Math.Round(p.Pose.Point.Y, 1))))
        {
            var stack = square.ToArray();
            if (stack.Length < 2)
            {
                continue;
            }

            for (var i = 0; i < stack.Length; i++)
            {
                var offset = StackOffset(i, stack.Length) * cell;
                stack[i].Offset = offset;
                stack[i].Scale *= stack.Length == 2 ? 0.8 : 0.68;
            }
        }

        var bob = Math.Abs(Math.Sin(now * 5.5));
        foreach (var pawn in pawns.OrderBy(p => p.Pose.Point.Y + p.Offset.Y).ThenBy(p => p.Pose.IsMoving))
        {
            var size = cell * PawnSize * pawn.Scale;
            var foot = pawn.Pose.Point + pawn.Offset + new Vector(0, cell * FootOffset * pawn.Scale);
            var lift = pawn.Pose.Lift * cell;
            if (pawn.CanMove)
            {
                PawnPainter.DrawMoveRing(dc, foot, size, (now * 1.6) % 1);
                lift += bob * cell * 0.14;
                _targets.Add((foot - new Vector(0, size * PawnPainter.Height * 0.55), size * 0.55, pawn.Token));
            }

            var pose = pawn.Pose;
            pawn.Head = foot - new Vector(0, size * 0.6);
            pawn.Size = size;

            // A struck wizard is hit by the element of the spell that caught it; with no caster, its own colour.
            var element = SpellEffects.ElementOf(pose.SpellArm >= 0 ? pose.SpellArm : pawn.Arm);
            if (pose.Burst >= 0)
            {
                var impact = SpellEffects.Impact(element, pose.Burst);
                SpellEffects.DrawImpact(dc, element, foot, size, pose.Burst);
                var alpha = Math.Min(pose.Alpha, impact.Alpha);
                if (alpha > 0)
                {
                    PawnPainter.Draw(dc, foot + new Vector(impact.Drift * cell, 0), size, pawn.Sprite, lift - (impact.Sink * cell), pose.Squash + impact.Squash, pose.Tilt + impact.Tilt, alpha, impact.Pinch);
                }

                SpellEffects.DrawImpactFront(dc, element, foot, size, pose.Burst);
            }
            else if (pose.Alpha > 0)
            {
                // A wizard hidden away after its spell is not drawn at all; only the aftermath remains.
                PawnPainter.Draw(dc, foot, size, pawn.Sprite, lift, pose.Squash, pose.Tilt, pose.Alpha);
            }

            if (pose.Aftermath >= 0)
            {
                SpellEffects.DrawAftermath(dc, element, foot, size, pose.Aftermath);
            }

            if (pose.Pop >= 0)
            {
                PawnPainter.DrawPop(dc, foot, size, pawn.Arm, pose.Pop);
            }
        }

        DrawSpellBalls(dc, pawns);
    }

    /// <summary>The charge a wizard gathers in its hand, and the spell that flies from it to the wizard it strikes.</summary>
    private static void DrawSpellBalls(DrawingContext dc, List<Pawn> pawns)
    {
        var target = pawns.FirstOrDefault(p => p.Pose.Incoming >= 0);
        foreach (var caster in pawns)
        {
            var element = SpellEffects.ElementOf(caster.Arm);
            var hand = caster.Head + new Vector(caster.Size * 0.32, caster.Size * 0.12);
            if (caster.Pose.Cast >= 0)
            {
                SpellEffects.DrawCharge(dc, element, hand, caster.Size, caster.Pose.Cast);
            }

            if (caster.Pose.Blast >= 0 && target is not null)
            {
                SpellEffects.DrawProjectile(dc, element, hand, target.Head, caster.Size, caster.Pose.Blast);
            }
        }
    }

    private static Vector StackOffset(int index, int count)
    {
        if (count == 2)
        {
            return new Vector(index == 0 ? -0.2 : 0.2, 0);
        }

        if (count == 3)
        {
            return index switch { 0 => new Vector(0, -0.2), 1 => new Vector(-0.22, 0.14), _ => new Vector(0.22, 0.14) };
        }

        var column = index % 2;
        var row = (index / 2) % 2;
        return new Vector(column == 0 ? -0.2 : 0.2, row == 0 ? -0.2 : 0.16);
    }

    private int? FindTarget(Point position)
    {
        int? best = null;
        var bestDistance = double.MaxValue;
        foreach (var (center, radius, token) in _targets)
        {
            var distance = (center - position).Length;
            if (distance <= radius && distance < bestDistance)
            {
                best = token;
                bestDistance = distance;
            }
        }

        return best;
    }

    private void SetTicking(bool on)
    {
        if (on == _ticking)
        {
            return;
        }

        _ticking = on;
        if (on)
        {
            CompositionTarget.Rendering += OnFrame;
        }
        else
        {
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

    private sealed class Pawn(int arm, int token, TokenPose pose, double scale, bool canMove)
    {
        public required BitmapSource Sprite { get; init; }

        public int Arm { get; } = arm;

        public int Token { get; } = token;

        public TokenPose Pose { get; } = pose;

        public double Scale { get; set; } = scale;

        public bool CanMove { get; } = canMove;

        public Vector Offset { get; set; }

        /// <summary>Where the wizard's head is drawn this frame, for spell effects that fly between wizards.</summary>
        public Point Head { get; set; }

        public double Size { get; set; }
    }
}
