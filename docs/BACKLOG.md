# Backlog

Ordered. The planner (Glenn's Claude chat) and Claude Code both edit this
file; Glenn only has to read it.

## Released

Glenn released the code work in this repo on 2026-09-19 ("go forth"). Items
marked **blocked on Glenn** need his hands (Unity editor, phone) or his
judgement; the planner writes the M2 briefs so code steps and editor steps are
kept apart. `/next` does not draft M2 steps on its own.

## Queue

1. **M2 step 1 — determinism across devices.** Glenn runs the SimSmokeTest
   build on the phone and confirms the on-screen hash equals the harness's
   `0x52F44A49D82FFCDA` for seed 7. **Blocked on Glenn.**
2. **DLL version stamp fix** (from Discovered). In `docs/NEXT.md` (planner,
   2026-09-19).
3. **M2 step 2 — render sim state in Unity.** Units, structures, HUD (gold,
   score, timer) driven from the sim's state each tick; no rules in the
   client. **Blocked on the planner's brief** (and step 1).
4. **M2 step 3 — deploy input.** Card hand and drop-to-deploy on the phone,
   sent to the sim as inputs. **Blocked on the planner's brief.**
5. **M2 step 4 — a bot match on the phone**, judged by hand for pace: is 13
   cards a side in 3:00 too few? Base income is the first lever; re-run the
   harness targets after any change. **Blocked on Glenn playing it.**
6. **Bot upgrade — individual card valuation** (after the vertical slice).
7. **Open items from docs/design.md:** studio name and Android package id,
   the fantasy roster of 16 units and 2 leaders. **Blocked on Glenn.**

## Discovered

(Claude Code adds items here when work turns them up.)

- **Stop the sim DLL changing on every commit.** The .NET SDK stamps the git commit ID into
  `NovaFaction.Sim.dll` (`AssemblyInformationalVersion` `1.0.0+<sha>`), so `sync-to-unity.ps1`
  produces a "new" DLL after any commit even when the code hasn't changed. Fix: set
  `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>`
  in `sim/NovaFaction.Sim/NovaFaction.Sim.csproj`, then sync once and commit the DLL. Small;
  the planner can release it.

## Done

- 2026-09-19 — **Session K: install and validate the working loop.** `.claude/` kit installed,
  Stop hook checked, health check recorded in STATUS.md.
