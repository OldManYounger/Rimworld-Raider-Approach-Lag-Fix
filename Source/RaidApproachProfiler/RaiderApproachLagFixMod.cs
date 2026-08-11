using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RaidApproachProfiler
{
    // ==================================
    //  MOD INITIALIZATION
    // ==================================

    /// <summary>
    /// Initializes Raider Approach Lag Fix, loads its saved settings, applies
    /// Harmony patches, and provides its mod-settings interface.
    /// </summary>
    public sealed class RaiderApproachLagFixMod : Mod
    {
        private const string HarmonyId =
            "OldManYoung.RaiderApproachLagFix";

        /// <summary>
        /// Gets the settings currently loaded for this mod.
        /// </summary>
        internal static RaiderApproachLagFixSettings CurrentSettings
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets whether lightweight diagnostic collection and periodic logging
        /// are currently enabled.
        /// </summary>
        internal static bool DiagnosticLoggingEnabled
        {
            get
            {
                return CurrentSettings != null &&
                       CurrentSettings.EnableDiagnosticLogging;
            }
        }

        /// <summary>
        /// Loads settings and applies the optimization patches.
        /// </summary>
        public RaiderApproachLagFixMod(ModContentPack content)
            : base(content)
        {
            CurrentSettings =
                GetSettings<RaiderApproachLagFixSettings>();

            Harmony harmony = new Harmony(HarmonyId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            string diagnosticState = DiagnosticLoggingEnabled
                ? "enabled"
                : "disabled";

            Log.Message(
                "[Raider Approach Lag Fix] Loaded. " +
                "Per-scan MarketValue caching is active; " +
                "diagnostic logging is " + diagnosticState + ".");
        }

        /// <summary>
        /// Supplies the name shown in RimWorld's mod-settings list.
        /// </summary>
        public override string SettingsCategory()
        {
            return "Raider Approach Lag Fix";
        }

        /// <summary>
        /// Draws the mod's settings interface.
        /// </summary>
        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            bool diagnosticLogging =
                CurrentSettings.EnableDiagnosticLogging;

            listing.CheckboxLabeled(
                "Enable diagnostic logging",
                ref diagnosticLogging,
                "Collects lightweight steal-scan timing statistics and " +
                "writes a summary to the log approximately every 600 game " +
                "ticks while the relevant raid trigger is active.");

            CurrentSettings.EnableDiagnosticLogging =
                diagnosticLogging;

            listing.Gap();

            listing.Label(
                "The performance optimization remains active regardless of " +
                "this setting.");

            listing.Label(
                "Leave diagnostics disabled during normal play. Enable them " +
                "when testing performance or preparing a bug report.");

            listing.End();
        }
    }
}