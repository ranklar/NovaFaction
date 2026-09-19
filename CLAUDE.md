# NovaFaction — instructions for Claude Code

Read docs/design.md before any task. It is the source of truth for game rules and architecture.

## Who you're working with
Glenn is the sole developer. He is an experienced program manager, not a current programmer
(C++ ~15 years ago). Explain changes in plain language, summarize what changed and exactly how to
verify it. He uses Windows 11 only — give PowerShell commands, never bash/WSL/Linux.

## How work flows

Glenn's only command is `/next`; the planner (Glenn's Claude design chat) can
start the same thing without him by dropping `docs\RUN.md`, which the
`novafaction-inbox` routine in the Claude desktop app picks up on its schedule.
Everything else lives in files:

- `docs/BACKLOG.md` — the ordered queue; items marked blocked wait for Glenn or
  for the planner's brief.
- `docs/NEXT.md` — exactly one brief: the piece of work to do now.
- `docs/STATUS.md` — newest entry first: what was done, test count, audit
  verdict, **Glenn's actions**, what is next. This is what Glenn and the
  planner read; nobody pastes summaries anywhere.
- `docs/runs/` — the inbox routine's receipts (`<timestamp>/DONE.md`),
  heartbeat and log; gitignored.
- `.claude/skills/next` runs the loop; `.claude/skills/audit` is an independent
  check by a fresh subagent; `.claude/hooks/stop-tests.ps1` runs the test suite
  whenever a turn ends and refuses to let the session finish on red tests.
- `.claude/settings.json` holds the permission rules. Deny rules are the real
  guardrails: `.env` is never read, no force-push, no recursive delete, plus
  this repo's own protected files.

Never ask Glenn to paste text, copy files, or run a command for you. If
something needs him, put it under **Glenn's actions** in STATUS.md and do
everything around it.

## Repo layout
- client/   Unity 6.3 LTS project (URP, Android first). Rendering, input, UI only.
- sim/      NovaFaction.Sim (netstandard2.1 class library) and NovaFaction.Sim.Tests (xunit).
- server/   ASP.NET Core service (created in M3).
- content/  JSON data: units, factions, maps, missions.
- docs/     Design doc and notes.
- tools/    NovaFaction.Harness: .NET 10 console app for headless batches, round robins, determinism and replay
            checks. May use System.Text.Json and Parallel (the sim may not). Output goes to tools/out/ (ignored by git).
            sync-to-unity.ps1 pushes the built sim and the content into the Unity client (see "Getting the sim into
            Unity").
- builds/   Local build output. Ignored by git.

## Sim rules (sim/NovaFaction.Sim) — determinism is non-negotiable
- No float or double anywhere. Use the project's fixed-point type for all numbers that vary.
- No System.Random, DateTime, Stopwatch, Environment ticks, or hash-code-dependent ordering.
  All randomness goes through the seeded sim RNG. Iterate collections in deterministic order
  (lists, sorted keys) — never rely on Dictionary or HashSet iteration order.
- Fixed tick rate (20 ticks/s). The sim advances only via explicit Tick() calls with the tick's inputs.
- No Unity references, no NuGet dependencies. Target netstandard2.1, LangVersion 9.0
  (Unity 6 compiles this code; no C# 10+ features such as file-scoped namespaces, record structs,
  global usings, required members).
- All balance numbers (stats, costs, income, timers) come from data in content/, never hardcoded.
- Bump SimVersion.Current (sim/NovaFaction.Sim/SimVersion.cs) whenever sim logic changes in a way that can change a
  match or its hashes; replays only re-run on the same sim version. Raise rulesVersion in content/rules.json whenever a
  rules value changes.
- Every feature ships with xunit tests. Include a determinism test where relevant: same seed and
  inputs twice must produce identical per-tick state hashes.
- Run before finishing any sim task:  dotnet test sim\NovaFaction.Sim.Tests

## Client rules (client/)
- No game rules in Unity code. The client renders sim state and sends player inputs.
- Edit C# scripts and JSON only. Do not hand-edit scenes, prefabs, .meta files, or
  ProjectSettings unless explicitly asked — Glenn does those in the Unity editor with guidance.
- Never touch client/Library, client/Temp, client/Logs.

### Getting the sim into Unity
Unity does not compile sim/ and does not read content/. It uses a prebuilt DLL and its own copy of the JSON, so
after ANY change to sim/ or content/ run:

    powershell -ExecutionPolicy Bypass -File tools\sync-to-unity.ps1

It builds sim/NovaFaction.Sim in Release, copies NovaFaction.Sim.dll to client/Assets/Plugins/NovaFaction/ and
mirrors content/**/*.json into client/Assets/Resources/content/. It is idempotent and prints what it copied.
- Both copies are committed: the DLL is small and Git LFS tracks *.dll, and the JSON has to ship in the build.
  Forgetting the script leaves the client running old rules, which shows up as a state hash that no longer
  matches the harness.
- client/Assets/Resources/content/ is generated. Never edit it by hand; edit content/ and re-run the script.
- client/Assets/Scripts/ContentLoader.cs loads that JSON through the same sim loaders the harness uses, so the
  client and the harness build identical content. SimSmokeTest.cs plays one headless match and shows its final
  state hash on screen; it must equal the harness's for the same seed.

## Server rules (server/, from M3)
- The server is authoritative for accounts, economy and match results. Never trust a
  client-reported outcome; verify by replaying the input log through the sim.

## Working style
- One feature per session. Small, reviewable changes. Ask before large refactors.
- Commit when a task is complete and tests pass, with a clear message. Never commit builds/,
  Library/, or binaries outside Git LFS.
- Keep docs/design.md current: when a design decision is made or changed in a session, update it.
- End every task with: what changed, how to verify (commands or phone steps), what's next.

## Session log
- 2026-09-19 Session L: made the sim DLL reproducible. Two SDK properties are needed, not one, and both are
  in `sim/NovaFaction.Sim/NovaFaction.Sim.csproj` with a comment saying why:
  `IncludeSourceRevisionInInformationalVersion=false` keeps the sha out of the version string, and
  `EnableSourceControlManagerQueries=false` stops the SDK querying git at all. The second one is the important
  one: SourceLink writes the sha into the PDB, and the PDB's checksum and the deterministic module id are
  embedded in the DLL, so without it the DLL still changed after every commit (exactly 72 bytes: module id,
  PDB id, PDB checksum, two hash-derived stamps). A "new" DLL from `sync-to-unity.ps1` now means the sim
  really changed. Caveat: reproducible per machine and per path only — deterministic builds embed absolute
  paths. Still open: the `PowerShell(...)` deny-rule twins. Writing `.claude/` needs Glenn's yes in chat, and
  this session ran unattended from `docs\RUN.md`, so the lines are under Glenn's actions in STATUS.md.
- 2026-09-19 Session K: installed `.claude/` (settings, Stop hook, `next` and `audit` skills). The Stop hook runs
  `dotnet test` (~10 s, 889 tests) only when `sim/`, `content/` or `tools/` changed since the last pass, and
  exits in 0.3 s otherwise. Writing `.claude/` needs Glenn's confirmation in chat. The sim DLL differs after each
  commit only because the SDK stamps the git commit ID into it (see BACKLOG "Discovered").
