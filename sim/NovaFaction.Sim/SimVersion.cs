namespace NovaFaction.Sim
{
    /// <summary>
    /// The version of the simulation's logic. Replays record it and only replay on the same version.
    /// <para>
    /// Bump <see cref="Current"/> whenever sim logic changes in a way that can change a match's outcome or its state
    /// hashes (movement, targeting, combat, economy, bot decisions, tick order, the hash layout...). Pure refactors,
    /// comments and new tests do not need a bump. When in doubt, bump: an old replay then fails with a clear message
    /// instead of a confusing hash mismatch.
    /// </para>
    /// </summary>
    public static class SimVersion
    {
        // 1: Sept 2026, first version with replay files.
        // 2: Sept 2026, bot saves toward the card its plan wants and fetches chests on its own side.
        public const int Current = 2;
    }
}
