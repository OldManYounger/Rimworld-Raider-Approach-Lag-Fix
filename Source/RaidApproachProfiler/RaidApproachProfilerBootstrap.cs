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
        private const string HarmonyId = "OldManYoung.RaidApproachProfiler";

        /// <summary>
        /// Applies this assembly's Harmony patches and reports successful startup.
        /// </summary>
        static RaidApproachProfilerBootstrap()
        {
            Harmony harmony = new Harmony(HarmonyId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log.Message(
                "[Raid Approach Profiler] Loaded successfully. " +
                "No diagnostic patches are active yet.");
        }
    }
}