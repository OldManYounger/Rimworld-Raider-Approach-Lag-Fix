# Vanilla Wealth Call Map

## Environment

- RimWorld version: 1.6.4871 rev590
- Assembly: Assembly-CSharp.dll
- Mod purpose: Investigate MarketValue and Lord spikes during hostile raid approach
- Investigation date: 2026-08-11

## Questions

1. What causes a map wealth recount?
2. Does the raid-approach Lord request colony wealth?
3. Are individual Thing MarketValue stats being calculated instead?
4. Which collections contain the affected trees and chunks?
5. How often are the calculations allowed to repeat?
6. Which caller stops running when raiders begin engaging?

## WealthWatcher

### HealthTotal callers

- Direct caller: Verse.AI.Group.Lord.Init()
- Result stored in: Lord.initialColonyHealthTotal
- Stored value read by: Trigger_FractionColonyDamageTaken.ActivateOn()
- Lord.Init appears to be a one-time Lord initialization operation.
- HealthTotal can trigger a complete wealth recount if the cache is stale.
- This may contribute to an initial raid/Lord hitch.
- It does not currently explain repeated approach-phase freezes.

### WealthTotal callers

- WealthTotal has 13 direct callers.
- The only raid-specific caller is RaidStrategyWorker.MakeLords().
- No LordTick, LordJobTick, LordToilTick, or assault-toil method directly requests WealthTotal.
- RaidStrategyWorker.MakeLords is associated with initial raid/Lord setup.
- WealthTotal does not presently explain repeated freezes after raiders are already spawned.
- Map-wide WealthWatcher recounts are now a secondary candidate.
- Per-Thing MarketValue requests remain a primary candidate.

### Lord.LordTick

LordTick contains no direct wealth or MarketValue calculation.

Recurring branches:

1. curJob.LordJobTick()
2. curLordToil.LordToilTick()
3. CheckTransitionOnSignal(TriggerSignal.ForTick)

Lord.Init() runs only when initialized is false and captures the initial
colony health. It cannot by itself explain recurring approach freezes.

The 60-tick world-pawn validation does not access wealth or MarketValue.

## Assault Lord state graph

### Recurring Lord branches

- LordToil.LordToilTick() is empty.
- LordToil_AssaultColony does not override LordToilTick().
- LordJob_AssaultColony does not override LordJobTick().
- LordToil_AssaultColony.UpdateAllDuties() only assigns AssaultColony duties
  and interrupts pawns whose duty must change.
- None of these methods directly access wealth or MarketValue.

### State graph triggers

LordJob_AssaultColony.CreateGraph() creates transitions containing:

- Trigger_TicksPassed
- Trigger_FractionColonyDamageTaken
- Trigger_KidnapVictimPresent
- Trigger_HighValueThingsAround
- Trigger_GameEnding
- Trigger_BecameNonHostileToPlayer

Lord.LordTick() calls CheckTransitionOnSignal(ForTick) every tick.

### Leading candidate

Trigger_HighValueThingsAround controls the transition from assault to stealing.
It may scan potential items and request individual MarketValue stats.

Possible call chain:

LordTick
→ CheckTransitionOnSignal
→ Trigger_HighValueThingsAround
→ item search
→ Thing.MarketValue

### Fields

- MinCountInterval:
- lastCountTick:
- cachedTerrainMarketValue:
- tmpThings:

### RecountIfNeeded

- Recount condition:
- Direct callers:
- Indirect callers:
- Notes:

### ForceRecount

- Collections processed:
- Methods called:
- Direct callers:
- Notes:

### CalculateWealthItems

- Collection iterated:
- Filters applied:
- MarketValue access:
- Treatment of chunks:
- Treatment of trees:
- Notes:

### CalculateWealthFloors

- Notes:

### WealthItemsFilter

- Accepted categories:
- Rejected categories:
- Notes:

## MarketValue

### Thing.MarketValue

- Implementation:
- Immediate downstream method:
- Relevant callers:

### StatWorker_MarketValue.GetValueUnfinalized

- Calculation path:
- Stat parts involved:
- Caching behavior:
- Notes:

## Raider Lord

### LordToil_AssaultColony.UpdateAllDuties

- Methods called:
- Duty assigned:
- Wealth access:
- MarketValue access:
- Notes:

## Working Call Chain

Unknown.