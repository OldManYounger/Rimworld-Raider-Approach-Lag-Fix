using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RaidApproachProfiler
{
    // ==================================
    //  STEAL-SCAN LIFETIME PATCH
    // ==================================

    /// <summary>
    /// Opens a temporary MarketValue cache around
    /// StealAIUtility.TotalMarketValueAround.
    ///
    /// The cache exists only for the duration of one synchronous steal scan.
    /// It is cleared after every scan so values are recalculated the next time
    /// the trigger runs.
    /// </summary>
    [HarmonyPatch(
        typeof(StealAIUtility),
        nameof(StealAIUtility.TotalMarketValueAround))]
    internal static class StealMarketValueTimingPatch
    {
        private struct ScanState
        {
            internal bool IsOuterScan;
            internal long StartedAt;
        }

        /// <summary>
        /// Starts the temporary cache before the steal scan executes.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(out ScanState __state)
        {
            __state = new ScanState
            {
                IsOuterScan = StealMarketValueScanCache.EnterScan(),
                StartedAt = Stopwatch.GetTimestamp()
            };
        }

        /// <summary>
        /// Completes timing, records the result, reports accumulated statistics
        /// when appropriate, and clears the temporary cache.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(
            List<Pawn> pawns,
            float __result,
            ScanState __state)
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - __state.StartedAt;
            bool finishedOuterScan = StealMarketValueScanCache.ExitScan();

            if (__state.IsOuterScan && finishedOuterScan)
            {
                int pawnCount = pawns != null ? pawns.Count : 0;

                StealMarketValueScanCache.CompleteScan(
                    elapsedTicks,
                    pawnCount,
                    __result);
            }
        }

        /// <summary>
        /// Ensures that an exception cannot leave the temporary cache active.
        /// The original exception is returned unchanged.
        /// </summary>
        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                StealMarketValueScanCache.AbortScan();
            }

            return __exception;
        }
    }

    // ==================================
    //  GETVALUE CACHE PATCH
    // ==================================

    /// <summary>
    /// Reuses the first completed StealAIUtility.GetValue result for each
    /// individual Thing during the active steal scan.
    /// </summary>
    [HarmonyPatch(
        typeof(StealAIUtility),
        nameof(StealAIUtility.GetValue))]
    internal static class StealMarketValueGetValueCachePatch
    {
        private struct ValueState
        {
            internal bool IsCacheMiss;
            internal long StartedAt;
        }

        /// <summary>
        /// Returns the cached value when the same Thing has already been
        /// evaluated during the current scan.
        ///
        /// Returning false tells Harmony to skip the original GetValue method
        /// for cache hits.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(
            Thing thing,
            ref float __result,
            out ValueState __state)
        {
            __state = default(ValueState);

            // Preserve completely normal GetValue behavior outside the
            // TotalMarketValueAround scan.
            if (!StealMarketValueScanCache.IsActive)
            {
                return true;
            }

            StealMarketValueScanCache.RecordRequest();

            float cachedValue;

            if (StealMarketValueScanCache.TryGetValue(thing, out cachedValue))
            {
                StealMarketValueScanCache.RecordCacheHit();
                __result = cachedValue;

                // Skip the original GetValue calculation.
                return false;
            }

            // This is the first evaluation of this Thing in the current scan.
            __state.IsCacheMiss = true;
            __state.StartedAt = Stopwatch.GetTimestamp();

            return true;
        }

        /// <summary>
        /// Stores the completed result after the first calculation for a Thing.
        /// It also records the time spent performing actual, uncached
        /// calculations.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(
            Thing thing,
            float __result,
            ValueState __state)
        {
            if (!__state.IsCacheMiss || !StealMarketValueScanCache.IsActive)
            {
                return;
            }

            long elapsedTicks = Stopwatch.GetTimestamp() - __state.StartedAt;

            StealMarketValueScanCache.StoreValue(thing, __result);
            StealMarketValueScanCache.RecordCalculation(elapsedTicks);
        }
    }

    // ==================================
    //  CACHE AND DIAGNOSTIC STATE
    // ==================================

    /// <summary>
    /// Owns the temporary per-scan cache and lightweight aggregate diagnostics.
    ///
    /// Thread-static storage prevents cache state from leaking between threads.
    /// RimWorld normally executes this code on its main game thread.
    /// </summary>
    internal static class StealMarketValueScanCache
    {
        private const int ReportIntervalTicks = 600;

        [ThreadStatic]
        private static Dictionary<Thing, float> cachedValues;

        [ThreadStatic]
        private static int scanDepth;

        // Current-scan diagnostic values.
        [ThreadStatic]
        private static long currentRequests;

        [ThreadStatic]
        private static long currentCacheHits;

        [ThreadStatic]
        private static long currentCalculations;

        [ThreadStatic]
        private static long currentCalculationTicks;

        [ThreadStatic]
        private static long currentMaximumCalculationTicks;

        // Accumulated report values.
        private static long reportScanCount;
        private static long reportPawnCount;
        private static long reportScanTicks;
        private static long reportMaximumScanTicks;
        private static long reportRequests;
        private static long reportCacheHits;
        private static long reportCalculations;
        private static long reportCalculationTicks;
        private static long reportMaximumCalculationTicks;
        private static float reportLastNearbyValue;

        private static int lastReportTick = -1;

        /// <summary>
        /// Indicates whether GetValue calls are currently inside a steal scan.
        /// </summary>
        internal static bool IsActive
        {
            get
            {
                return scanDepth > 0 && cachedValues != null;
            }
        }

        /// <summary>
        /// Enters a steal scan and prepares a fresh cache for an outer scan.
        /// Returns true when this is the outermost scan.
        /// </summary>
        internal static bool EnterScan()
        {
            bool isOuterScan = scanDepth == 0;

            if (isOuterScan)
            {
                if (cachedValues == null)
                {
                    cachedValues = new Dictionary<Thing, float>(
                        ReferenceThingComparer.Instance);
                }
                else
                {
                    cachedValues.Clear();
                }

                ResetCurrentScanCounters();
            }

            scanDepth++;

            return isOuterScan;
        }

        /// <summary>
        /// Leaves a steal scan and returns true once the outermost scan ends.
        /// </summary>
        internal static bool ExitScan()
        {
            if (scanDepth > 0)
            {
                scanDepth--;
            }

            return scanDepth == 0;
        }

        /// <summary>
        /// Attempts to obtain the previously calculated value for a Thing.
        /// </summary>
        internal static bool TryGetValue(Thing thing, out float value)
        {
            if (cachedValues != null && thing != null)
            {
                return cachedValues.TryGetValue(thing, out value);
            }

            value = 0f;
            return false;
        }

        /// <summary>
        /// Stores the first completed value for a Thing in the current scan.
        /// </summary>
        internal static void StoreValue(Thing thing, float value)
        {
            if (cachedValues == null || thing == null)
            {
                return;
            }

            cachedValues[thing] = value;
        }

        /// <summary>
        /// Records one request made to StealAIUtility.GetValue.
        /// </summary>
        internal static void RecordRequest()
        {
            currentRequests++;
        }

        /// <summary>
        /// Records a request fulfilled by the temporary cache.
        /// </summary>
        internal static void RecordCacheHit()
        {
            currentCacheHits++;
        }

        /// <summary>
        /// Records one actual GetValue execution and its elapsed time.
        /// </summary>
        internal static void RecordCalculation(long elapsedTicks)
        {
            currentCalculations++;
            currentCalculationTicks += elapsedTicks;

            if (elapsedTicks > currentMaximumCalculationTicks)
            {
                currentMaximumCalculationTicks = elapsedTicks;
            }
        }

        /// <summary>
        /// Adds a completed scan to the reporting window and clears references
        /// retained by its temporary cache.
        /// </summary>
        internal static void CompleteScan(
            long elapsedTicks,
            int pawnCount,
            float nearbyValue)
        {
            reportScanCount++;
            reportPawnCount += pawnCount;
            reportScanTicks += elapsedTicks;
            reportRequests += currentRequests;
            reportCacheHits += currentCacheHits;
            reportCalculations += currentCalculations;
            reportCalculationTicks += currentCalculationTicks;
            reportLastNearbyValue = nearbyValue;

            if (elapsedTicks > reportMaximumScanTicks)
            {
                reportMaximumScanTicks = elapsedTicks;
            }

            if (currentMaximumCalculationTicks >
                reportMaximumCalculationTicks)
            {
                reportMaximumCalculationTicks =
                    currentMaximumCalculationTicks;
            }

            // Clear Thing references immediately after the scan.
            if (cachedValues != null)
            {
                cachedValues.Clear();
            }

            int currentTick = Find.TickManager != null
                ? Find.TickManager.TicksGame
                : 0;

            if (lastReportTick < 0)
            {
                lastReportTick = currentTick;
                return;
            }

            if (currentTick - lastReportTick >= ReportIntervalTicks)
            {
                ReportAndReset(currentTick);
            }
        }

        /// <summary>
        /// Clears the cache after an exceptional scan termination.
        /// </summary>
        internal static void AbortScan()
        {
            scanDepth = 0;
            ResetCurrentScanCounters();

            if (cachedValues != null)
            {
                cachedValues.Clear();
            }
        }

        /// <summary>
        /// Resets counters that belong to one individual scan.
        /// </summary>
        private static void ResetCurrentScanCounters()
        {
            currentRequests = 0;
            currentCacheHits = 0;
            currentCalculations = 0;
            currentCalculationTicks = 0;
            currentMaximumCalculationTicks = 0;
        }

        /// <summary>
        /// Writes the lightweight cache report to the RimWorld log.
        /// </summary>
        private static void ReportAndReset(int currentTick)
        {
            if (reportScanCount <= 0)
            {
                lastReportTick = currentTick;
                return;
            }

            double totalMilliseconds =
                TicksToMilliseconds(reportScanTicks);

            double averageMilliseconds =
                totalMilliseconds / reportScanCount;

            double maximumMilliseconds =
                TicksToMilliseconds(reportMaximumScanTicks);

            double averagePawns =
                (double)reportPawnCount / reportScanCount;

            double averageRequests =
                (double)reportRequests / reportScanCount;

            double averageCalculations =
                (double)reportCalculations / reportScanCount;

            double hitPercent = reportRequests > 0
                ? (double)reportCacheHits / reportRequests * 100.0
                : 0.0;

            double calculationMilliseconds =
                TicksToMilliseconds(reportCalculationTicks);

            double calculationPercent = reportScanTicks > 0
                ? (double)reportCalculationTicks / reportScanTicks * 100.0
                : 0.0;

            double maximumSingleCalculationMilliseconds =
                TicksToMilliseconds(reportMaximumCalculationTicks);

            Log.Message(
                "[Raid Approach Profiler] Cached steal-scan summary:\n" +
                "calls=" + reportScanCount +
                ", totalMs=" + totalMilliseconds.ToString("F3") +
                ", averageMs=" + averageMilliseconds.ToString("F3") +
                ", maximumMs=" + maximumMilliseconds.ToString("F3") +
                "\n" +
                "averagePawns=" + averagePawns.ToString("F1") +
                ", getValueRequests=" + reportRequests +
                ", averageRequestsPerScan=" +
                averageRequests.ToString("F1") +
                "\n" +
                "cacheHits=" + reportCacheHits +
                ", actualCalculations=" + reportCalculations +
                ", averageCalculationsPerScan=" +
                averageCalculations.ToString("F1") +
                ", cacheHitPercent=" + hitPercent.ToString("F1") +
                "\n" +
                "actualCalculationMs=" +
                calculationMilliseconds.ToString("F3") +
                ", calculationPercent=" +
                calculationPercent.ToString("F1") +
                ", maximumSingleCalculationMs=" +
                maximumSingleCalculationMilliseconds.ToString("F4") +
                ", lastNearbyValue=" +
                reportLastNearbyValue.ToString("F1"));

            ResetReportCounters();
            lastReportTick = currentTick;
        }

        /// <summary>
        /// Resets the counters accumulated across the reporting window.
        /// </summary>
        private static void ResetReportCounters()
        {
            reportScanCount = 0;
            reportPawnCount = 0;
            reportScanTicks = 0;
            reportMaximumScanTicks = 0;
            reportRequests = 0;
            reportCacheHits = 0;
            reportCalculations = 0;
            reportCalculationTicks = 0;
            reportMaximumCalculationTicks = 0;
            reportLastNearbyValue = 0f;
        }

        /// <summary>
        /// Converts Stopwatch ticks to milliseconds.
        /// </summary>
        private static double TicksToMilliseconds(long elapsedTicks)
        {
            return elapsedTicks * 1000.0 / Stopwatch.Frequency;
        }
    }

    // ==================================
    //  REFERENCE COMPARER
    // ==================================

    /// <summary>
    /// Compares Thing instances by object identity.
    ///
    /// This ensures that two separate map objects can never share a cached
    /// result merely because a type implements value-based equality.
    /// </summary>
    internal sealed class ReferenceThingComparer : IEqualityComparer<Thing>
    {
        internal static readonly ReferenceThingComparer Instance =
            new ReferenceThingComparer();

        private ReferenceThingComparer()
        {
        }

        /// <summary>
        /// Returns true only when both values reference the same Thing object.
        /// </summary>
        public bool Equals(Thing x, Thing y)
        {
            return ReferenceEquals(x, y);
        }

        /// <summary>
        /// Returns an identity-based hash code for a Thing object.
        /// </summary>
        public int GetHashCode(Thing obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}