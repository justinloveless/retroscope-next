using System.Diagnostics;

namespace Retroscope.Core.Tests.Timing;

/// <summary>
/// Tells linear work from quadratic work without a wall-clock budget, which a loaded machine can always exceed: the
/// same operation on an input four times larger takes about 4 times as long when it is linear and about 16 times as
/// long when it is quadratic, however slow the machine is.
/// </summary>
internal static class Scaling {
    /// <summary>Linear work stays well below it (n log n gives about 4.6); quadratic work passes it.</summary>
    public const double MaxLinearRatio = 8;

    private const int Runs = 7;
    private static readonly TimeSpan MinSample = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// How many times longer <paramref name="large"/> takes than <paramref name="small"/>, whose input is a quarter
    /// the size. Each is timed in several interleaved runs and the fastest run counts, so load slows both sizes alike
    /// and a pause hits at most some runs. Short operations are repeated until a sample is long enough to time.
    /// </summary>
    public static double Ratio(Action small, Action large) {
        small();
        large();
        var repeats = (int)Math.Clamp(MinSample / Time(small, 1), 1, 1000);
        var fastestSmall = TimeSpan.MaxValue;
        var fastestLarge = TimeSpan.MaxValue;
        for (var run = 0; run < Runs; run++) {
            fastestSmall = Min(fastestSmall, Time(small, repeats));
            fastestLarge = Min(fastestLarge, Time(large, repeats));
        }
        return fastestLarge / fastestSmall;
    }

    private static TimeSpan Time(Action action, int repeats) {
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < repeats; i++) {
            action();
        }
        return stopwatch.Elapsed;
    }

    private static TimeSpan Min(TimeSpan x, TimeSpan y) => x < y ? x : y;
}
