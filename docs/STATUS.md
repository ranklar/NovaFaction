# Status — newest entry first

Glenn: read the top entry only. **Glenn's actions** lists anything only you can
do. Everything else is handled.

---

### 2026-09-19 — Session K (install the working loop) — done

- **Done:** Installed the kit: `.claude/settings.json` (permission rules + Stop hook),
  `.claude/hooks/stop-tests.ps1`, and the `next` and `audit` skills. They're copied unchanged
  from `docs/kit/claude/`, which is now deleted. The hook checked out on this machine: the
  first run took 10.5 s (tests ran) and wrote `.claude/hooks/.last-pass`. A repeat took 0.3 s
  (skipped). Touching `content/rules.json` made the tests run again (10.1 s). Reading `.env` is
  denied (there's no `.env` in the repo anyway). `.gitignore` covers `.last-pass`,
  `settings.local.json`, `docs/runs/` and `docs/RUN.md`.
- **Tests:** 889 passing. `dotnet test` took 16.9 s wall-clock (8 s in the test runner
  itself). The Stop hook adds about 10 s to any turn that changed `sim/`, `content/` or
  `tools/`, and 0.3 s otherwise.
- **Audit:** PASS — `docs/audit/2026-09-19_1614.md`. The main finding: the "no force-push",
  `git reset --hard`, `git clean` and delete deny rules only apply to Bash commands, not to
  PowerShell commands (and PowerShell is this machine's main shell). The new NEXT.md brief
  already covers adding the PowerShell versions.
- **Health check:**
  1. Tests: 889/889 green, 16.9 s.
  2. `sync-to-unity.ps1`: the content matched (9 of 9 JSON files unchanged), but it **did
     copy a new DLL**. I compared the two DLLs: the code is identical. The only difference is
     the git commit ID the .NET build stamps into the file's version (`1.0.0+002b2ef…` in the
     committed DLL, `1.0.0+9e6b413…` in the fresh build). So the client is running current
     rules, but every commit will make the next sync look like a change. I put the committed
     DLL back and committed nothing; see BACKLOG "Discovered".
  3. M2: `docs/design.md` lists M2 as "Vertical slice on phone" (Feb–Apr 2027). Commit
     `9e6b413` brought the sim into Unity (sync script, content loader, SimSmokeTest). Nothing
     in the repo records that the phone has shown `0x52F44A49D82FFCDA` for seed 7,
     balanced vs balanced, twolane. That step is still Glenn's.
- **Kit changes for the planner:** none to the files. Notes for the other repos:
  - The auto-mode safety check refuses to write `.claude/` until Glenn confirms in chat. It
    worked once he answered "yes, install it".
  - The new skills (`/next`, `/audit`) don't load until Claude Code starts a new session, so
    this session's audit was a fresh subagent running `audit/SKILL.md` word for word.
  - The git and delete deny rules (force-push, `reset --hard`, `git clean`, `rm`, `rmdir`,
    `del`, `Remove-Item -Recurse`) are written as `Bash(...)` only. Commands run through the
    PowerShell tool aren't covered, so each one needs a `PowerShell(...)` version too.
- **Glenn's actions:**
  1. Run SimSmokeTest on the phone and check that it shows `0x52F44A49D82FFCDA`
     (BACKLOG item 1).
  2. From the next session on, type `/next` to start work.
- **Next:** the planner released code work during this session and wrote the next brief in
  `docs/NEXT.md`: stop the sim DLL changing on every commit, plus the PowerShell deny rules.
  I didn't start it here, because Session K's brief says not to start project work. `/next`
  picks it up.

---

### 2026-09-19 — Session K (install the working loop) — BLOCKED (earlier attempt)

- **Done:** Nothing installed. The inbox routine picked up `docs\RUN.md`, but Claude Code's
  safety check refused to copy `docs/kit/claude/` into `.claude/`. The kit rewrites Claude's
  own permission rules and adds a Stop hook, and an unattended run started from a dropped
  file isn't allowed to do that. After that it also refused to run the test suite, so the
  run stopped. `docs/kit/` is untouched, `.gitignore` and `CLAUDE.md` still have the
  planner's uncommitted edits, and nothing was committed or pushed.
- **Tests:** not run this session (blocked). Last commit `9e6b413` was green.
- **Audit:** not run. The `/audit` skill is part of the kit that could not be installed.
- **Health check:** (1) test count and duration not measured (blocked). (2) sync-to-unity
  not run (blocked). (3) M2: the design doc lists M2 as "Vertical slice on phone" (Feb–Apr
  2027). The latest commit, `9e6b413`, brings the sim into Unity (sync script, content loader,
  SimSmokeTest). The reference hash `0x52F44A49D82FFCDA` (seed 7, balanced vs balanced,
  twolane) appears only in the brief and BACKLOG.md, so there's no record that the phone
  has confirmed it. That step is still Glenn's (BACKLOG item 1).
- **Kit changes for the planner:** none made. Note for other repos: installing `.claude/`
  from an unattended RUN.md run is refused, so the kit has to be installed in a session
  Glenn starts himself.
- **Glenn's actions:**
  1. Open NovaFaction in the Claude desktop app and type: "Read docs/NEXT.md and carry it
     out." (`/next` doesn't exist until the kit is installed.) Approve the `.claude/`
     install when it asks. The brief in `docs/NEXT.md` is unchanged and ready.
  2. (Unchanged) Run SimSmokeTest on the phone and compare the hash with `0x52F44A49D82FFCDA`.
- **Next:** Session K again, started by Glenn. The brief is still in `docs/NEXT.md`.

---

## Entry template (copy above the line, newest first)

### YYYY-MM-DD — <session name>

- **Done:** two or three lines, plain language.
- **Tests:** N passing, in M seconds.
- **Audit:** PASS or FAIL — `docs/audit/<file>.md`, one line on any finding that matters.
- **Glenn's actions:** none, or a short list of things only he can do.
- **Next:** one line; the full brief is in `docs/NEXT.md`.
