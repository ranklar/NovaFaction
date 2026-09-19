# NEXT — Stop the sim DLL changing on every commit

Written by the planner (Glenn's Claude chat), 2026-09-19, from the item the
Session K health check discovered. Glenn released the code work in this repo
the same day; this is the first, deliberately small, item.

## Scope

1. In `sim/NovaFaction.Sim/NovaFaction.Sim.csproj` set
   `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>`
   so the .NET SDK stops stamping the git commit id into the DLL's
   informational version.
2. Run `powershell -ExecutionPolicy Bypass -File tools\sync-to-unity.ps1`
   once, confirm the content copy reports 9 of 9 JSON files unchanged, and
   commit the rebuilt `client/Assets/Plugins/NovaFaction/NovaFaction.Sim.dll`
   (Git LFS tracks it). Then run the sync a second time and confirm it copies
   nothing.
3. Confirm `SimVersion.Current` did not need to change (no sim logic changed)
   and say so in STATUS.md.
4. If Claude Code is allowed to edit `.claude/settings.json` in this session,
   add the `PowerShell(...)` twins of the Bash deny rules (force-push,
   `reset --hard`, `git clean`, `Remove-Item -Recurse`, `rmdir`, `del`) that
   the Session K entry recommended. If the edit is refused, list the exact
   lines under Glenn's actions instead; do not route around the refusal.

## Acceptance criteria (the audit checks these)

- `dotnet test sim\NovaFaction.Sim.Tests` green; count in STATUS.md.
- After the commit, a fresh `sync-to-unity.ps1` run copies nothing.
- `git log -1 -- client/Assets/Plugins/NovaFaction/NovaFaction.Sim.dll` shows
  the new commit and the DLL is stored through LFS (`git lfs ls-files`).
- No scene, prefab, `.meta` or ProjectSettings file changed.

## After this session

The next item is M2 step 2 (render sim state in Unity). The planner writes
that brief after reading `docs/design.md`'s client architecture; `/next` should
not draft it on its own because it needs Glenn's hands in the Unity editor for
the scene wiring and the planner will split code from editor steps.
