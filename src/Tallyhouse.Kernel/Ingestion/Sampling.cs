namespace Tallyhouse.Kernel.Ingestion;

/// <summary>
/// Deterministic sampling by user. Every user falls in one of <see cref="Buckets"/> buckets derived from the
/// hash of their identity, and an event type sampled at rate r keeps the users in the first r * Buckets
/// buckets. The query side depends on two consequences:
/// <list type="bullet">
/// <item>A kept user is kept for every event of that type, so per-user funnels and retention are not biased
/// towards heavy users the way a coin flip per event would be.</item>
/// <item>The kept populations nest. A user kept at 10% is also kept at 50%, so a query spanning types sampled
/// at different rates can restrict itself to the smallest population and scale by one factor.</item>
/// </list>
/// </summary>
public static class Sampling
{
    public const ushort Buckets = 10_000;

    public static ushort BucketOf(ulong userKey) => (ushort)(userKey % Buckets);

    public static ushort ThresholdOf(double rate) => (ushort)Math.Round(rate * Buckets);

    public static bool IsValidRate(double rate) =>
        rate is > 0 and <= 1 && Math.Abs((rate * Buckets) - Math.Round(rate * Buckets)) < 1e-6;

    public static bool Keeps(ushort bucket, ushort threshold) => bucket < threshold;

    /// <summary>The Horvitz-Thompson weight of one kept event: the inverse of the fraction of users kept.</summary>
    public static double WeightOf(ushort threshold) => (double)Buckets / threshold;
}
