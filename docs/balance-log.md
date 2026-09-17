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
