# NovaFaction — Game Design v0.1 (Sept 2026)

## Vision
Real-time unit-drop battler (Clash Royale / Warcraft Rumble genre). Commercial free-to-play.
Android first; iOS and PC later. Solo developer using Claude Code.
Themed factions released over time as content packs; fantasy faction first.

## Match rules
- 1v1, human or bot. 3:00 clock.
- Win: enemy Keep destroyed = instant win. Otherwise higher damage score at the clock.
  Exact tie -> sudden death. No draws.
- Sudden death (decided Sept 2026, implemented Sept 2026): lasts 60s; gold income is doubled
  (rules.json suddenDeathIncomeMultiplier); the first player to deal any structure damage wins.
  If it expires with no damage dealt, tie-breaks apply in this order:
  1. more enemy structures destroyed;
  2. higher HP on your own weakest standing structure (destroyed structures are left out; rule 1
     already counted them. Corrected Sept 2026; it used to say a destroyed structure counts as 0 HP);
  3. more gold received from mines and chests (base income does not count);
  4. a coin flip from the match seed.
- Score = HP removed from enemy structures + a destruction bonus per structure killed.
- Structures per side: Keep (base) + 2 forward towers. Forward towers attack.
  Destroying a forward tower extends the attacker's deploy zone onto that side.
- Resource: Gold (display name is faction data). Steady base income plus two map sources:
  gold mines (neutral, capturable, extra income, capped) and gold chests (spawn on the
  field, collected by walking a unit over them). Target: active player earns ~1/3 more than a turtle.
  Implemented Sept 2026; see "Map gold" under Technical architecture for the rules and the arithmetic.
- Stored gold caps at 10. Card costs 1-7.
- Cards are units or spells (implemented Sept 2026; see "Spells"). Spells can be cast anywhere on the map.
- Deck of 8 = 1 leader + 7 cards (units and spells mixed freely). Hand of 4, next card visible. Cycle order shuffled from
  the match seed, then loops.
- Leader deploys like a unit; carries the faction passive and an active ability on cooldown
  (implemented Sept 2026; see "Stat modifiers, levels and leaders").
- One leader at a time (decided and implemented Sept 2026): a player's leader card cannot be deployed while that
  player's leader is alive on the field or waiting out its spawn delay. The deploy is silently ignored and counted
  (IgnoredDeploys), like any other rejected deploy.
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
  daily cap. ~5-7% stat gain per level (in the sim since Sept 2026: Hp and Damage +6% of base per level,
  levels 1-15; see "Stat modifiers, levels and leaders").
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
  flow-field pathfinding on a grid. Headless bot-vs-bot matches (HeadlessMatch; see "Controllers and bots") and a harness
  console app (tools/NovaFaction.Harness; see "Headless harness"); per-tick state hash for cross-platform determinism checks;
  replay files (see "Replays"). No Unity references.
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
  - content/rules.json holds match-wide rules (tick rate, clock, sudden death length and income
    multiplier, gold income/start/cap, spawn delay, hand and deck size, unit separation, stopped push
    and spawn spacing, aggro radius, melee crowd penalty, and the eight map gold values). Every key is
    required; unknown keys are errors. JSON has no comments, so values that are still guesses are listed
    by name in "tuningPlaceholders". Current placeholders: base income 0.35 gold/s (about Clash Royale's
    pace), 5 starting gold, the four unit movement values (see "Units, decks and movement"), the two
    targeting values (see "Combat and match resolution"), all eight map gold values (see "Map gold"), the two
    capture give-up times (mineCaptureGiveUpSeconds 3, mineCaptureRetrySeconds 6; see "Map gold") and the two level
    values (maxUnitLevel 15, levelStatBonusPerLevel 0.06; see "Stat modifiers, levels and leaders").
    The give-up and retry times must be whole ticks; maxUnitLevel is 1-100; levelStatBonusPerLevel is 0-1.
    rulesVersion (whole number >= 1, added Sept 2026 for replays) must be raised whenever a rules value changes; replays
    record it. MatchRules.ContentHash fingerprints every value (not tuningPlaceholders) from the parsed data.
    suddenDeathIncomeMultiplier (2) is design data, not a placeholder; base income times it must not
    exceed the gold cap. unitStoppedPushFactor must be 0..1.
    The spawn delay must be a whole number of ticks. handSize must be less than deckSize.
  - SimJson: strict RFC 8259 reader, no dependencies. Numbers stay as text until read as int/long or
    Fix (exponents are valid JSON but rejected as Fix). Errors carry file name, line and column.
    Duplicate keys are errors; a leading byte-order mark is allowed; nesting max 64.
    The sim takes JSON text, not file paths, so the client can load it however Unity needs.
  - Command = tick, player (0/1), per-player sequence number, type (None, DeployCard,
    LeaderAbility), hand slot (-1 when unused), target. Canonical order: tick, player, sequence.
    Malformed commands (bad player/slot/type, wrong tick, duplicate player+sequence) make Tick()
    throw with state unchanged; they are input-layer bugs, not gameplay. Game-rule rejections
    (empty hand slot, not enough gold, target not deployable, a leader card while that player's leader is on the field or
    pending) are deterministic no-ops that only
    increase the player's IgnoredDeploys counter, which is in the state hash. A rejected LeaderAbility
    (see "Stat modifiers, levels and leaders") likewise only increases IgnoredAbilities.
  - Simulation.Tick(commands) order: validate the outside commands, collect each controller's commands (see
    "Controllers and bots"), validate all, record them in the CommandLog, apply commands in
    canonical order (leader Heal and Rally act here), create units of zero-delay deploys, combat, spells and
    movement (see "Combat and match resolution" and "Spells"), resolve Keep kills and sudden-death damage, mine
    capture, capture give-ups, then chest collection (see "Map gold"), accrue income (base plus mines, multiplied
    in sudden death), advance tick and clock, remove Rally buffs whose time is up; then, unless the match just
    ended, create units whose spawn delay is over, spawn a due chest wave, and handle clock expiry.
    Tick N's commands must be stamped N (State.Tick before the call). A 3:00 match is 3600 ticks.
  - A match is built from a MatchSetup (rules, map, structure stats, player 0's deck, player 1's deck, and
    optionally each player's structure level, default 1) plus the seed:
    new Simulation(setup, seed); Simulation.Replay(setup, seed, log). MatchSetup.WithBot(player, personality) returns a copy
    in which that player is a bot (see "Controllers and bots"). Each deck carries its own
    faction roster, so the two players may later bring different factions.
  - Income is exact: each tick adds income/ticksPerSecond with the sub-raw remainder carried per
    player, so a player gains exactly the per-second income every whole second. Base income and mine
    income have separate carries and are added in that order, so at the cap mine gold is the part
    that gets lost. At the cap both carries are discarded (a capped player banks nothing).
  - Clock expiry: see "Combat and match resolution".
  - State hash: 64-bit FNV-1a over little-endian bytes, starting with a hash format version.
    Covers tick, RNG state, clock, phase, winner, end reason, tie-break rule, and per player: gold
    raw, income carry, mine income carry, score, gold from map, command count, ignored deploys, ignored
    abilities, ability ready tick, the deck's card catalog content hash (units and spells), the deck's card
    levels (sorted card order), the structure level, hand slots and draw queue. Then the map, the structures file's
    content hash, every structure's combat state (index order: index, max hp, damage, hp, attack cooldown, target
    unit id), the next unit id, every unit (id order: id, owner, card id, position, hp, state, objective,
    target kind and id, attack cooldown, level, the five effective stats, the Rally buff (present, expiry tick,
    modifiers), capture stall ticks, mine-ignore tick), every pending spawn (deploy order, with its level), the
    next projectile id and
    every projectile (id order: id, owner, position, target, aim point, speed per tick, damage, splash
    radius, target layer), every mine (index order: index, owner, capturing player, capture progress
    ticks), every chest spawn (index order: index, chest present), the next spell id, every pending spell
    and then every active spell zone (both in id order: id, owner, spell id, target, land tick, end tick,
    pulse interval, unit damage, structure damage). Unit state includes Capturing. Hash format version is 7
    (Sept 2026: levels, leaders and capture give-up).
    New state implements IStateHashable and appends count-then-items in id order.
    A test pins the hash of a scripted full match on the small test map with fixed inline rules and
    structure stats (it includes kills, projectiles, chest pickups by both players, Fireball casts, a War Cry,
    rejected leader abilities and a Keep kill); change it only on purpose.
  - Replay = rules + seed + CommandLog (Simulation.Replay). Replay ignores the setup's bots and feeds the log to two
    HumanControllers: bot decisions are already in the log. Replay files: see "Replays".
  - Match observer: see "Match observer".
- Replays (sim/NovaFaction.Sim/Replays, Content/ContentLibrary.cs, SimVersion.cs; implemented Sept 2026):
  - SimVersion.Current (a constant in code, now 1) is the version of the sim's logic. Bump it whenever sim logic changes
    in a way that can change a match or its hashes (rules code, bots, tick order, hash layout); refactors and tests do
    not need a bump. Replays only re-run on the same sim version.
  - Content version (ContentVersion.Compute(setup)) = FNV-1a over the content hashes of everything the match uses: the
    rules (MatchRules.ContentHash, includes rulesVersion), structures.json, the map, each player's faction (units plus
    spells) and, per player, whether a bot played and that personality's hash (BotPersonality.ContentHash). All are
    computed from parsed data like the map's hash, so line endings never matter. Decided Sept 2026: content the match
    does not use (other maps, other bots) is left out, so adding a map or a bot does not invalidate old replays.
  - ContentLibrary: all loaded content by id (rules, structures, maps, factions' card catalogs, bots); lookups only,
    listed collections sorted by id. The caller reads the files (the sim still takes JSON text).
  - Binary format, version 1 (ReplayWriter, ReplayReader). Little-endian; varint = unsigned LEB128; svarint = zigzag
    varint; string = varint byte length + UTF-8.
    Header: magic "NFRP", formatVersion (u16), simVersion (varint), contentVersion (u64), rulesVersion (varint), map id
    (string), map content hash (u64), seed (u64); per player: faction (string), card count, each card's id (string) and
    level (varint) in deck order, structure level (varint), bot flag (byte) and the personality id (string) for a bot.
    Body: command count, then each command in canonical order: tick delta from the previous command (varint), player
    (byte), sequence (varint), type (byte), hand slot + 1 (varint), target x and y raw Q48.16 (svarint).
    Footer: final tick (varint; also the log's tick count), final state hash (u64). Nothing may follow.
    A full 3:00 bot match is under 1 KB (about 40-50 commands).
  - Replay.FromMatch(sim) records a match as it stands (normally finished; the log is copied).
    ReplayReader.Read(bytes, content) checks format version, sim version, rules version, that the map exists with the
    same hash, that decks and bots exist, and the content version; each failure is a ReplayException with a readable
    message. ReplayReader.Read(bytes) checks only the file and format version, for inspecting old files. Corrupt data
    never throws anything else and never causes huge allocations (lengths are bounded; a test fuzzes it).
  - Replay.Verify(content) rebuilds the setup, re-runs the log with HumanControllers and checks the final tick and hash.
    It never throws for a bad replay: the result says why (tampered command -> hash mismatch; malformed command -> the
    tick that could not run; match ended early; other sim or content version). The server will use this (M3).
  - ReplayWriter.ToJson(replay): indented JSON of the same data for debugging (64-bit values as hex strings, positions
    as exact decimals). Nothing reads it back; the binary file is authoritative.
- Match observer (sim/NovaFaction.Sim/Observers, implemented Sept 2026):
  - Simulation.Observer (optional IMatchObserver, may be set or cleared between ticks; HeadlessMatch.Run takes one) hears:
    card deployed (unit cards), spell cast, leader ability used, unit died (with the killing hit's source), structure
    damaged (HP actually removed, with source card), structure destroyed, mine captured (new and previous owner), chest
    collected (unit, gold received, even 0 at the cap), gold accrued (Base, Mine or Chest; only gold actually banked) and
    match ended (always last, once). Events carry the tick being run (match ended: the match length).
  - Damage sources: kind (Unit, Structure, Spell, LeaderAbility), attacking player, card id (structure kind for
    structures) and entity id. Projectiles and ability strikes carry their source; it is not state and not hashed.
    The killer of a unit is the hit that took it to 0 HP, in the combat hit order.
  - Observers are never part of the match: events are readonly structs with no live state, calls are synchronous inside
    Tick(), calling Tick() from an observer throws, and an observer must not throw. Tests check identical per-tick
    hashes with and without an observer (bot matches, attaching and detaching mid-match, and the pinned scripted match
    keeps its pinned hash), and that events add up to the final state (gold ledger, score, deaths, structure HP, mine
    owners, accepted commands).
- Headless harness (tools/NovaFaction.Harness, implemented Sept 2026): a .NET 10 console app over the sim and the real
  content/ folder (it may use System.Text.Json and Parallel; the sim may not). Commands: run (one match: timeline,
  result, per-side and per-card stats, final hash, optional replay file that is verified at once), batch (N matches in
  parallel: win rates, end reasons, tie-break rules, Keep-kill rate, length and score-margin mean/spread/min/max,
  per-card deploys, kills, deaths, structure damage per gold, gold by source per side, the income ratio and the
  active-vs-turtle ratio, mine captures and chests; CSV and JSON in tools/out/), roundrobin (every ordered pairing of
  personalities, mirrors included, same seeds for every pairing; one table plus overall win rates), determinism (each
  match played serially with an observer and in parallel without, compared hash by hash after every tick, then saved as
  a replay, read back and verified), verify and export (replay files). Sides are personality ids or "idle" (a player who
  never acts). Decks default to the seven fantasy units plus Fireball. tools/out/ is not committed.
  - Sept 2026 baseline (shipped placeholders, twolane, 50 matches per pairing): every match goes the full 3:00, no Keep
    kills; overall win rates turtle 68%, balanced 59%, swarm 50%, aggressive 23%. Both sides bank about 73-76 gold per
    match (about 63 base, 5-10 mines, 1-7 chests), so active and turtle bots earn about the same (ratio about 1.0 against
    the 1.33 target); the turtle collects the most chests. The aggressive bot never deploys its
    6-cost warlord (it rarely holds 6 gold).
  - Tuning sessions and their numbers are logged in docs/balance-log.md against the targets in "Balance targets".
- Sim map layer (sim/NovaFaction.Sim/Map):
  - Map files: content/maps/<id>.json with formatVersion (1), id (a-z 0-9 _ -), cellSize (world units
    per cell, 1/16..64), grid, structures, deployZones. Unknown keys are errors.
  - grid: equal-length strings, first string = top row. Legend: '.' ground, '#' blocked, 'K' Keep,
    'T' forward tower, 'M' gold mine, 'C' chest spawn, '0'/'1' player spawn-side marker.
    C, 0, 1 are markers on open ground (walkable). M is a mine with a 1x1 blocked footprint (changed
    Sept 2026; it used to be walkable). Max 256x256.
  - Coordinates: cell (0,0) is bottom-left; y grows toward player 1. World origin is the
    bottom-left corner of cell (0,0); cell (x,y) covers [x, x+1) * cellSize on each axis.
    Player 0 owns the bottom, player 1 the top, but owners are written explicitly in the file.
  - structures: list of { id, kind "keep"|"tower", owner 0|1, footprint {x,y,width,height} },
    towers also have unlocksDeployZone (a rectangle on the owner's side that the other player may
    deploy into once the tower is destroyed). A structure's number in the sim is its list position.
    The K/T letters must match the declared footprints exactly (both ways); this double entry catches
    typos. deployZones: list of { player, x, y, width, height }; at least one per player.
  - Validation: rectangular grid; known characters; exactly 1 Keep + 2 towers per player;
    footprints in bounds, non-overlapping, and drawn with the right letter; spawn/mine/chest
    markers never under a footprint; all rectangles in bounds; at least one '0' and one '1' marker,
    each inside its own player's base deploy zone; every structure, mine and chest spawn reachable
    from every spawn marker. Errors name the file, line and cell.
  - Map content hash: FNV-1a over the parsed data (not the file bytes), so line endings and
    whitespace, which git may change per platform, do not matter. Folded into the state hash along
    with the map id, which structures are destroyed, and each player's unlocked zones.
    Hash format version is now 2.
  - Grid: walkable = in bounds, not '#', not a mine, and not under a standing structure. Destroying a
    structure makes its footprint walkable and bumps a walkability version. Mines never change.
    WorldToCell is exact floor division. The map validation's reachability checks use this grid, so a
    mine that walls off a lane or a spawn marker is a load error.
  - FlowField (one per target structure, cached, rebuilt when the walkability version changes):
    Dijkstra from the target's cells over walkable cells, 8 neighbors, straight cost 1, diagonal
    cost sqrt(2) = 92682/65536. No corner cutting: a diagonal needs both side cells open. The target's
    own cells count as open. Distance = path cost from cell center to nearest target cell center,
    times cellSize; Fix.MaxValue when unreachable. Direction = one of 8 unit vectors along a shortest
    path, zero inside the target or when unreachable. Ties: best aligned with the line to the
    target center, then the one to the left of that line, then a fixed order. The first two rules
    are unchanged by a 180-degree rotation, so on a symmetric map both players route identically
    (a test checks this on twolane).
  - Deploy: allowed on a walkable cell inside one of the player's base zones or unlocked zones.
    Unlocked zones are listed in tower order, not destruction order. Destroying a Keep unlocks nothing.
  - Simulation now takes the map: new Simulation(rules, map, seed); Replay(rules, map, seed, log).
  - content/maps/twolane.json: 18x32, 180-degree symmetric (also left-right symmetric apart from
    the mines). Keeps 4x3, towers 2x2, a 4-row blocked river with two 4-wide lane gaps,
    one mine at the inner edge of each gap (one on each side of the center line), two chest spawns
    per lane, base deploy zones = each player's 13 rows nearest their Keep, tower unlocks = a 9x6
    block in front of the fallen tower's half of the field.
- Units, decks and movement (sim/NovaFaction.Sim/Content, Cards, Units):
  - Unit files: content/factions/<faction>/units.json with formatVersion (1), faction (id), units.
    Each unit: id (a-z 0-9 _ -, unique across the faction's units and spells), displayName, slot (tank,
    bruiser, swarm, ranged, flyer, siege, support, building, leader; "spell" is rejected because spell cards
    live in spells.json), cost (whole number 1-7), hp (> 0), damage (>= 0),
    attackIntervalSeconds (> 0), range (> 0), moveSpeed (>= 0), targets (ground, air, both),
    targetPriority (any = enemy units and structures, structuresOnly), isFlying, spawnCount (1-25),
    isLeader (must be true exactly when slot is leader). Optional: projectileSpeed (> 0; present =
    ranged, absent = melee), splashRadius (> 0; absent = single target), canCapture (see "Map gold"),
    passive and ability (leaders only; see "Stat modifiers, levels and leaders"),
    "placeholder": true marks numbers that are guesses. Unknown keys are errors. The file's content
    hash (from parsed data) is part of the state hash.
  - content/factions/fantasy/units.json: 7 placeholder units, one per requested job: stone_golem
    (tank), knight (bruiser), goblin_pack (swarm of 4), elf_archer (ranged), griffin (flyer),
    catapult (siege) and warlord (leader). The fire_spirit spell stand-in was removed in Sept 2026 when the
    Fireball spell replaced it. Every number is a placeholder. Ranged: elf_archer (projectile 10/s) and
    catapult (6/s). Splash: catapult (1.25). Structures only: stone_golem and catapult. Capturers (by the
    default rule): knight, goblin_pack, elf_archer, warlord. The warlord's passive (+10% damage for bruiser and
    swarm) and War Cry ability are placeholders too.
  - Range is measured from the unit's center to the nearest point of the target's footprint, so
    melee units use a small positive range (0.5).
  - Deck: exactly deckSize (8) different card ids from one faction's card catalog (units.json plus
    spells.json), exactly one of them a leader unit (no duplicate cards). Unit and spell cards mix freely.
    Stored sorted by id, so the order a player lists cards never matters. The default test deck is the 7
    units plus Fireball.
    Each card has a level (default 1) given alongside the ids and kept with its card after sorting
    (Deck.Levels, Deck.GetLevel). Deck.Create accepts 1-100; MatchSetup rejects levels above the rules'
    maxUnitLevel. Where levels come from (the server's progression data) is decided with M3.
  - Hand: at match start player 0's deck is shuffled with the match RNG, then player 1's. The first
    handSize cards are the hand (slots 0-3), the rest the queue; the front of the queue is the
    visible next card. Playing a slot puts the next card into that slot and the played card at the
    back of the queue. Slots are never empty under current rules; the sim supports an empty slot
    (deploys from it are rejected) for future rules.
  - Deploy: valid when the slot holds a card, gold >= cost, and the target is deployable for that
    player now (base or unlocked zones, walkable cell). For a spell card the target only has to be on the
    map (see "Spells"). Commands apply in canonical order, so a
    second deploy in the same tick sees the gold the first one spent. A valid deploy pays, cycles
    the hand and queues a pending spawn for deploy tick + delay ticks. With a delay of D > 0 ticks the
    units are in the state once State.Tick reaches deploy tick + D, in the Spawning state, unmoved.
    With D = 0 they appear during the deploy tick and move on it. The spawn delay must be a whole
    number of ticks that Q48.16 can represent exactly (0.25 s works, 0.05 s does not).
  - Spawn pattern: square spiral in steps of unitSpawnSpacing (0.5): center, front, left, right,
    back, then the diagonals, then the next ring. Rotated 180 degrees for player 1 so both players'
    formations face the enemy the same way. A position on a blocked cell moves to the nearest
    walkable cell center within 3 cells (ties: lower row, then left), else to the deploy target.
  - Units: id (from 1, never reused), owner, card, level, position, hp, effective stats (max hp, damage, move
    speed, range, attack interval; see "Stat modifiers, levels and leaders"), Rally buff, state (Spawning, Moving,
    Holding, Attacking, Capturing), objective (structure index or none), target (enemy unit id, structure index or
    none), attack cooldown, and the capture give-up counters (see "Map gold"). Combat and movement use the
    effective stats, never the card's base numbers. Stored in id order. Holding now means "nothing it can attack" (e.g. no enemy
    structure left); a unit in range of its target is Attacking.
    The client reads MatchState.Units, Structures, Projectiles, PendingSpawns, PendingSpells, SpellZones,
    Mines, Chests, Winner/EndReason and each player's Cards (Hand, NextCard, Queue; each card has a Kind,
    Unit or Spell), Gold and GoldFromMap; only the sim can change them.
  - Movement runs in two passes so update order cannot matter: every unit decides state, target,
    objective and velocity from the positions at the start of the tick, then moving units step
    (targeting details in "Combat and match resolution").
    - Spawning units act (and may attack) on their first tick.
    - A unit that is not locked onto a target re-picks its objective every tick: the nearest standing
      enemy structure by flow-field path distance (ground) or by straight-line distance to the
      footprint (flyers). Ties go to the lower structure index. No structure left: Holding with no
      objective. When any structure is destroyed, every unit re-picks its objective that same tick.
    - Within range of the target (before or after the step): Attacking. An arriving unit stops on the
      tick it arrives and attacks from the next one.
    - Ground direction: bilinear blend of the flow directions of the 4 cells around the unit,
      leaving out blocked cells and cells whose path cost differs from the unit's own cell by more
      than 2 (the other side of a wall); falls back to the unit's own cell direction.
    - Chasing an enemy unit (ground): the flow field toward the target's cell (built on demand, cached
      per cell until walkability changes), blended the same way; straight at the target once it is in
      the same or a neighboring cell. Flyers fly straight at the target.
    - Flyers fly straight at the footprint center, ignore terrain and are kept inside the map.
    - Separation (soft collision): each friendly unit of the same layer (ground or air) closer than
      unitSeparationDistance (0.6) pushes by (distance short / separation distance) along the line
      between them. Pushes from moving neighbors and from stopped (Attacking or Holding at tick
      start) neighbors are summed separately, each capped at 1. The stopped sum is scaled by
      unitStoppedPushFactor (0.3, placeholder), so it is always slower than any unit and cannot hold
      a unit short of its target; moving units may therefore overlap stopped friends a little.
      The stopped sum also adds a sideways slide of the same size as the (unweakened) sum,
      perpendicular to the unit's heading, toward the side it is already offset to (left when exactly
      head-on; left stays left under the map's 180-degree rotation, so both players behave alike).
      Being sideways, the slide never slows the unit. The total is scaled by
      unitSeparationPushPerSecond (1.5). Two units on exactly the same point split along X, lower id
      to the left. Stopped units are never pushed. Enemies do not push each other.
    - Speed: velocity = direction * moveSpeed + push, divided by the tick rate per step.
    - Ground step: take the full step if it lands on a walkable cell without cutting a blocked
      corner; otherwise the pure flow step (always open); otherwise stay. MatchSetup rejects content
      where moveSpeed + push * (2 + unitStoppedPushFactor) would exceed half a cell per tick, which keeps
      these checks sound. moveSpeed here is the fastest the deck's leader can make the unit (its passive, and
      its passive plus a Rally), so War Cry's +20% lowers the base speed limit from 6.55 to about 5.46.
- Combat and match resolution (sim/NovaFaction.Sim/Combat):
  - content/structures.json: formatVersion (1) and one entry per kind, "keep" and "tower" (the map file
    names): hp, damage, attackIntervalSeconds, range, targets, projectileSpeed, destructionBonus, optional
    "placeholder". All current numbers are placeholders: Keep 4000 HP, 90 damage every 1 s, range 6,
    bonus 1000; tower 2500 HP, 80 damage every 0.8 s, range 7, bonus 500; both target both layers and
    shoot at 10 units/s. The Keep attacks from the start (no Clash Royale style activation yet).
    Its content hash is part of the state hash. Each player's structures have a structure level (MatchSetup,
    default 1, 1..maxUnitLevel) that scales their hp and damage like a card level (StructureState.MaxHp and
    Damage); the other stats and the destruction bonus are not scaled.
  - Structures track HP (MatchState.Structures). At 0 HP a structure is destroyed: footprint walkable,
    flow fields rebuild, a tower's unlock zone goes to the attacker, every unit re-picks its objective.
  - Who can hit what: Ground attackers hit ground units and structures; Air attackers hit only flyers
    (and never structures); Both hits everything. Flyers are untouchable by Ground-only attackers.
  - Unit targeting, each tick, from start-of-tick state:
    - A unit keeps an enemy unit as its target while it lives, can be hit, and stays within the scan
      radius; a ground attacker also drops it when it is out of range and standing somewhere the
      attacker cannot walk to (e.g. over the river). A structure target is kept only while it stands
      and is in range (locked while attacking); otherwise the unit looks again every tick.
    - Scan radius = max(aggroRadius, the unit's range); aggroRadius is 5.5 (placeholder), so a unit
      never ignores an enemy it could already hit.
    - Scan: the nearest enemy it may attack within the radius, by straight-line distance (to the unit's
      center, or to the nearest point of a footprint). targetPriority any: enemy units and standing
      enemy structures; structuresOnly: structures only. Ties: units before structures, then lower id
      or index. No hit: the objective structure (see movement). An Air-only unit with no target still
      walks to its objective and Holds there.
    - Spreading melee: a melee unit scores an enemy unit as distance + meleeTargetCrowdPenalty (1,
      placeholder) per friendly unit already targeting it. Units decide in id order and each pick or
      drop updates the count at once, so friends deciding later in the same tick see it. Counts are
      per player, so the order never favors either player.
  - Range: unit to unit is center to center; unit to structure and structure to unit is to the nearest
    point of the footprint.
  - Attacks: attack cooldowns count down every tick in every state; a unit or structure whose target is
    in range attacks when its cooldown is 0, then waits attackIntervalSeconds (rounded to the nearest
    whole tick, at least 1). A fresh unit already in range attacks on its first tick. Melee damage
    lands the same tick. Ranged units and all structures fire a Projectile (from the unit's position or
    the footprint center) that first moves on the next tick, homes on the target at projectileSpeed,
    and always hits when it gets there (arrival = remaining distance <= one tick's travel). Aim point:
    the target unit's position (updated each tick while it lives) or the nearest footprint point to
    where it was fired from. If the target is dead or destroyed, the projectile flies on to the last
    aim point and fizzles there with no damage. Damage, splash and target layer are copied into the
    projectile when fired.
  - Structures shoot the nearest enemy unit in range they can hit (distance from the footprint, ties
    to the lower id) and keep that target while it stays in range.
  - Splash (optional splashRadius): damages every enemy unit the attacker could hit whose center is
    within the radius of the impact point, and every enemy structure whose footprint is. Priority does
    not matter for splash (a structuresOnly catapult's splash still hurts units). No friendly fire.
  - Tick order inside combat: cooldowns; unit decisions; structure decisions; projectiles in flight
    move and arrive; spell zones pulse and due spells land (see "Spells"); leader AreaDamage casts from this tick
    hit; units then structures attack; all hits apply in that order (projectile id, spell hits, ability hits in
    command order, unit id, structure index); moving units that are still alive step; units at 0 HP are removed; targets
    pointing at removed units or destroyed structures are cleared. All hits in a tick are
    simultaneous: a unit killed this tick still gets its attack.
  - Score: HP actually removed from enemy structures (a hit is capped by the HP left) plus the
    structure's destructionBonus when it falls. Spell damage counts like any other damage (also for
    sudden-death first damage). Killing units scores nothing.
  - Resolution (MatchState.Winner 0/1 or -1, EndReason, TieBreakRule):
    - A Keep destroyed: Ended at once, the attacker wins (KeepDestroyed), whatever the score. If both
      Keeps fall on the same tick: the higher score wins (Score), else the tie-break list.
    - Regulation clock at zero: higher score wins (Score). Equal: SuddenDeath with a fresh
      suddenDeathSeconds clock and income times suddenDeathIncomeMultiplier (a 0 s sudden death goes
      straight to the tie-break list).
    - Sudden death: the first tick with any structure HP removed ends the match (FirstDamage). If both
      players removed structure HP that tick, the one who removed more wins; exactly equal goes to the
      tie-break list. Unit-on-unit damage does not count.
    - Sudden death clock at zero: the tie-break list (TieBreak, TieBreakRule says which rule decided):
      1. more enemy structures destroyed; 2. higher HP on your own weakest standing structure
      (destroyed structures are left out, whatever HP value they kept; a player with no standing
      structure counts as 0); 3. more gold received from mines and chests (PlayerState.GoldFromMap,
      see "Map gold"); 4. a coin flip: SimRandom.NextInt(0, 2) from the match RNG, the only draw
      combat makes.
- Map gold (sim/NovaFaction.Sim/Economy, implemented Sept 2026):
  - MatchState.Mines: one per 'M' cell, in map order (bottom row first, then left to right). Neutral
    at the start. The cell is blocked for movement and deploys, but a mine is not a structure: it
    cannot be targeted or damaged, never becomes a unit objective, and scores nothing.
  - Presence: a unit (ground or air, any state, alive after this tick's combat) counts when its center
    is within mineCaptureRadius of the mine cell's center (<=). The radius must be at least the map's
    cell size, or units next to the mine could never reach it (MatchSetup checks this).
  - Capture, evaluated once per tick after combat: exactly one player present who is not the owner
    moves progress one tick toward their capture. If the other player has stored progress, it is
    first unwound one tick per tick; only at 0 does the new player's progress start. Progress
    reaching mineCaptureSeconds (in ticks) makes that player the owner and resets progress to 0.
    Both players present: progress pauses. Nobody present, or only the owner: progress decays one
    tick per tick toward 0 (decided Sept 2026: decay is as fast as capture, and the owner standing at
    the mine undoes an enemy's attempt). An owned mine is recaptured the same way; it stays with its
    owner until the enemy's capture completes.
  - Income: each owned mine adds mineIncomePerSecond; a player's mine income is capped at
    mineIncomeCap. It uses the exact accrual with its own carry (see "Sim match loop"), is doubled in
    sudden death like base income (decided Sept 2026: sudden death doubles all income), and a capture
    counts from the tick it completes. rules.json requires (goldBaseIncomePerSecond + mineIncomeCap)
    * suddenDeathIncomeMultiplier <= goldCap.
  - MatchState.Chests: one entry per 'C' spawn point, in map order, with IsPresent. A wave comes at
    chestFirstSpawnSeconds of match time and every chestSpawnIntervalSeconds after (sudden death
    included; match time = ticks since the start). It fills every empty spawn point; a waiting chest
    stays as it is, so chests never stack. A wave due at 0 s is there at the start; a wave due on the
    tick the match ends still appears (harmless). Both times must be whole ticks.
  - Collection, after mine capture: any unit (either player, ground or air) whose center is within
    chestCollectRadius of the spawn cell's center takes the chest on the first tick it is there,
    measured after this tick's movement. If several units qualify, the closest one wins (exact
    distance), then the lower unit id. A unit already standing on a spawn point takes a new chest on
    the tick after it appears. The owner gets chestGold at once, limited by the gold cap; the chest is
    used up even if the player is full.
  - GoldFromMap per player = mine income and chest gold actually added to the bank (gold lost at the
    cap does not count). Tie-break rule 3 uses it.
  - Placeholder values and the 1/3 target (twolane, 3:00, base income 0.35/s):
    - a turtle earns 0.35 * 180 = 63 gold;
    - mines: mineIncomePerSecond 0.05, mineIncomeCap 0.1 (both mines). Allowing for walking there,
      the 4 s capture (mineCaptureSeconds) and losing a mine now and then, assume both are held for
      about 120 s: 0.1 * 120 = 12 gold;
    - chests: waves at 30, 60, 90, 120 and 150 s (chestFirstSpawnSeconds 30, chestSpawnIntervalSeconds
      30) at 4 spawn points = at most 20 chests in regulation; an active player who takes 12 of them
      at chestGold 0.75 gets 9 gold;
    - 12 + 9 = 21 = 63 / 3, so the active player banks about 84 against the turtle's 63, if they keep
      spending so the cap does not swallow it.
    - mineCaptureRadius 1.5 (cells next to the mine, diagonals included) and chestCollectRadius 0.75
      (a unit walking through the spawn cell) are feel values. Capturing units stop at a mine they pass (see
      below), so one unit is enough. Tune all eight in the headless harness.
  - Capture behavior (decided and implemented Sept 2026): units with canCapture stop at mines they pass.
    - canCapture (units.json, optional): defaults to true for ground units with targetPriority any and false
      otherwise. It may be set to false for any unit; setting it to true for a flyer or a structures-only unit
      is a load error.
    - Each tick, a capturer that finds no enemy to attack (the normal target scan within its scan radius comes
      up empty) and whose center is within mineCaptureRadius of a mine its player does not own (neutral or
      the enemy's) is Capturing: it stands still with no target and does not move. A moving capturer that
      comes within the radius stops on that same tick, like a unit arriving at its target.
    - It stays until its player owns the mine; on the next tick it walks on toward its objective. Units never
      turn toward a mine; they only stop when their normal path takes them within the radius.
    - Combat first: an enemy it may attack (a unit it can hit, or an enemy structure) within its scan radius
      makes it fight as usual. When the fight ends it re-decides: still within the radius of a mine it does
      not own, it captures again; otherwise it goes on toward its objective. An enemy it cannot hit (a flyer
      over a ground-only knight) does not interrupt it but still contests the mine, which pauses progress.
    - The capture itself is unchanged: presence still counts every unit near the mine, flyers and
      non-capturers included (they just do not stop). A Capturing unit counts as stopped for separation.
    - mineCaptureSeconds reduced from 5 to 4 (placeholder).
  - Capture give-up (decided and implemented Sept 2026; resolves the "stuck at a contested mine" open item):
    - After mine capture each tick, every Capturing unit checks whether its capture moved forward: some mine within
      mineCaptureRadius advanced for its player this tick (its own progress grew, the capture completed, or the
      other player's stored progress was unwound because this player was alone). If so its stall count is 0;
      otherwise the count grows by one. A unit that is not Capturing (walking, fighting) has a count of 0, so a
      fight in between starts the count over.
    - When the count reaches mineCaptureGiveUpSeconds (3 s = 60 ticks, placeholder) the unit gives up: the count
      resets and the unit ignores mines on the next mineCaptureRetrySeconds of ticks (6 s = 120, placeholder).
      It stays Capturing for the rest of that tick and walks on toward its objective from the next one, like after
      a finished capture. While ignoring mines it never stops at one (the passing stop is off too); presence at a
      mine still counts as usual.
    - Example: a knight contested by an enemy flyer from tick 10 gives up on tick 69, moves from tick 70, and may
      stop at a mine again from tick 190. A capturer that cannot leave (speed 0) captures 60 ticks, ignores 120,
      captures 60 again, and so on.
    - In practice only a contested mine stalls a capture: a lone capturer always moves progress.
- Spells (sim/NovaFaction.Sim/Content/SpellBook.cs, CardCatalog.cs, Spells/, implemented Sept 2026):
  - Card model: a card is a unit card (UnitDefinition, units.json) or a spell card (SpellDefinition,
    spells.json); both derive from CardDefinition (id, displayName, cost, kind, slot, isLeader, placeholder).
    Only units can be leaders. CardCatalog joins a faction's two files: the factions must match and card ids
    must be unique across both (a clash is an error pointing into spells.json). Its content hash (both files'
    parsed data) replaces the unit roster hash in the state hash.
  - content/factions/<faction>/spells.json: formatVersion (1), faction, spells (may be empty). Each spell: id,
    displayName, cost (whole number 1-7), radius (> 0), damage (>= 0, per hit), castDelaySeconds (>= 0; the
    spell lands this long after the cast), durationSeconds (>= 0; 0 = instant, one hit), targets (ground, air,
    both). Optional: zoneTickSeconds (required when durationSeconds > 0, not allowed when it is 0; > 0 and at
    most durationSeconds), structureDamageMultiplier (>= 0, default 0.35, placeholder), placeholder. Unknown
    keys are errors. Times are at most 600 s and must be whole ticks at the match's tick rate (MatchSetup
    checks this for spells in the decks, like the spawn delay).
  - content/factions/fantasy/spells.json (all numbers placeholders): fireball (cost 4, radius 2.5, 325 damage,
    lands after 1 s, instant, both layers) and blizzard (cost 3, radius 3, 30 damage every 0.5 s for 4 s,
    lands after 0.5 s, both layers; 8 pulses, 240 damage in total). Both use the 0.35 structure multiplier, so
    a Fireball removes about 113.75 structure HP.
  - Casting: DeployCard with a spell card is valid when the slot holds it, gold >= cost, and the target is
    inside the map rectangle (0 <= x < width * cellSize, same for y; blocked cells, the river, mines and
    structures included; deploy zones do not matter). A valid cast pays, cycles the hand like a unit card, and
    adds a pending spell (MatchState.PendingSpells: id from 1 in cast order, owner, spell, target, land tick =
    cast tick + delay in ticks, end tick = land tick + duration in ticks, pulse interval in ticks).
  - Resolution, inside the combat step after projectiles move and before units attack: first every active
    zone (MatchState.SpellZones, id order) due a pulse hits; then every pending spell whose land tick has come
    (id order) hits. A zero-delay spell lands on its cast tick. An instant spell is then gone; a zone joins
    the zone list (its landing hit is its first pulse) and pulses on land tick + k * interval while before
    its end tick, and is removed after its last active tick (end tick - 1). So a 4 s zone with 0.5 s pulses
    hits 8 times.
  - A spell hit damages every enemy unit it may hit (targets, same rule as attacks) whose center is within
    radius of the target, and every standing enemy structure whose footprint is within radius (unless the
    spell is air only), at damage * structureDamageMultiplier. Positions are the start of the tick, as for
    splash. No friendly fire. Structure damage scores, destroys structures (bonus, unlocked zones) and counts
    as sudden-death first damage exactly like attack damage. Spells use no randomness.
  - Decks keep 8 cards with exactly one leader; the fire_spirit unit is gone (Fireball replaces it).
  - Spell levels (decided Sept 2026): a spell card's level scales its damage (units and structures) by the same
    level factor as unit damage, fixed at cast time (SpellInstance.Damage and StructureDamage). Leader passives and
    Rally buffs never change spells.
- Stat modifiers, levels and leaders (sim/NovaFaction.Sim/Content/Modifiers.cs, LeaderAbility.cs, implemented Sept 2026):
  - Modifier: stat (hp, damage, moveSpeed, range, attackInterval), kind (multiply or add), value, appliesTo ("all"
    or a non-empty list of unit slots; "spell" is not allowed, no duplicates). JSON:
    { "stat": "damage", "kind": "multiply", "value": 1.1, "appliesTo": ["bruiser", "swarm"] }. All four keys are
    required. A multiply value is the factor (1.1 = +10%), 0..100; an add value is -1000000..1000000.
  - Effective stat = (base * level factor + sum of add values) * (1 + sum of (multiply value - 1)), over every
    modifier (leader passive plus Rally buff) whose appliesTo matches the unit's slot (decided Sept 2026: bonuses
    add up rather than compound, so +10% and +30% make +40%; Fix sums are exact, so modifier order never matters).
    Results are clamped: hp, range and attack interval at least the smallest positive Fix, damage and speed at
    least 0. A multiply on attackInterval above 1 makes attacks slower. Only hp and damage have a level factor.
  - Levels: level factor = 1 + levelStatBonusPerLevel * (level - 1), placeholder 0.06 (the doc's 5-7%), so a level 5
    card has 1.24x hp and damage. maxUnitLevel is 15 (placeholder). 0.06 is not exact in Q48.16 (0.0599976), so a
    level 5 golem has 2231.98 hp rather than 2232; that is deterministic and fine.
  - Effective stats are computed when a unit is created and whenever its Rally buff is added, replaced or removed.
    When max hp changes, current hp keeps its share: hp * newMax / oldMax, in one exact 128-bit step, rounded to
    the nearest raw unit, never below the smallest positive value for a living unit.
  - Leader passive (units.json "passive", leaders only, optional, default none): a list of modifiers that applies
    to every unit of the deck's owner for the whole match, from tick 0, whether or not the leader was ever deployed
    or is alive (it comes from the deck, not the unit). It applies to units only, never to structures or spells.
    Placeholder: warlord +10% damage for bruiser and swarm (knight 154, goblins 66).
  - Leader ability (units.json "ability", leaders only, optional): displayName, type (areaDamage, rally, heal),
    radius (> 0), range (>= 0), cooldownSeconds (> 0, at most 600), plus per type: rally: durationSeconds (> 0, at
    most 600) and modifiers (at least one); areaDamage: damage (>= 0) and optional structureDamageMultiplier (default
    0.35 like spells); heal: amount (> 0). Keys of another type are errors. Times must be whole ticks (MatchSetup).
    Placeholder: warlord "War Cry", rally, +30% damage and +20% move speed for all units, 5 s, radius 4, range 6,
    cooldown 20 s.
  - LeaderAbility command (target = world point) is used when all hold: the deck's leader has an ability; a living
    unit of the player whose card is a leader is on the field (a Spawning unit counts; a pending spawn does not)
    with the target within the ability's range of its center (<=, any such leader will do); and State.Tick >= the
    player's AbilityReadyTick (0 at the start: no initial cooldown). Otherwise it is ignored and only
    IgnoredAbilities grows. A use sets AbilityReadyTick = tick + cooldown ticks (a 20 s cooldown used on tick 0
    allows the next use on tick 400).
  - Effects, on units whose center is within radius of the target (<=), taken at command time (start-of-tick
    positions), in unit id order:
    - rally: every living friendly unit (the leader included, both layers) gets the buff: the modifiers until
      ExpireTick = cast tick + duration ticks. It is active in the cast tick's combat and removed at the end of tick
      ExpireTick - 1 (when State.Tick reaches ExpireTick), so a 5 s rally lasts exactly 100 ticks. A unit has at
      most one buff: a new Rally replaces the old one (refreshing its time) instead of stacking. Units created later
      and units that walk into the radius later are not buffed. The buff outlives the leader.
    - heal: every living friendly unit gains amount hp, capped at its max hp.
    - areaDamage: resolved in this tick's combat like an instant spell hitting both layers: every enemy unit in
      radius takes the damage and every enemy structure whose footprint is in radius takes damage *
      structureDamageMultiplier (scores, destroys, counts as sudden-death damage).
    - Heal and areaDamage amounts scale with the leader card's level factor (decided Sept 2026).
  - Only one leader per player can be on the field (see "Match rules"; changed Sept 2026, a second copy used to be
    allowed when the card cycled round while the first was alive). The cooldown is per player either way.
- Controllers and bots (sim/NovaFaction.Sim/Controllers, Bots, HeadlessMatch.cs; implemented Sept 2026):
  - IController (Player, AddCommands(state, output)) supplies one player's commands. Simulation builds one per player
    from the MatchSetup: a BotController for a player given a bot personality, otherwise a HumanController.
  - HumanController: commands from outside (touch, network, script, replay). Submit(command) queues a command for its
    tick; SubmitAll(log) queues a player's whole recorded log. Queued commands stay queued until their tick has run,
    so a Tick() that throws loses nothing; a queued command for a tick that already ran makes Tick() throw.
  - Simulation.Tick() / Tick(commands): outside commands (the old path) are still allowed, but only for players with a
    HumanController (for a bot player they are an input-layer error). Then human controllers add their queued
    commands, then bots decide (player order), all reading the state at the start of the tick. Everything goes into
    the CommandLog and is applied in canonical order as before, so bot matches replay from the log alone.
    Controllers are not match state and are not in the state hash; their effects are (through the commands).
  - HeadlessMatch.Run(setup, seed, scripted commands?) runs a match to its end and returns a MatchResult (winner, end
    reason, tie-break rule, ticks, final hash, the Simulation and its log). Scripted commands go to the human players;
    one for a bot player is an error. A full 3-minute bot-vs-bot match on twolane takes about 0.2 s (Debug build,
    Sept 2026); a test requires under 2 s.
  - Bot personalities: content/bots/<id>.json with formatVersion (1), id, reactionDelaySeconds (above 0, at most 10,
    whole ticks, checked by MatchSetup.WithBot), decisionQuality, aggression, defensiveness, mineFocus, spellUsage (all
    0..1), goldReserve (0..1000), optional placeholder. Unknown keys are errors. Shipped (all placeholders):
    balanced (0.75 s, quality 0.8, aggression 0.5, defensiveness 0.5, mines 0.4, spells 0.5, reserve 2), aggressive
    (0.5 s, 0.8, 0.9, 0.3, 0.2, 0.6, 0), turtle (1 s, 0.8, 0.15, 0.9, 0.2, 0.4, 5), swarm (0.5 s, 0.7, 0.7, 0.4, 0.7,
    0.3, 1).
  - Bot randomness (decided Sept 2026): each bot has its own SimRandom seeded with a SplitMix64 finalizer of
    (match seed + golden-ratio constant * (player + 1)). It never touches the match RNG.
  - Decision loop (decided Sept 2026): the bot decides on tick 0 and then every reactionDelaySeconds (the delay is the
    decision period, not a perception lag). Each decision scores candidate card actions and plays at most one, then
    scores ability actions and uses at most one. With probability decisionQuality it takes the best score, otherwise
    one of the top three (by score, ties in generation order) at random. Before issuing it re-checks the sim's rules
    (gold, hand slot, deploy zone or map bounds, leader already on the field, ability cooldown and leader range), so a
    bot never has a command rejected.
  - What the bot sees: units, pending deploys (both players'; a drop is visible while it spawns), structures, mines,
    its own hand and gold. "Own half" = closer to its own Keep's footprint center than to the enemy Keep's.
  - Unit value (the bot's currency for threats and spells) = card cost / spawn count, times hp / max hp for units on the
    field (a pending deploy counts at full value).
  - (a) Threats: enemy units and pending deploys on the bot's half, grouped by the nearest standing own structure.
    Own units able to fight units (targetPriority any) and own pending deploys within 4 of a group's value-weighted
    center count against it. (b) Defense: a group whose net value is at least 0.5 + 4 * (1 - defensiveness) is answered
    with a unit card from the hand that the bot can pay for (the reserve may be spent on defense). Matchups:
    structuresOnly cards never defend; cards that cannot hit air are skipped against mostly-flyer groups; bonuses for
    anti-air against flyers, splash against swarms (spawn count 3+ or swarm slot), damage per second against tanks (tank
    slot or max hp 1000+), and fast melee (speed 1.25+) against a lone ranged unit. Melee drops halfway from the
    structure's nearest footprint point to the group center, ranged a quarter of the way; the nearest deployable point is
    used when that spot is not deployable.
  - (c) Attack: lanes are the enemy forward towers; a lane holds its tower and the enemy Keep, and the lane with the
    smallest share of its hp left is pushed (target: its tower while it stands, else the Keep). Attack waves drop on the
    deployable cell nearest the target, preferring cells at least 1 outside every standing enemy structure's range. If an
    own tank (tank slot or max hp 1000+, so the warlord counts) is already pushing that lane, other cards drop 2 behind
    it; otherwise a tank card leads, and other cards lead with a lower score that grows with aggression. Attacks keep
    goldReserve plus 3 * (1 - aggression) gold in hand, but a bot at the gold cap always may spend.
  - (c2) Saving (decided and implemented Sept 2026, replacing "spend whatever you can afford"): every unit card in hand
    is scored for the attack whether or not the bot can pay for it. When the best of those is one it cannot afford and
    it outranks everything it can, the bot holds that card's whole cost back on top of the reserve and the attack wait,
    so a cheaper card is only played when there is still enough left for the card the plan wants. This is what makes an
    aggressive bot play its 6-cost leader at all; before it, the leader never reached the field. Defence never respects
    the saving (an unanswered push costs more than a missed leader), sudden death turns it off, and a bot at the gold
    cap may always spend, so saving can never make it bank gold the cap would swallow. Mine and chest drops and
    attacking spells may break into the savings, but only when they outrank the best attack the bot just declined;
    otherwise saving would simply push the gold into whatever cheap action was left.
  - (d) Mines: with mineFocus above 0, the cheapest capture-capable non-leader card in hand is dropped on the deployable
    cell nearest to each mine the bot does not own, unless an own capturer (on the field or pending) is within 6 of it.
    On twolane no deploy cell touches a mine, so this sends the capturer down that mine's lane and it stops as it passes
    (see "Capture behavior"). Keeps goldReserve.
  - (d2) Chests (implemented Sept 2026; the bot used to ignore chests entirely and walk its units past its own): a chest
    drop is the attack it replaces, routed via the chest. The cheapest non-leader unit card in hand is dropped on the
    deployable cell nearest the chest, scored as that card's attack utility plus what the chest is worth
    (3 * chestGold * mineFocus), and paid for exactly like that attack, savings included. A chest is fetched only when
    it is no farther from the bot's own Keep than from the enemy's (a chest exactly on the center line counts for
    both), no own unit or pending deploy is within 4 of it, the drop point is within 6 of the chest, and the drop point
    is within 6 of the lane point the bot is pushing, so the bot never splits its army across the map for one chest.
    Off in sudden death. Units still never walk to a chest on purpose; the drop only points one that way, and a drop on
    the spawn cell itself collects the chest as soon as the unit appears.
  - (e) Spells: every enemy unit the spell can hit, every enemy structure it can hurt, and the value-weighted center of
    what each such candidate hits are tried as aim points. Value = for each enemy unit in radius its unit value times the
    share of its hp the spell removes (all pulses, level scaled; at most 1), plus 8 per enemy structure the spell would
    destroy, or 100 per enemy structure in radius during sudden death (first damage wins). It casts when the value
    exceeds cost * (1.5 - spellUsage). A cast on its own half ranks above a unit drop for the same threat (it may use the
    reserve); elsewhere it keeps the reserve and ranks with attacks.
  - (f) Leader ability, only with a living leader and the cooldown over. Rally: at an enemy structure with at least 3 own
    units targeting it or within 3 of it, aimed at their center (pulled toward the leader to within its range), used if
    at least 3 own units are inside the radius. Heal: where the most missing hp (at most one heal amount per unit) is in
    radius, if that is at least two heal amounts. AreaDamage: valued like a spell, used above 3 * (1.5 - spellUsage).
  - (g) Sudden death: all in. goldReserve and the attack wait drop to 0, aggression counts as 1, mine drops stop, and
    spells and AreaDamage aim at enemy structures. Defense works as usual.
  - Brain constants (thresholds, utilities, radii above) are code in BotController, not content: they are how the bot
    thinks, not game balance. Move them to data if tuning needs it.
- server/: ASP.NET Core (C#). Accounts, economy, matchmaking, input relay, match verification by
  re-running the sim. PostgreSQL. Runs on the Windows desktop for LAN testing; cloud container later.
- content/: JSON data for units, factions, maps, missions. Art in Addressables bundles per theme.
- Netcode: custom deterministic lockstep with input relay. Photon Quantum is the fallback;
  decision at end of M1.

## Balance targets (Sept 2026)
The numbers a tuning session aims at. They are measured with the headless harness on twolane: a round robin with
100 matches per ordered pairing for the win rates and the income ratio, and balanced-vs-balanced batches of 200
matches for the mirror numbers. Every value they are reached with is still a placeholder.
- Turtle personality overall win rate at or below 45%; no personality above 60% or below 40%.
- Active-versus-turtle income ratio between 1.25 and 1.40 (this replaces the older "about 1/3 more" wording with a
  band; the target itself is unchanged).
- Keep-kill rate in balanced mirror matches between 20% and 35%, and at least one forward tower destroyed in at
  least 60% of those matches.
- Sudden death in fewer than 10% of matches.
- No card whose structure damage per gold or kills per gold is more than double the median card's. The harness
  pools both sides and prints each card against the median (batch, "Card efficiency"); structures and leader
  abilities are left out because no gold is paid for them.
Progress against them is logged per iteration in docs/balance-log.md.

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
- Replays are verified in-process; the server side (M3) still has to store and verify them. Replay files are not
  forward-compatible: a SimVersion bump makes older replays unplayable (they can still be exported to JSON).
- Unit levels: the sim scales them (Sept 2026); where they come from (server progression data, league level
  caps) is decided with M3. The level numbers (maxUnitLevel 15, +6% per level) are placeholders.
- Leader numbers (the warlord passive and War Cry) are placeholders; the second fantasy leader has no passive or
  ability yet. The client can show the cooldown from
  PlayerState.AbilityReadyTick and buffs from Unit.Buff.
- Enemy units do not block or push each other (decided to leave as is with combat; revisit if fights
  look wrong on the phone).
- Spell numbers (cost, radius, damage, delays, the 0.35 structure multiplier) are placeholders; tune in the
  headless harness. Spells have no travel visual data yet (the client can
  animate the cast delay from PendingSpells' land tick).
- Keep activation: the Keep shoots from the start. Clash Royale only wakes the king tower once it is
  hit or a tower falls; decide when tuning.
- Combat numbers (structure HP/damage/range, aggro radius, stopped push, crowd penalty) are
  placeholders; with the shipped numbers towers win most fights against a trickle of units. Tune in
  the headless harness.
- Map gold numbers (all eight rules.json values) are placeholders built on the arithmetic in "Map gold". The harness
  (Sept 2026) shows active bots do not earn more than the turtle (ratio about 1.0, target 1.33); tune the numbers and/or
  the bots' mine and chest behavior.
- Units still never walk to a mine or chest on purpose (their objective is always a structure); since
  Sept 2026 capturers stop at mines they happen to pass. The bot deploys capturers toward mines and cheap units toward
  its own chests on purpose (Sept 2026); mission design must too. Revisit if players find mines hard to hold.
- Capture give-up (Sept 2026) replaced the old "held at a contested mine for good" behavior; its 3 s / 6 s
  times are placeholders to tune in the headless harness.
- Crowding at structures (fixed Sept 2026 with the weak stopped push and sideways slide): a busy
  scripted battle test requires that every living unit is Attacking or Holding at the end.
- SimVersion is 2 (Sept 2026): the bot saves toward the card its plan wants and fetches chests on its own side, so
  replays written against version 1 no longer re-run.
- Bot (Sept 2026): personality values and the brain constants are placeholders; tune them in the headless harness.
  Bot vs bot on the shipped numbers is close to a stalemate: towers win most fights, so matches usually end on a
  small score difference and rarely by a Keep kill (a lone balanced bot does destroy a passive opponent's Keep). The
  bot does not chase chests, does not predict unit movement when aiming spells, and does not yet read the
  opponent's likely hand. Campaign difficulty knobs (enemy levels, income multiplier) are not wired to bots yet.
  Harness baseline (Sept 2026): aggressive is the weakest personality (23% overall) and never plays its leader; no
  pairing produces Keep kills. The leader and chest problems were fixed Sept 2026 (see "Controllers and bots" (c2)
  and (d2)); two known weaknesses remain, both design rather than bugs: every non-tank card scores the same attack
  utility, so the card the bot pushes with comes down to hand slot order and cost (the catapult is nearly a dead
  card), and the bot has no model of tower fire, so it cannot tell a card that will reach a structure from one that
  dies on the way.
