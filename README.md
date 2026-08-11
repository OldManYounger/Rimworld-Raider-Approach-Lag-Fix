# Raider Approach Lag Fix

An experimental performance mod for RimWorld 1.6.

## Problem

While hostile raiders approach a colony, RimWorld periodically checks whether
valuable objects are nearby so that the raid can transition into stealing.

The vanilla scan can calculate MarketValue thousands of times for only a few
hundred distinct map objects. Trees and stone chunks are especially expensive
in heavily modded games because their MarketValue calculation can search the
loaded recipe database.

This can produce periodic main-thread stalls during the approach phase.

## Fix

The mod temporarily caches `StealAIUtility.GetValue` results during one
`StealAIUtility.TotalMarketValueAround` scan.

The cache:

- Uses individual Thing instances as keys.
- Exists only during the synchronous scan.
- Is cleared immediately after the scan.
- Does not persist between game ticks.
- Does not alter the scan interval, radius, or stealing threshold.

## Initial results

In a heavily modded test:

- Average scan time fell from approximately 29–43 ms to 2.5–3.8 ms.
- Maximum scan time fell from approximately 41–50 ms to 3.1–4.4 ms.
- Approximately 93–96% of repeated value requests were served from the cache.
- Raiders continued to detect valuable items and transitioned into stealing.

## Requirements

- RimWorld 1.6
- Harmony

## Development status

This is an alpha implementation. It currently includes lightweight diagnostic
logging while compatibility and behavior are validated.

## Building

The project targets .NET Framework 4.7.2.

Local references are required for:

- RimWorld `Assembly-CSharp.dll`
- Unity `UnityEngine.CoreModule.dll`
- Harmony `0Harmony.dll`

Game and Harmony binaries are intentionally not included in this repository.

## Disclaimer

This is unofficial community-created content and is not endorsed by Ludeon
Studios.