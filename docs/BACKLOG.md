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
2. **M2 step 2 — render sim state in Unity.** Units, structures, HUD (gold,
   score, timer) driven from the sim's state each tick; no rules in the
   client. **Blocked on the planner's brief** (and step 1).
3. **M2 step 3 — deploy input.** Card hand and drop-to-deploy on the phone,
   sent to the sim as inputs. **Blocked on the planner's brief.**
4. **M2 step 4 — a bot match on the phone**, judged by hand for pace: is 13
   cards a side in 3:00 too few? Base income is the first lever; re-run the
   harness targets after any change. **Blocked on Glenn playing it.**
5. **Bot upgrade — individual card valuation** (after the vertical slice).
6. **Open items from docs/design.md:** studio name and Android package id,
   the fantasy roster of 16 units and 2 leaders. **Blocked on Glenn.**

## Discovered

(Claude Code adds items here when work turns them up.)

- **The DLL is only reproducible on one machine and one path.** Deterministic builds embed
  absolute source and PDB paths, so a clone at a different path, or a second machine, builds a
  one-off different `NovaFaction.Sim.dll` and the sync reports one extra "copied" line. Harmless
  today (Glenn builds in one place). It would matter if the DLL were ever built in CI or by a
  second developer; the lever is `<ContinuousIntegrationBuild>` with `<PathMap>`. Found while
  fixing the version stamp, 2026-09-19.

## Done

- 2026-09-19 — **Session L: DLL version stamp fix.** The sim DLL no longer changes after a commit.
  Two SDK properties, not one: the informational version *and* the SourceLink sha in the PDB, whose
  checksum lives in the DLL. Verified byte-identical across commits and a clean rebuild.
- 2026-09-19 — **Session K: install and validate the working loop.** `.claude/` kit installed,
  Stop hook checked, health check recorded in STATUS.md.
