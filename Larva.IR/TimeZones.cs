namespace Larva.IR
{
    /// <summary>
    /// What the managed runtimes (the VM, the in-process native runtime) answer for <c>time.offset</c> and
    /// <c>time.precise</c>.
    /// </summary>
    public static class TimeZones
    {
        private static readonly long MinSeconds = (DateTime.MinValue.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond;
        private static readonly long MaxSeconds = (DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond;

        /// <summary>The local time zone's offset from UTC, in seconds east, at a moment in seconds since 1970 UTC.</summary>
        public static long OffsetSeconds(long seconds)
        {
            seconds = Math.Clamp(seconds, MinSeconds, MaxSeconds);
            var moment = new DateTime(DateTime.UnixEpoch.Ticks + seconds * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            return (long)TimeZoneInfo.Local.GetUtcOffset(moment).TotalSeconds;
        }

        /// <summary>A monotonic clock in nanoseconds.</summary>
        public static long PreciseNanoseconds()
        {
            long stamp = System.Diagnostics.Stopwatch.GetTimestamp(), frequency = System.Diagnostics.Stopwatch.Frequency;
            return stamp / frequency * 1_000_000_000 + stamp % frequency * 1_000_000_000 / frequency;
        }
    }
}
