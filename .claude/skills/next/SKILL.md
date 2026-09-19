---
name: next
description: Carry out the brief in docs/NEXT.md end to end, then audit it, record status, prime the next brief, commit and push. Glenn runs this; never invoke it on your own.
disable-model-invocation: true
---

You are running NovaFaction's standard work loop. Glenn types `/next` (or the inbox routine starts this for him) and nothing else; everything below is your job. Never ask Glenn to paste text anywhere or to run commands for you. Anything only he can do (an account, a key, a decision, a hands-on step) goes under **Glenn's actions** in docs/STATUS.md, and you do the rest of the work around it.

## 1. Load context

Read, in this order: `CLAUDE.md`, `docs/NEXT.md`, the top entry of `docs/STATUS.md`, and `docs/BACKLOG.md`.

## 2. Make sure there is a brief

If `docs/NEXT.md` holds a real brief, use it as written.

If it is empty, or only holds the placeholder: take the top item in `docs/BACKLOG.md` that is **not** marked blocked (on Glenn, on the planner's brief, or on release). If there is one, write a brief from it — goal, scope, acceptance criteria that a command can check, files you expect to touch, tests to add — head it "Drafted by Claude Code" and continue. If every item is blocked, do not invent work: add a one-line STATUS.md entry saying "nothing released", and stop.

## 3. Do the work

Follow CLAUDE.md's rules for this repo without exception. Add or update tests for what you build. Run the test suite until it is green.

If the brief turns out to be wrong or impossible as written, do the largest sensible part of it and say exactly what you changed and why in STATUS.md. Do not silently substitute a different plan.

## 4. Independent audit

Run the `/audit` skill and wait for its verdict. If it reports FAIL, fix the findings and run it once more. If it still fails, the STATUS entry is marked BLOCKED with the reason.

## 5. Record

- `docs/STATUS.md`: add a new entry at the top, using the template at the bottom of that file.
- `docs/BACKLOG.md`: move the finished item to **Done** with today's date; add anything new you discovered under **Discovered**.
- `CLAUDE.md`: add a short session-log entry with the decisions and findings a future session needs. Keep it tight.
- `docs/NEXT.md`: replace it with a drafted brief for the next unblocked backlog item, headed "Drafted by Claude Code — the planner may replace this". If nothing is unblocked, leave it as the placeholder and say so in STATUS.md.

## 6. Commit and push

One commit, or a few logical ones, with descriptive messages. Then `git push origin main`. Never force-push, never rewrite history.

## 7. Report

Finish with at most six lines: what was built, the test count, the audit verdict, Glenn's actions if any, and what is queued next. No code walkthroughs; STATUS.md carries the detail.

## 8. Receipt

If this run was started by the inbox routine, there is a folder `docs/runs/<timestamp>/` holding `RUN.md` (and usually `STARTED.md`) but no `DONE.md`. Write `DONE.md` there before you finish: the task line, start and finish times, what was done in at most five lines, and anything that needs Glenn. Do this even when the work was blocked or nothing was released; the receipt is how the planner and the heartbeat know the run ended.
