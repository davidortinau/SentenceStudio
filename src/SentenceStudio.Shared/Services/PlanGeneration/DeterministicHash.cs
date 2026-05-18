namespace SentenceStudio.Services.PlanGeneration;

/// <summary>
/// Process-stable hashing helpers for deterministic plan generation. The BCL's
/// <see cref="string.GetHashCode()"/> uses a per-AppDomain random seed for
/// security reasons, which means the same input produces different hashes
/// across process restarts. Plan generation needs the opposite: identical
/// inputs (resource ids, dates) MUST produce identical sort orders run after
/// run so users see consistent picks and tests don't flake on the tiebreaker.
/// </summary>
internal static class DeterministicHash
{
    /// <summary>FNV-1a 64-bit hash. Cheap, well-distributed, deterministic.</summary>
    public static long Fnv1a64(string value)
    {
        unchecked
        {
            const long offsetBasis = unchecked((long)14695981039346656037UL);
            const long prime = 1099511628211L;
            long hash = offsetBasis;
            if (value is null) return hash;
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= prime;
            }
            return hash;
        }
    }

    /// <summary>Combine a string and a date into one deterministic 64-bit key.
    /// Used as the plan-generation tiebreaker so a candidate's relative order
    /// rotates day-by-day without depending on randomized hashing.</summary>
    public static long Combine(string value, DateTime date)
    {
        unchecked
        {
            long h = Fnv1a64(value);
            // Mix in the date's day-precision ticks. Kind is ignored — callers
            // already normalize today's value to Kind=Utc midnight.
            long dayTicks = date.Date.Ticks;
            const long prime = 1099511628211L;
            h ^= dayTicks;
            h *= prime;
            return h;
        }
    }
}
