# NovaFaction — balance log

One entry per tuning iteration: what changed, why, and the numbers it produced. The targets live in
docs/design.md under "Balance targets". Every value here is still a placeholder.

## How the numbers are measured

Release build of the harness, on twolane, always with the same seeds so two iterations differ only by what changed:

```powershell
dotnet build -c Release tools\NovaFaction.Harness\NovaFaction.Harness.csproj
dotnet run -c Release --no-build --project tools\NovaFaction.Harness -- roundrobin --matches-per-pair 100 --map twolane --out tools\out\<iteration>
dotnet run -c Release --no-build --project tools\NovaFaction.Harness -- batch --matches 200 --p0 balanced --p1 balanced --map twolane --out tools\out\<iteration>
```

The round robin gives the per-personality win rates and the active-versus-turtle income ratios (seeds 1..100 for each
of the 16 ordered pairings). The 200-match balanced mirror gives the Keep-kill rate, the tower-kill rate, the
sudden-death rate and the per-card efficiency table.

| Target | Where it is read |
| --- | --- |
| Turtle at or below 45%, nobody above 60% or below 40% | round robin, "Overall" table |
| Active-versus-turtle income 1.25-1.40 | round robin, "Active vs turtle income" table |
| Keep-kill rate 20-35% | mirror batch, "Keep-kill rate" |
| A forward tower down in 60%+ of matches | mirror batch, "Tower-kill rate" |
| Sudden death under 10% | mirror batch, "Sudden death" |
| No card over 2x the median card | mirror batch, "Card efficiency" |

## Baseline (before this session)

The shipped placeholders, with no bot or balance change yet. This is what the rest of the log is measured against.

Round robin, 100 matches per ordered pairing (1600 matches):

| bot | played | win rate | gold/match |
| --- | --- | --- | --- |
| turtle | 800 | 69.6% | 74.3 |
| balanced | 800 | 58.2% | 74.3 |
| swarm | 800 | 48.9% | 74.1 |
| aggressive | 800 | 23.2% | 73.1 |

Overall rates: Keep kill 0.0%, tower kill 0.2%, sudden death 3.8%.
Active-versus-turtle income, mean over the six mixed pairings: **1.001** (aggressive 0.99, balanced 1.01, swarm 1.00).

Balanced mirror, 200 matches:

| stat | value |
| --- | --- |
| win rate | P0 59.0% / P1 41.0% |
| Keep-kill rate | 0.0% |
| tower-kill rate | 0.5% |
| sudden death | 6.0% |
| match length | 180.2 s +/- 0.8 |
| score margin (absolute) | 267 +/- 291 |
| gold per side | 74.4 (63.1 base, 8.5 mine, 2.9 chest) |
| chests collected per side | 3.8 of 20 |

Card efficiency, both sides pooled (median 4.7 structure damage per gold, 0.163 kills per gold):

| card | gold | dmg/gold | x median | kills/gold | x median |
| --- | --- | --- | --- | --- | --- |
| catapult | 350 | 50.7 | **10.88** | 0.000 | 0.00 |
| fireball | 2944 | 0.7 | 0.15 | 0.325 | 2.00 |
| elf_archer | 4800 | 6.0 | 1.30 | 0.263 | 1.62 |
| goblin_pack | 4770 | 5.0 | 1.07 | 0.240 | 1.48 |
| griffin | 5736 | 4.9 | 1.04 | 0.107 | 0.66 |
| warlord | 2436 | 0.2 | 0.05 | 0.167 | 1.03 |
| knight | 6004 | 0.8 | 0.18 | 0.158 | 0.97 |
| stone_golem | 2820 | 4.5 | 0.96 | 0.000 | 0.00 |

Targets met at the baseline: sudden death (3.8% / 6.0%). Everything else is missed, most of them badly: turtle wins
69.6%, aggressive wins 23.2%, the income ratio is 1.00 instead of 1.25-1.40, and essentially no structure ever falls,
so the Keep-kill and tower-kill targets are at zero. The catapult is a huge outlier on structure damage per gold, but
it is deployed only 0.17 times per match, so that number rests on 70 deploys over 200 matches.

## Step 1 — bot fixes (no balance number changed)

Three changes to the bot brain, all in sim/NovaFaction.Sim/Bots/BotController.cs. No content file and no balance
number was touched, so the difference below is the bot alone. SimVersion is now 2.

**(a) Saving for the card the plan wants.** The bot used to spend every coin it could afford, so its 6-cost leader
never came out: by the time it had 6 gold it had already bought two cheap cards. `AddAttack` now scores every unit
card in hand whether or not the bot can pay for it. If the best of those is one it cannot afford and it outranks
everything it can, the bot holds that card's whole cost back (`SavingsGoal`) and only plays something cheaper when
there is still enough left for the goal. A bot at the gold cap may always spend, so saving never makes it bank gold
it would lose, and defence is never held back — an unanswered push costs more than a missed leader.

The first version of this pushed the held gold straight into whatever cheap action was left (a mine drop, a chest),
which is the opposite of saving. So a mine or chest drop, or an attacking spell, now has to beat the best attack the
bot just declined (`_savingsFloor`) before it may break into the savings.

**(b) Chests.** The bot had no chest behaviour at all: on twolane both of a player's own chests sit one row behind
its own front line, and the timelines showed the enemy's units walking over them while the owner's units marched
the other way. A chest drop is now modelled as the attack it replaces, routed via the chest: the cheapest non-leader
card in hand, at the deployable cell nearest the chest, scored as that card's attack utility plus what the chest is
worth (3 x chestGold x mineFocus), and paid for exactly like that attack. Only chests no farther from the bot's own
Keep than from the enemy's are fetched, only when no own unit or pending deploy is already within 4 of the chest,
and only when the drop point is within 6 of the lane point the bot is pushing — otherwise the bot splits its army
across the map for 0.75 gold. Skipped in sudden death, where only structure damage wins.

**(c) Other bugs.** None found that were clearly bugs rather than design weaknesses. What was checked: the bot never
issues a command the sim rejects (an existing test already pins this); the lane, deploy-point and threat-grouping
code is correct as written; command sequence numbers are per tick and never collide. The 59%/41% P0 win rate in the
baseline mirror is seed variance, not a side bias: the same 400 matches from seed 5000 give 51.7%/48.2%.

Two real weaknesses were found and deliberately left alone, because fixing either is a design change rather than a
bug fix:
- Every non-tank card scores the same attack utility, so which card the bot pushes with comes down to hand slot
  order and cost. The catapult is the visible symptom: it is deployed 0.17 times per match while the knight goes
  3.8 times, because at the same cost of 5 the Stone Golem always outranks it on the tank bonus and it can never
  defend (structures only).
- The bot has no model of tower fire, so it cannot tell a card that will reach a structure from one that dies on
  the way. That is the whole reason attacking loses to defending here.

### Numbers after step 1

Round robin, 100 matches per ordered pairing:

| bot | baseline | after step 1 | gold/match (baseline -> now) |
| --- | --- | --- | --- |
| turtle | 69.6% | **61.9%** | 74.3 -> 74.2 |
| balanced | 58.2% | **54.9%** | 74.3 -> 74.6 |
| swarm | 48.9% | **51.7%** | 74.1 -> 75.1 |
| aggressive | 23.2% | **31.5%** | 73.1 -> 74.1 |

Overall rates: Keep kill 0.0% (unchanged), tower kill 0.5% (0.2%), sudden death 3.5% (3.8%).
Active-versus-turtle income: **1.013** (1.001).

Balanced mirror, 200 matches: Keep kill 0.0%, tower kill 0.0%, sudden death 4.5%, mean gold 75.0/74.2 per side
(63.1 base, 8.6/7.7 mine, 3.4 chest), chests collected 4.5 per side (3.8 at the baseline).

The leader is the clearest win. In 100 aggressive-versus-aggressive matches the warlord was deployed **0 times** at
the baseline and **1.25-1.46 times per match** now, and War Cry went from never to 0.36-0.43 uses per match. The
balanced bot against a do-nothing opponent still destroys the Keep in 100% of 20 matches (95% at the baseline).

No target is met by the bot fixes alone, but the spread of personalities is much tighter (the gap between the best and the
worst personality fell from 46.4 to 30.4 points) and the economy target moved
the right way. Everything else needs balance numbers.
