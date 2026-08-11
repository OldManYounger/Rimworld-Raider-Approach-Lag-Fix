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
    /// Diagnostic timing is performed only when enabled in mod settings.
    /// </summary>
    [HarmonyPatch(
        typeof(StealAIUtility),
        nameof(StealAIUtility.TotalMarketValueAround))]
    internal static class StealMarketValueScanPatch
    {
        private struct ScanState
        {
            internal bool IsOuterScan;
            internal bool DiagnosticsEnabled;
            internal long StartedAt;
        }

        /// <summary>
        /// Starts the temporary cache and conditionally begins scan timing.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(out ScanState __state)
        {
            bool diagnosticsEnabled;

            bool isOuterScan =
                StealMarketValueScanCache.EnterScan(
                    out diagnosticsEnabled);

            __state = new ScanState
            {
                IsOuterScan = isOuterScan,
                DiagnosticsEnabled = diagnosticsEnabled,
                StartedAt = diagnosticsEnabled
                    ? Stopwatch.GetTimestamp()
                    : 0
            };
        }

        /// <summary>
        /// Completes the scan, clears its cache, and records diagnostics when
        /// the corresponding mod option is enabled.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(
            List<Pawn> pawns,
            float __result,
            ScanState __state)
        {
            long elapsedTicks = __state.DiagnosticsEnabled
                ? Stopwatch.GetTimestamp() - __state.StartedAt
                : 0;

            bool finishedOuterScan =
                StealMarketValueScanCache.ExitScan();

            if (__state.IsOuterScan && finishedOuterScan)
            {
                int pawnCount = pawns != null
                    ? pawns.Count
                    : 0;

                StealMarketValueScanCache.CompleteScan(
                    elapsedTicks,
                    pawnCount,
                    __result,
                    __state.DiagnosticsEnabled);
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
    /// Reuses the first completed StealAIUtility.GetValue result for each Thing
    /// during one TotalMarketValueAround scan.
    /// </summary>
    [HarmonyPatch(
        typeof(StealAIUtility),
        nameof(StealAIUtility.GetValue))]
    internal static class StealMarketValueGetValueCachePatch
    {
        private struct ValueState
        {
            internal bool IsCacheMiss;
            internal bool MeasureCalculation;
            internal long StartedAt;
        }

        /// <summary>
        /// Returns a cached result when the same Thing has already been
        /// evaluated during the current scan.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(
            Thing thing,
            ref float __result,
            out ValueState __state)
        {
            __state = default(ValueState);

            // Preserve normal GetValue behavior outside a steal scan.
            if (!StealMarketValueScanCache.IsActive)
            {
                return true;
            }

            bool diagnosticsEnabled =
                StealMarketValueScanCache.DiagnosticsEnabledForCurrentScan;

            if (diagnosticsEnabled)
            {
                StealMarketValueScanCache.RecordRequest();
            }

            float cachedValue;

            if (StealMarketValueScanCache.TryGetValue(
                    thing,
                    out cachedValue))
            {
                if (diagnosticsEnabled)
                {
                    StealMarketValueScanCache.RecordCacheHit();
                }

                __result = cachedValue;

                // Skip the original GetValue calculation for this cache hit.
                return false;
            }

            // The original method must run for the first request.
            __state.IsCacheMiss = true;
            __state.MeasureCalculation = diagnosticsEnabled;
            __state.StartedAt = diagnosticsEnabled
                ? Stopwatch.GetTimestamp()
                : 0;

            return true;
        }

        /// <summary>
        /// Stores the first completed result for a Thing and conditionally
        /// records the calculation time.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(
            Thing thing,
            float __result,
            ValueState __state)
        {
            if (!__state.IsCacheMiss ||
                !StealMarketValueScanCache.IsActive)
            {
                return;
            }

            // Caching remains active regardless of diagnostic settings.
            StealMarketValueScanCache.StoreValue(thing, __result);

            if (__state.MeasureCalculation)
            {
                long elapsedTicks =
                    Stopwatch.GetTimestamp() - __state.StartedAt;

                StealMarketValueScanCache.RecordCalculation(
                    elapsedTicks);
            }
        }
    }

    // ==================================
    //  CACHE AND OPTIONAL DIAGNOSTICS
    // ==================================

    /// <summary>
    /// Owns the temporary cache and optional diagnostic counters.
    /// </summary>
    internal static class StealMarketValueScanCache
    {
        private const int ReportIntervalTicks = 600;

        [ThreadStatic]
        private static Dictionary<Thing, float> cachedValues;

        [ThreadStatic]
        private static int scanDepth;

        [ThreadStatic]
        private static bool diagnosticsEnabledForCurrentScan;

        // Current-scan diagnostic counters.
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

        // Counters accumulated across one reporting window.
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
        /// Indicates whether a steal scan currently owns a temporary cache.
        /// </summary>
        internal static bool IsActive
        {
            get
            {
                return scanDepth > 0 && cachedValues != null;
            }
        }

        /// <summary>
        /// Indicates whether diagnostics were enabled when the current
        /// outermost scan began.
        /// </summary>
        internal static bool DiagnosticsEnabledForCurrentScan
        {
            get
            {
                return diagnosticsEnabledForCurrentScan;
            }
        }

        /// <summary>
        /// Enters a steal scan and prepares a fresh cache for its outermost
        /// invocation.
        /// </summary>
        internal static bool EnterScan(
            out bool diagnosticsEnabled)
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

                diagnosticsEnabledForCurrentScan =
                    RaiderApproachLagFixMod.DiagnosticLoggingEnabled;

                ResetCurrentScanCounters();

                if (!diagnosticsEnabledForCurrentScan)
                {
                    // Prevent statistics from an earlier enabled session
                    // from appearing after diagnostics are re-enabled.
                    ResetReportCounters();
                    lastReportTick = -1;
                }
            }

            scanDepth++;
            diagnosticsEnabled =
                diagnosticsEnabledForCurrentScan;

            return isOuterScan;
        }

        /// <summary>
        /// Leaves a steal scan and returns true once its outermost invocation
        /// has ended.
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
        /// Attempts to retrieve the previously calculated value for a Thing.
        /// </summary>
        internal static bool TryGetValue(
            Thing thing,
            out float value)
        {
            if (cachedValues != null && thing != null)
            {
                return cachedValues.TryGetValue(
                    thing,
                    out value);
            }

            value = 0f;
            return false;
        }

        /// <summary>
        /// Stores the first completed value for a Thing in this scan.
        /// </summary>
        internal static void StoreValue(
            Thing thing,
            float value)
        {
            if (cachedValues == null || thing == null)
            {
                return;
            }

            cachedValues[thing] = value;
        }

        /// <summary>
        /// Records one GetValue request.
        /// </summary>
        internal static void RecordRequest()
        {
            currentRequests++;
        }

        /// <summary>
        /// Records one GetValue request fulfilled by the cache.
        /// </summary>
        internal static void RecordCacheHit()
        {
            currentCacheHits++;
        }

        /// <summary>
        /// Records one actual GetValue execution.
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
        /// Finishes one scan, clears its temporary references, and records
        /// optional timing statistics.
        /// </summary>
        internal static void CompleteScan(
            long elapsedTicks,
            int pawnCount,
            float nearbyValue,
            bool diagnosticsEnabled)
        {
            if (diagnosticsEnabled)
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
            }

            // Do not retain map objects after the synchronous scan ends.
            if (cachedValues != null)
            {
                cachedValues.Clear();
            }

            diagnosticsEnabledForCurrentScan = false;

            if (!diagnosticsEnabled)
            {
                return;
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
        /// Clears scan state after an exceptional termination.
        /// </summary>
        internal static void AbortScan()
        {
            scanDepth = 0;
            diagnosticsEnabledForCurrentScan = false;
            ResetCurrentScanCounters();

            if (cachedValues != null)
            {
                cachedValues.Clear();
            }
        }

        /// <summary>
        /// Resets counters belonging to one individual scan.
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
        /// Writes one accumulated diagnostic report.
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
                ? (double)reportCalculationTicks /
                  reportScanTicks * 100.0
                : 0.0;

            double maximumSingleCalculationMilliseconds =
                TicksToMilliseconds(
                    reportMaximumCalculationTicks);

            Log.Message(
                "[Raider Approach Lag Fix] Cached steal-scan summary:\n" +
                "calls=" + reportScanCount +
                ", totalMs=" +
                totalMilliseconds.ToString("F3") +
                ", averageMs=" +
                averageMilliseconds.ToString("F3") +
                ", maximumMs=" +
                maximumMilliseconds.ToString("F3") +
                "\n" +
                "averagePawns=" +
                averagePawns.ToString("F1") +
                ", getValueRequests=" + reportRequests +
                ", averageRequestsPerScan=" +
                averageRequests.ToString("F1") +
                "\n" +
                "cacheHits=" + reportCacheHits +
                ", actualCalculations=" +
                reportCalculations +
                ", averageCalculationsPerScan=" +
                averageCalculations.ToString("F1") +
                ", cacheHitPercent=" +
                hitPercent.ToString("F1") +
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
        /// Resets the accumulated reporting window.
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
            return elapsedTicks * 1000.0 /
                   Stopwatch.Frequency;
        }
    }

    // ==================================
    //  REFERENCE COMPARER
    // ==================================

    /// <summary>
    /// Compares Thing instances by object identity.
    /// </summary>
    internal sealed class ReferenceThingComparer :
        IEqualityComparer<Thing>
    {
        internal static readonly ReferenceThingComparer Instance =
            new ReferenceThingComparer();

        private ReferenceThingComparer()
        {
        }

        /// <summary>
        /// Returns true only when both values reference the same Thing.
        /// </summary>
        public bool Equals(Thing x, Thing y)
        {
            return ReferenceEquals(x, y);
        }

        /// <summary>
        /// Returns an identity-based hash code.
        /// </summary>
        public int GetHashCode(Thing obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}