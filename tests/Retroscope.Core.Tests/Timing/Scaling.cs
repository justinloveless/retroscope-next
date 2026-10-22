using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Retroscope.Core.Tests.Timing;

/// <summary>The timing tests run alone, so other tests do not compete for the caches whose misses they would count.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimedAlone {
    public const string Name = "Timing";
}

/// <summary>
/// Tells linear work from quadratic work without a wall-clock budget, which a loaded machine can always exceed: the
/// same operation on an input four times larger takes about 4 times the work when it is linear and about 16 times
/// when it is quadratic, however slow the machine is.
/// </summary>
internal static class Scaling {
    /// <summary>Linear work stays below it even on a loaded machine; quadratic work, about 16, passes it.</summary>
    public const double MaxLinearRatio = 11;

    private const int Runs = 9;
    private static readonly TimeSpan MinSample = TimeSpan.FromMilliseconds(10);

    // Chosen once, so a run never compares samples of one clock with samples of the other.
    private static readonly Func<TimeSpan> ThreadCpuTime = CpuClock() ?? (() => Stopwatch.GetElapsedTime(0));

    /// <summary>
    /// The work <paramref name="large"/> does divided by the work <paramref name="small"/> does, where large has four times
    /// the input. Work is this thread's CPU time, not elapsed time: a loaded machine preempts a long sample more often than a
    /// short one, which would inflate the ratio. Each sample starts on a collected heap, the two sizes alternate, the
    /// cheapest sample of each counts, and short operations are repeated until a sample is long enough to measure.
    /// </summary>
    public static double Ratio(Action small, Action large) {
        small();
        large();
        var repeats = (int)Math.Clamp(MinSample / Work(small, 1), 1, 10_000);
        var cheapestSmall = TimeSpan.MaxValue;
        var cheapestLarge = TimeSpan.MaxValue;
        for (var run = 0; run < Runs; run++) {
            cheapestSmall = Min(cheapestSmall, Work(small, repeats));
            cheapestLarge = Min(cheapestLarge, Work(large, repeats));
        }
        return cheapestLarge / cheapestSmall;
    }

    private static TimeSpan Work(Action action, int repeats) {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = ThreadCpuTime();
        for (var i = 0; i < repeats; i++) {
            action();
        }
        return ThreadCpuTime() - before;
    }

    // CLOCK_THREAD_CPUTIME_ID is 16 on macOS and 3 on Linux. On other platforms, or where clock_gettime cannot be
    // loaded from libc, elapsed time is the best there is.
    private static Func<TimeSpan>? CpuClock() {
        var clock = OperatingSystem.IsMacOS() ? 16 : OperatingSystem.IsLinux() ? 3 : -1;
        try {
            if (clock < 0 || ClockGetTime(clock, out _) != 0) {
                return null;
            }
        } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) {
            return null;
        }
        return () => ClockGetTime(clock, out var now) == 0
            ? TimeSpan.FromTicks((now.Seconds * TimeSpan.TicksPerSecond) + (now.Nanoseconds / 100))
            : throw new InvalidOperationException("clock_gettime failed after it had worked.");
    }

    private static TimeSpan Min(TimeSpan x, TimeSpan y) => x < y ? x : y;

    [DllImport("libc", EntryPoint = "clock_gettime")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int ClockGetTime(int clock, out TimeSpec time);

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec {
        public long Seconds;
        public long Nanoseconds;
    }
}
