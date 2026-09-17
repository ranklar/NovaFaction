# NovaFaction — Game Design v0.1 (Sept 2026)

## Vision
Real-time unit-drop battler (Clash Royale / Warcraft Rumble genre). Commercial free-to-play.
Android first; iOS and PC later. Solo developer using Claude Code.
Themed factions released over time as content packs; fantasy faction first.

## Match rules
- 1v1, human or bot. 3:00 clock.
- Win: enemy Keep destroyed = instant win. Otherwise higher damage score at the clock.
  Exact tie -> 60s sudden death, first damage wins. No draws.
- Score = HP removed from enemy structures + a destruction bonus per structure killed.
- Structures per side: Keep (base) + 2 forward towers. Forward towers attack.
  Destroying a forward tower extends the attacker's deploy zone onto that side.
- Resource: Gold (display name is faction data). Steady base income plus two map sources:
  gold mines (neutral, capturable, extra income, capped) and gold chests (spawn on the
  field, collected by walking a unit over them). Target: active player earns ~1/3 more than a turtle.
- Stored gold caps at 10. Unit costs 1-7.
- Deck of 8 = 1 leader + 7 cards. Hand of 4, next card visible. Cycle order shuffled from
  the match seed, then loops.
- Leader deploys like a unit; carries the faction passive and an active ability on cooldown.
- 1s spawn delay on deploy. Deploy zones are map data (own side + around held structures).

## Maps
- A map is a data file: terrain grid, structure positions, deploy zones, mine/chest spawn
  points, decoration set. Layouts vary per map.
- Launch PvP pool (3): two-lane symmetric; open field with chokepoints; three-lane with center mine.
- Fairness rule: ranked draws only from a free rotating pool. Purchased/unlocked maps appear
  only in PvE, friendly matches, and a casual host-picks mode.
- PvE campaign maps may be asymmetric (defend a pass, siege a fortress).

## Factions (themes)
- Theme = faction (own unit roster) + arenas + UI skin + music, shipped as a content pack.
- Every faction fills the same ~10 archetype slots: tank, bruiser, swarm, ranged, flyer,
  siege, support, spell, building, leader. Slot = job; faction = flavor + one twist mechanic.
- Unit gold cost derives from one shared cost formula (HP, damage, speed, range, utility budget).
- Launch: fantasy only, 14-16 units incl. 2 leaders. Second faction = first major post-launch
  release. Never two factions in development at once.

## PvE campaign
- Linear chapters, light story via text panels. Launch: 5 chapters x 7 missions on ~12 map
  layouts; one boss per chapter with a unique mechanic. Chapter 1 is the tutorial: missions 1-5
  each introduce one mechanic (deploy, gold cap, chests, mines, forward towers, damage score).
- Win conditions are mission data: destroy target, survive the clock, most damage, escort.
- Enemies = the PvP bot brain with a preset deck and a personality (aggressive, turtle, swarm).
  Difficulty knobs: enemy unit levels, income multiplier, reaction delay, decision quality.
  Bosses add scripted waves.
- 3 stars per mission: win; win with Keep above 50% HP; mission-specific challenge.
  Star totals advance a chapter reward track with fixed rewards (main unit-earning path).
- Heroic tier per chapter after clearing it: leveled enemies + a modifier, better shard rewards.
- Pacing: start with 6 units, unlock one every 2-3 missions through chapters 1-2 (~12 by chapter 3).

## Progression and economy
- Currencies: Gold (in-match only), Coins (earned meta), Gems (premium).
- Units earned via campaign, ranked season rewards, season pass; or bought outright with gems.
  Every unit is earnable.
- Unit levels cost coins + shards (duplicates). Shards from play; gem purchase allowed under a
  daily cap. ~5-7% stat gain per level.
- Matchmaking: rating + deck power (average unit level). Level cap per league.
- Season pass: 30-day cycle, free and paid tracks. Home of faction/map releases.
- Daily quests (3) and weekly quests for coins, shards, a trickle of gems.
- Cosmetics within a faction: unit skins, base skins, victory effects.
- No loot boxes or random purchases. No ads. No energy gates.
- Ranked matchmaking falls back to human-like bots when the queue is empty.

## Social
- None at launch. Ranked leaderboard only (top 100 + own rank). Data model must allow
  friends and clans later without migration.

## Store and compliance
- Server owns the economy: rewards, purchases, levels and balances computed server-side.
- Purchase receipts verified server-side with Google/Apple before granting.
- Teen rating; not child-directed (avoid COPPA scope).

## Technical architecture
- client/: Unity 6.3 LTS, URP, Android first. Renders sim state; no game rules in Unity code.
- sim/: netstandard2.1 C# library. Deterministic: fixed-point math, seeded RNG, fixed 20 ticks/s,
  flow-field pathfinding on a grid. Headless bot-vs-bot harness; per-tick state hash for
  cross-platform determinism checks. No Unity references.
- Sim numerics (sim/NovaFaction.Sim):
  - Fix = Q48.16 fixed point (long raw, 1.0 = 65536). + - * / saturate at MaxValue/MinValue
    instead of wrapping; * and / use exact 128-bit intermediates and round to nearest, ties away
    from zero (symmetric for negatives). Divide by zero throws. Round() is also ties-away-from-zero.
  - Balance data is written as plain decimals ("1.5", "-0.25"; no exponent, no leading '+',
    no ".5" / "5.") and parsed with integer math, rounded to the nearest 1/65536.
    Fix.ToString() gives the shortest decimal that parses back to the same value.
  - Fix.ToFloat() is view-only for the client. A reflection test fails the build if any other
    sim member uses float or double.
  - FixVector2.Length/Normalized use exact 128-bit sums of squares, so they stay accurate for tiny
    and huge vectors; LengthSquared/Dot saturate beyond about +/-8,000,000 per component.
  - SimRandom = PCG32 (XSH-RR, reference stream 54), state is a single ulong (GetState/SetState)
    for state hashes and replays. NextInt is unbiased (rejection sampling). Chance always consumes
    exactly one draw. Changing the algorithm breaks saved replays; a test pins reference output.
- Sim match loop (sim/NovaFaction.Sim):
  - content/rules.json holds match-wide rules (tick rate, clock, sudden death, gold income/start/cap,
    spawn delay, hand and deck size). Every key is required; unknown keys are errors. JSON has no
    comments, so values that are still guesses are listed by name in "tuningPlaceholders".
    Current placeholders: base income 0.35 gold/s (about Clash Royale's pace) and 5 starting gold.
    The spawn delay must be a whole number of ticks. handSize must be less than deckSize.
  - SimJson: strict RFC 8259 reader, no dependencies. Numbers stay as text until read as int/long or
    Fix (exponents are valid JSON but rejected as Fix). Errors carry file name, line and column.
    Duplicate keys are errors; a leading byte-order mark is allowed; nesting max 64.
    The sim takes JSON text, not file paths, so the client can load it however Unity needs.
  - Command = tick, player (0/1), per-player sequence number, type (None, DeployCard,
    LeaderAbility), hand slot (-1 when unused), target. Canonical order: tick, player, sequence.
    Malformed commands (bad player/slot/type, wrong tick, duplicate player+sequence) make Tick()
    throw with state unchanged; they are input-layer bugs, not gameplay. Game-rule rejections
    (not enough gold, outside deploy zone) will be deterministic no-ops when those features land.
  - Simulation.Tick(commands) order: validate, record in the CommandLog, apply commands in
    canonical order, accrue income, advance tick and clock, handle clock expiry.
    Tick N's commands must be stamped N (State.Tick before the call). A 3:00 match is 3600 ticks.
  - Income is exact: each tick adds income/ticksPerSecond with the sub-raw remainder carried per
    player, so a player gains exactly the per-second income every whole second. At the cap the
    carry is discarded (a capped player banks nothing).
  - Clock expiry: Regulation -> Ended for now. Scoring, sudden death and Keep kills are TODO.
  - State hash: 64-bit FNV-1a over little-endian bytes, starting with a hash format version.
    Covers tick, RNG state, clock, phase, and per player: gold raw, income carry, score, command
    count. New state implements IStateHashable and appends count-then-items in id order.
    A test pins the hash of a scripted full match; change it only on purpose.
  - Replay = rules + seed + CommandLog (Simulation.Replay). The log is in memory only for now.
- server/: ASP.NET Core (C#). Accounts, economy, matchmaking, input relay, match verification by
  re-running the sim. PostgreSQL. Runs on the Windows desktop for LAN testing; cloud container later.
- content/: JSON data for units, factions, maps, missions. Art in Addressables bundles per theme.
- Netcode: custom deterministic lockstep with input relay. Photon Quantum is the fallback;
  decision at end of M1.

## Milestones
- M0 Setup (Sept-Oct 2026)
- M1 Sim + bot, headless (Oct 2026-Jan 2027)
- M2 Vertical slice on phone: 1 map, 8 units, PvE vs bot (Feb-Apr 2027)
- M3 Accounts, relay, LAN PvP, verification (May-Jul 2027)
- M4 Full fantasy roster, 3 PvP maps, campaign ch.1-3, economy, ranked (Aug 2027-Jan 2028)
- M5 IAP, polish, analytics, cloud, closed test (Feb-Apr 2028)
- M6 Ch.4-5, Heroic, balance, store listing, Android launch (May-Jul 2028); iOS ~2 months later; PC after

## Open items
- Studio name and Android package identifier.
- Fantasy roster: the 16 units and 2 leaders.
- Income, cost and match-length numbers (tune in the headless harness).
- Sudden death that ends with no damage dealt: the rules say "no draws" but name no tie-break.
- CommandLog file format for saved replays and server verification.