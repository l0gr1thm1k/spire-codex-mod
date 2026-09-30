using System.Runtime.CompilerServices;
using System.Threading;

namespace SpireCodex.Replay;

internal static class PowerInstances
{
    private static readonly ConditionalWeakTable<object, string> Ids = new();

    private static int _next;

    public static string Of(object power)
        => Ids.GetValue(power, _ => $"{ReplayRecorder.AttemptId}.{Interlocked.Increment(ref _next)}");
}
