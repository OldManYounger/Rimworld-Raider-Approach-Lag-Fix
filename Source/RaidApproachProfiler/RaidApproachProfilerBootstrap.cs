using System.Reflection;
using HarmonyLib;
using Verse;

namespace RaidApproachProfiler
{
    /// <summary>
    /// Initializes the Raid Approach Profiler assembly when RimWorld loads it.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class RaidApproachProfilerBootstrap
    {
        private const string HarmonyId = "OldManYoung.RaiderApproachLagFix";

        /// <summary>
        /// Applies this assembly's Harmony patches and reports successful startup.
        /// </summary>
        static RaidApproachProfilerBootstrap()
        {
            Harmony harmony = new Harmony(HarmonyId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log.Message(
                "[Raider Approach Lag Fix] Loaded with per-scan steal-value caching " +
                "and lightweight diagnostics.");
        }
    }
}