using Verse;

namespace RaidApproachProfiler
{
    // ==================================
    //  MOD SETTINGS
    // ==================================

    /// <summary>
    /// Stores user-configurable settings for Raider Approach Lag Fix.
    /// </summary>
    public sealed class RaiderApproachLagFixSettings : ModSettings
    {
        /// <summary>
        /// Controls lightweight timing collection and periodic log reports.
        /// The optimization itself remains active.
        /// </summary>
        public bool EnableDiagnosticLogging;

        /// <summary>
        /// Saves and loads settings through RimWorld's Scribe system.
        /// </summary>
        public override void ExposeData()
        {
            Scribe_Values.Look(
                ref EnableDiagnosticLogging,
                "enableDiagnosticLogging",
                false);

            base.ExposeData();
        }
    }
}