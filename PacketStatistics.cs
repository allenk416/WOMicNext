namespace WOMicNext;

internal sealed record RecentPackets(long Received, long Lost, long Discarded)
{
    public double LossPercent => Received + Lost == 0 ? 0 : 100.0 * Lost / (Received + Lost);
}

internal sealed class PacketStatistics
{
    private const int BucketCount = 100; // 100 ms bins, fixed memory for the most recent 10 seconds.
    private sealed class Bucket
    {
        public long Time = -1, Received, Lost, Discarded;
    }
    private readonly Bucket[] buckets = Enumerable.Range(0, BucketCount).Select(_ => new Bucket()).ToArray();
    private readonly object gate = new();
    private readonly Func<long> milliseconds;

    internal PacketStatistics(Func<long>? clock = null) => milliseconds = clock ?? (() => Environment.TickCount64);

    public void Record(long received = 0, long lost = 0, long discarded = 0)
    {
        lock (gate)
        {
            long time = milliseconds() / 100;
            var bucket = buckets[(int)(time % BucketCount)];
            if (bucket.Time != time)
            { bucket.Time = time; bucket.Received = bucket.Lost = bucket.Discarded = 0; }
            bucket.Received += received; bucket.Lost += lost; bucket.Discarded += discarded;
        }
    }

    public RecentPackets Snapshot()
    {
        lock (gate)
        {
            long time = milliseconds() / 100;
            long received = 0, lost = 0, discarded = 0;
            foreach (var bucket in buckets)
            {
                if (bucket.Time < 0 || bucket.Time > time || time - bucket.Time >= BucketCount) continue;
                received += bucket.Received; lost += bucket.Lost; discarded += bucket.Discarded;
            }
            return new(received, lost, discarded);
        }
    }

    public void Reset()
    {
        lock (gate)
            foreach (var bucket in buckets)
            { bucket.Time = -1; bucket.Received = bucket.Lost = bucket.Discarded = 0; }
    }
}
