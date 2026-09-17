# NovaFaction — instructions for Claude Code

Read docs/design.md before any task. It is the source of truth for game rules and architecture.

## Who you're working with
Glenn is the sole developer. He is an experienced program manager, not a current programmer
(C++ ~15 years ago). Explain changes in plain language, summarize what changed and exactly how to
verify it. He uses Windows 11 only — give PowerShell commands, never bash/WSL/Linux.

## Repo layout
- client/   Unity 6.3 LTS project (URP, Android first). Rendering, input, UI only.
- sim/      NovaFaction.Sim (netstandard2.1 class library) and NovaFaction.Sim.Tests (xunit).
- server/   ASP.NET Core service (created in M3).
- content/  JSON data: units, factions, maps, missions.
- docs/     Design doc and notes.
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
- Every feature ships with xunit tests. Include a determinism test where relevant: same seed and
  inputs twice must produce identical per-tick state hashes.
- Run before finishing any sim task:  dotnet test sim\NovaFaction.Sim.Tests

## Client rules (client/)
- No game rules in Unity code. The client renders sim state and sends player inputs.
- Edit C# scripts and JSON only. Do not hand-edit scenes, prefabs, .meta files, or
  ProjectSettings unless explicitly asked — Glenn does those in the Unity editor with guidance.
- Never touch client/Library, client/Temp, client/Logs.

## Server rules (server/, from M3)
- The server is authoritative for accounts, economy and match results. Never trust a
  client-reported outcome; verify by replaying the input log through the sim.

## Working style
- One feature per session. Small, reviewable changes. Ask before large refactors.
- Commit when a task is complete and tests pass, with a clear message. Never commit builds/,
  Library/, or binaries outside Git LFS.
- Keep docs/design.md current: when a design decision is made or changed in a session, update it.
- End every task with: what changed, how to verify (commands or phone steps), what's next.