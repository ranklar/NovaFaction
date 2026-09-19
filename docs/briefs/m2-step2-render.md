# NEXT — M2 step 2: render sim state in Unity

Written by the planner (Glenn's Claude chat), 2026-09-19, after reading
`docs/design.md`, `CLAUDE.md` and the existing client scripts. Names and paths
below were taken from the source; anything marked "verify" should be checked
against the code before relying on it.

## Why

M1 finished the simulation and M2 step 1 proves it produces the same hash on
the phone as on the PC. Nobody has ever *seen* a match. This step draws one:
units, structures and a HUD reading gold, score and the clock, all pulled out
of `MatchState` every tick. It is about the data path and the update loop, not
about looking good — primitives are fine. Step 3 (deploy input) needs
something to deploy into, and the open item in `docs/design.md` — "does the
match feel slow or empty, is 13 cards a side too few?" — cannot be judged
until a match is visible.

## Scope

Four new C# files under `client/Assets/Scripts/`. No sim or content changes
are expected.

1. **`SimView.cs` — the only place the client turns sim numbers into floats.**
   Sim world space is X right, Y up (`Map/CellCoord.cs`: row 0 is player 0's
   edge, the last row is player 1's). `twolane` is 18 x 32 cells at `cellSize`
   1. Render on the XZ plane with a top-down camera: sim `(X, Y)` becomes Unity
   `(X, 0, Y)`. Provide `static float ToFloat(Fix)`, `static Vector3
   ToWorld(FixVector2)`, `static Vector3 CellCentre(Grid grid, CellCoord cell)`
   built on `Grid.CellToWorld`, and `static string ClockText(int
   ticksRemaining, int ticksPerSecond)` giving `m:ss`. Nothing else in the
   client may call `Fix.ToFloat()` directly.

2. **`MatchRunner.cs` — owns the match and decides *when* it advances, never
   *what* happens.** On `Start()`: `ContentLoader.Load()`,
   `content.GetMap(MapId)`, build the same deck `SimSmokeTest.cs` builds, then
   `new MatchSetup(...).WithBot(0, ...).WithBot(1, ...)` and `new
   Simulation(setup, Seed)`. Inspector fields `Seed = 7`, `MapId = "twolane"`,
   `Bot0 = "balanced"`, `Bot1 = "balanced"`, matching SimSmokeTest so the two
   are comparable. In `Update()`, run a fixed-step accumulator: add
   `Time.deltaTime`; while it is at least `1f / State.Rules.TicksPerSecond`
   (20/s) and `!sim.IsEnded`, call the parameterless `sim.Tick()` (it collects
   both bots' commands itself, as `HeadlessMatch.Run` does) and subtract one
   step. Cap catch-up at 8 ticks per frame so a hitch cannot freeze the app;
   drop the surplus. Expose `public Simulation Sim` and `public MatchState
   State`. Set `Application.targetFrameRate = 60` and `Screen.sleepTimeout =
   NeverSleep`.

3. **`MatchView.cs` — draws the state, keeps no state of its own beyond the
   GameObjects.** It builds its visuals at runtime with
   `GameObject.CreatePrimitive`; it must not require a prefab. On `Start()`:
   a ground quad sized `Grid.Width x Grid.Height`, a dark cube for every
   blocked terrain cell, a cube per structure scaled to its footprint, and a
   marker per mine and chest spawn. Then in `LateUpdate()`, reconcile against
   the live state:
   - **Units** — `Dictionary<int, GameObject>` keyed by `Unit.Id` (ids never
     repeat). Create a capsule for an unseen id, move it to
     `SimView.ToWorld(unit.Position)`, raise it on Y when flying, colour by
     owner, destroy the object for any id no longer in `state.Units`. Show HP
     as a vertical scale on a thin child cube.
   - **Structures** — index order, `state.Structures`. Same HP bar; when
     destroyed, grey out or hide. Read the flag; never recompute it.
   - **Projectiles** — small spheres keyed by id, same create/move/destroy.
   - **Mines** — colour by owner (-1 neutral). **Chests** — shown only while
     present.
   - **Pending spawns** — optional ghost marker; skip if it costs time.

   Reconciling by id rather than list position is the point: the unit list is
   compacted and positions shift when a unit dies.

4. **`MatchHud.cs` — the HUD, drawn with `OnGUI`, as `SimSmokeTest.cs`
   already does.** This deliberately avoids a uGUI Canvas, because a Canvas
   would mean editing a scene, which the repo forbids Claude Code from doing.
   Show for both players: gold, score, the clock from
   `state.ClockRemainingTicks`, the phase (Regulation / SuddenDeath / Ended),
   unit counts, and once ended the winner, end reason and tie-break rule.
   Scale the font from `Screen.height` so it is readable on a phone. Every
   number is read straight off the state; the HUD computes nothing but the
   `m:ss` string.

## Rules that matter here

- **No game rules in the client.** The client reads and draws. It may read
  `MatchState.Units, Structures, Projectiles, PendingSpawns, PendingSpells,
  SpellZones, Mines, Chests, Winner/EndReason`, and each player's `Cards`,
  `Gold` and `GoldFromMap`. No comparing gold to a card cost, no deciding
  whether something is in range, no counting damage. There is exactly one call
  site of `sim.Tick()` in the whole client.
- **Determinism.** Nothing in `sim/` should change. If something genuinely
  must (a missing read-only accessor, say), that is a sim change: add xunit
  tests, bump `SimVersion.Current`, re-run `tools\sync-to-unity.ps1`, and say
  so in STATUS.md. Floats are allowed in `client/` for rendering only; the
  no-float guard tests must still pass.
- **Claude Code edits C# and JSON only.** No `.unity`, `.prefab`, `.meta` or
  `ProjectSettings`. Unity writes the `.meta` files for the new scripts itself
  when Glenn next focuses the editor; those get committed.
- Windows, PowerShell only. Commands: `dotnet test sim\NovaFaction.Sim.Tests`,
  and `powershell -ExecutionPolicy Bypass -File tools\sync-to-unity.ps1` only
  if `sim/` or `content/` changed.

## Glenn's editor steps

These happen after Claude Code says the scripts are in, and they are the whole
of his part. Unity is already open on `C:\dev\NovaFaction\client`.

1. Click the Unity window and wait for the spinner at the bottom right to
   stop; it is compiling the new scripts. If a red error appears in the
   Console tab, stop and paste it to the planner.
2. In the **Project** panel (bottom left) open `Assets`, then `Scenes`.
   Right-click the empty space on the right, choose **Create > Scene**, and
   name it `Match`.
3. Double-click `Match` to open it.
4. In the **Hierarchy** panel (top left) right-click the empty space, choose
   **Create Empty**, and rename the new object `Match`.
5. With `Match` selected, in the **Inspector** on the right click **Add
   Component**, type `MatchRunner`, press Enter. Repeat for `MatchView`, then
   `MatchHud`, in that order.
6. In the Hierarchy click **Main Camera**. In the Inspector change
   **Projection** from `Perspective` to `Orthographic`, set **Size** to `16`,
   **Position** to X `9`, Y `20`, Z `16`, and **Rotation** to X `90`, Y `0`,
   Z `0`. That looks straight down and fits the 18 x 32 map on a portrait
   phone screen.
7. Press **Ctrl+S** to save the scene.
8. Menu bar: **File > Build Profiles** (or **Build Settings**). Make sure
   `Scenes/Match` is in the scene list and ticked, and drag it above
   `SampleScene` if that is listed. Close the window.
9. Menu bar: **Edit > Project Settings > Graphics**. Scroll to **Always
   Included Shaders**, click **+** twice, and set the two new slots to
   `Universal Render Pipeline/Lit` and `Universal Render Pipeline/Unlit`.
   Without this the primitives can come out pink in a phone build even though
   they look right in the editor. Close the window.
10. Press **Play** at the top of the editor and watch a match. Press Play
    again to stop.
11. When it looks right in the editor, build and install to the phone the same
    way as SimSmokeTest, and watch a match there.

## Acceptance criteria (the audit checks these)

Verifiable by running commands:

1. `dotnet test sim\NovaFaction.Sim.Tests` — 889 or more tests, all green,
   including the no-float guard.
2. `git diff --stat` touches only `client/Assets/Scripts/*.cs` (plus the
   `.meta` files Unity generated for them) and the docs. No `.unity`,
   `.prefab`, `ProjectSettings/` or `content/` file changed. No file under
   `sim/` changed; if one did, STATUS.md says why and `SimVersion.Current`
   was bumped.
3. `powershell -ExecutionPolicy Bypass -File tools\sync-to-unity.ps1` reports
   the DLL and all content files unchanged.
4. The harness reference match still prints `0x52F44A49D82FFCDA` for seed 7,
   balanced vs balanced, on twolane.
5. Searching `client/Assets/Scripts/` finds `sim.Tick(` exactly once (in
   `MatchRunner.cs`) and `ToFloat(` only inside `SimView.cs`.
6. Reading the four files: no client file compares gold to a cost, or computes
   damage, range, income, capture progress or a winner. Every displayed number
   traces to a property on the sim's state types.

Only Glenn can confirm these, by looking:

7. In the editor's Game view a match plays start to finish: units appear after
   a bot deploys, walk their lane, fight, and vanish when they die; structures
   lose HP and grey out when destroyed.
8. The clock counts down from 3:00 and stops, gold rises and drops, and the
   score climbs when a structure takes damage.
9. The same is true in the phone build, at a frame rate that does not feel
   broken.

## Out of scope

Deliberately not in this step: deploy input of any kind; the card hand, next
card or gold-cost UI; PvP or networking; real art, sprites, models, animation,
particles or sound; a movable camera; menus or a match-end screen beyond the
HUD's winner line. Deploy input is M2 step 3; playing and judging the pace is
M2 step 4.

## After this session

Write the STATUS.md entry newest-first: what was added, the test count and
duration, the audit verdict, and under **Glenn's actions** the numbered editor
steps above, unchanged. Mark the backlog item done and leave deploy input as
the next brief for the planner. If drawing the state turned up something the
client cannot reach without a sim change, do not make that change here — add
it under **Discovered** in `docs/BACKLOG.md` so the step-3 brief can carry it.
