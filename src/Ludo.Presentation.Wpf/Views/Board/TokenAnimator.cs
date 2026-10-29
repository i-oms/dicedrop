using System.Windows;
using Ludo.Application.Session;

namespace Ludo.Presentation.Wpf.Views.Board;

internal readonly record struct TokenKey(int Seat, int Token);

/// <summary>Where to draw a pawn this frame, and any spell effects on it.</summary>
/// <param name="Point">Centre of the square the pawn is over.</param>
/// <param name="Lift">Height above the board, in cells.</param>
/// <param name="Squash">0 at rest, 1 fully squashed.</param>
/// <param name="Tilt">Toppling angle in radians, for a wizard knocked over by a spell.</param>
/// <param name="Alpha">Opacity; 0 while a struck wizard is hidden away.</param>
/// <param name="Burst">0 to 1 while the spell's impact strikes this wizard, otherwise -1.</param>
/// <param name="SpellArm">Seat colour of the wizard that cast the spell (its element), or -1 for none.</param>
/// <param name="Aftermath">0 to 1 while the spell's aftermath plays on this wizard's square, otherwise -1.</param>
/// <param name="Cast">0 to 1 while this wizard charges its spell, otherwise -1.</param>
/// <param name="Blast">0 to 1 while this wizard's spell flies out, otherwise -1.</param>
/// <param name="Incoming">0 to 1 while a spell is flying at this wizard, otherwise -1.</param>
/// <param name="Pop">0 to 1 while a ring marks this wizard reappearing in its yard, otherwise -1.</param>
internal readonly record struct TokenPose(
    Point Point,
    double Lift,
    double Squash,
    bool IsMoving,
    double Tilt = 0,
    double Alpha = 1,
    double Burst = -1,
    int SpellArm = -1,
    double Aftermath = -1,
    double Cast = -1,
    double Blast = -1,
    double Incoming = -1,
    double Pop = -1);

/// <summary>
/// Turns board snapshots into motion. When a token's progress grows it hops square by square, as when
/// counting out a move by hand. A capture plays as one spell in the capturing wizard's element: it charges,
/// the spell flies at the victim, the element's impact strikes, its aftermath lingers, and the victim
/// reappears in its yard. Each element sets how long its spell takes. Motions play one after another, in the
/// order they happened.
/// </summary>
internal sealed class TokenAnimator
{
    private const double HopSeconds = 0.17;
    private const double EntrySeconds = 0.32;
    private const double SettleSeconds = 0.12;
    private const double HopHeight = 0.55;
    private const double EntryHeight = 1.1;

    private readonly Dictionary<TokenKey, int> _known = [];
    private readonly Queue<Motion> _queue = new();
    private Motion? _current;
    private double _currentStart;

    public bool IsBusy => _current is not null || _queue.Count > 0;

    /// <summary>Takes the snapshot's positions as they are, with nothing to animate.</summary>
    public void Reset(GameStateView? game)
    {
        _known.Clear();
        _queue.Clear();
        _current = null;
        if (game is null)
        {
            return;
        }

        foreach (var seat in game.Seats)
        {
            foreach (var token in seat.Tokens)
            {
                _known[new TokenKey(seat.Seat, token.Index)] = token.Progress;
            }
        }
    }

    public void Update(GameStateView game, double now)
    {
        var forward = new List<Motion>();
        var back = new List<Motion>();
        foreach (var seat in game.Seats)
        {
            foreach (var token in seat.Tokens)
            {
                var key = new TokenKey(seat.Seat, token.Index);
                if (_known.TryGetValue(key, out var old) && old != token.Progress && seat.Standing != SeatStanding.Left)
                {
                    (token.Progress > old ? forward : back).Add(new Motion(key, old, token.Progress) { Arm = seat.Arm });
                }

                _known[key] = token.Progress;
            }
        }

        // Each spell resolves before its caster steps onto the square, so the victim is gone first.
        var caster = forward.FirstOrDefault();
        foreach (var victim in back)
        {
            victim.Caster = caster?.Key;
            victim.CasterArm = caster?.Arm ?? -1;
            victim.CasterFrom = caster?.From ?? -1;
        }

        foreach (var motion in back.Concat(forward))
        {
            _queue.Enqueue(motion);
        }

        Tick(now);
    }

    public void Tick(double now)
    {
        while (true)
        {
            if (_current is { } running)
            {
                var end = _currentStart + Duration(running);
                if (now < end)
                {
                    return;
                }

                _current = null;
                if (_queue.Count == 0)
                {
                    return;
                }

                // Chain on from where the last one ended so a slow frame doesn't stretch the sequence.
                _currentStart = end;
            }
            else if (_queue.Count > 0)
            {
                _currentStart = now;
            }
            else
            {
                return;
            }

            _current = _queue.Dequeue();

            // Play faster when moves pile up, so the board never falls far behind the game.
            _current.Speed = _queue.Count > 1 ? 2.5 : 1;
        }
    }

    /// <param name="path">Square centre for a progress value along this token's path.</param>
    public TokenPose Pose(TokenKey key, Func<int, Point> path, double now)
    {
        if (_current is { } motion && motion.Key == key)
        {
            return motion.IsCapture ? Spell(motion, path, now - _currentStart) : Hop(motion, path, now - _currentStart);
        }

        // The wizard whose spell is in progress charges it, then casts it.
        if (_current is { IsCapture: true } spell && spell.Caster == key)
        {
            var timing = SpellEffects.TimingOf(SpellEffects.ElementOf(spell.CasterArm));
            var progress = Math.Clamp((now - _currentStart) / Duration(spell), 0, 1);
            var square = path(spell.CasterFrom);
            if (progress < timing.ChargeEnd)
            {
                return new TokenPose(square, 0, 0, false, Cast: progress / timing.ChargeEnd);
            }

            if (progress < timing.FlightEnd)
            {
                return new TokenPose(square, 0, 0, false, Blast: (progress - timing.ChargeEnd) / (timing.FlightEnd - timing.ChargeEnd));
            }

            return new TokenPose(square, 0, 0, false);
        }

        // Waiting for its turn to move: stay where the queued motion starts.
        foreach (var queued in _queue)
        {
            if (queued.Key == key)
            {
                return new TokenPose(path(queued.From), 0, 0, false);
            }
        }

        return new TokenPose(path(_known.GetValueOrDefault(key, -1)), 0, 0, false);
    }

    private static double Duration(Motion motion) => motion.IsCapture
        ? SpellEffects.TimingOf(SpellEffects.ElementOf(motion.CasterArm)).Seconds / motion.Speed
        : ((motion.From < 0 ? EntrySeconds : HopSeconds * (motion.To - motion.From)) + SettleSeconds) / motion.Speed;

    private static TokenPose Hop(Motion motion, Func<int, Point> path, double elapsed)
    {
        elapsed *= motion.Speed;
        var entering = motion.From < 0;
        var hop = entering ? EntrySeconds : HopSeconds;
        var hops = entering ? 1 : motion.To - motion.From;
        if (elapsed >= hops * hop)
        {
            var settle = Math.Clamp((elapsed - (hops * hop)) / SettleSeconds, 0, 1);
            return new TokenPose(path(motion.To), 0, 1 - settle, true);
        }

        var index = (int)(elapsed / hop);
        var t = (elapsed / hop) - index;
        var from = entering ? -1 : motion.From + index;
        var to = entering ? motion.To : from + 1;
        var height = entering ? EntryHeight : HopHeight;

        // Each landing squashes the pawn briefly before it pushes off again.
        var squash = index > 0 && t < 0.22 ? 1 - (t / 0.22) : 0;
        return new TokenPose(Lerp(path(from), path(to), Ease(t)), Math.Sin(Math.PI * t) * height, squash, true);
    }

    /// <summary>
    /// A captured wizard's spell, in four stages: the spell flies at it, its element's impact strikes it, the
    /// aftermath lingers over its square while it is out of sight, then it reappears standing in its yard.
    /// </summary>
    private static TokenPose Spell(Motion motion, Func<int, Point> path, double elapsed)
    {
        var timing = SpellEffects.TimingOf(SpellEffects.ElementOf(motion.CasterArm));
        var progress = Math.Clamp(elapsed / Duration(motion), 0, 1);
        var square = path(motion.From);

        if (progress < timing.FlightEnd)
        {
            var incoming = progress < timing.ChargeEnd ? -1 : (progress - timing.ChargeEnd) / (timing.FlightEnd - timing.ChargeEnd);
            return new TokenPose(square, 0, 0, false, Incoming: incoming);
        }

        if (progress < timing.ImpactEnd)
        {
            var impact = (progress - timing.FlightEnd) / (timing.ImpactEnd - timing.FlightEnd);
            return new TokenPose(square, 0, 0, false, Burst: impact, SpellArm: motion.CasterArm);
        }

        if (progress < timing.AftermathEnd)
        {
            var aftermath = (progress - timing.ImpactEnd) / (timing.AftermathEnd - timing.ImpactEnd);
            return new TokenPose(square, 0, 0, false, Alpha: 0, Aftermath: aftermath, SpellArm: motion.CasterArm);
        }

        var reset = (progress - timing.AftermathEnd) / (1 - timing.AftermathEnd);
        return new TokenPose(path(-1), 0, 0, false, Alpha: Ease(reset), Pop: reset);
    }

    private static double Ease(double t) => t * t * (3 - (2 * t));

    private static Point Lerp(Point a, Point b, double t) => a + ((b - a) * t);

    private sealed record Motion(TokenKey Key, int From, int To)
    {
        public bool IsCapture => To < From;

        public double Speed { get; set; } = 1;

        /// <summary>The seat colour this wizard plays in.</summary>
        public int Arm { get; init; }

        /// <summary>For a capture: the wizard whose spell struck this one, if it had just landed.</summary>
        public TokenKey? Caster { get; set; }

        /// <summary>The caster's seat colour (its element), or -1 when there is no caster.</summary>
        public int CasterArm { get; set; } = -1;

        /// <summary>The caster's progress before its move, where it stands while the spell is cast.</summary>
        public int CasterFrom { get; set; } = -1;
    }
}
