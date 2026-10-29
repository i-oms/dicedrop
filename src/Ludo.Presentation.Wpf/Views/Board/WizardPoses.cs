using System.Windows.Media.Imaging;
using Ludo.Application.Session;

namespace Ludo.Presentation.Wpf.Views.Board;

/// <summary>
/// Chooses each wizard's pose: a walk cycle while it hops, its standing pose otherwise, and now and then a
/// short idle action after a random pause.
/// </summary>
internal sealed class WizardPoses
{
    private const double WalkFramesPerSecond = 6;
    private const double ActionSeconds = 1.0;
    private const double MinPauseSeconds = 2;
    private const double MaxPauseSeconds = 6;

    private static readonly string[] WalkFrames = ["walk_a", "walk_b"];
    private static readonly string[] IdleActions = ["act_raise", "act_twirl", "act_gesture"];

    private readonly Dictionary<TokenKey, Idle> _idle = [];

    public BitmapSource Pick(TokenKey key, Piece piece, int arm, bool walking, double now)
    {
        if (walking)
        {
            return WizardSprites.Get(WalkFrames[(int)(now * WalkFramesPerSecond) % WalkFrames.Length], arm);
        }

        if (!_idle.TryGetValue(key, out var idle))
        {
            idle = new Idle(new Random(HashCode.Combine(key.Seat, key.Token)), now);
            _idle[key] = idle;
        }

        if (now >= idle.ActionUntil && now >= idle.NextAction)
        {
            idle.Action = IdleActions[idle.Random.Next(IdleActions.Length)];
            idle.ActionUntil = now + ActionSeconds;
            idle.NextAction = idle.ActionUntil + MinPauseSeconds + (idle.Random.NextDouble() * (MaxPauseSeconds - MinPauseSeconds));
        }

        return now < idle.ActionUntil
            ? WizardSprites.Get(idle.Action, arm)
            : WizardSprites.Get(WizardSprites.StandPose(piece), arm);
    }

    private sealed class Idle(Random random, double now)
    {
        public Random Random { get; } = random;

        public double NextAction { get; set; } = now + MinPauseSeconds + (random.NextDouble() * (MaxPauseSeconds - MinPauseSeconds));

        public double ActionUntil { get; set; } = -1;

        public string Action { get; set; } = IdleActions[0];
    }
}
