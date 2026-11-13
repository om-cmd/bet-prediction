# Forecast benchmark

The 13 November 2026 audit replayed 90-minute match forecasts across the 22 competitions in the standard selector. The revised model picked **1,253 of 2,537 results (49.4%)**, compared with **1,246 (49.1%)** for the previous model. Match-weighted log loss moved from **1.0240 to 1.0217**. The revised probabilities scored better in seven competitions, tied in nine, and scored worse in six.

Each replay uses the latest season or tournament edition in the listed source. The model selects its settings using the available earlier seasons or editions. Team form and Elo update before each kickoff using earlier completed matches. The replay assumes a result is available three hours after kickoff if the source lacks a final-score timestamp. The Women's Champions League uses date-level cutoffs. The app caches live forecasts in memory, so these are reconstructed forecasts.

Top-pick accuracy counts the highest-probability 90-minute result. Lower log loss means better probability estimates. The aggregate weights each match equally, so competitions with larger samples have more influence.

| Competition | Matches | Previous correct | Revised correct | Log loss, previous → revised | Latest scored date (UTC) |
| --- | ---: | ---: | ---: | ---: | --- |
| [Champions League](https://www.uefa.com/uefachampionsleague/news/02a8-2174c9e9019d-f909a77bd77a-1000--2026-27-champions-league-all-the-league-phase-fixtures-and-results/) | 72 | 42 | 49 | 0.8879 → 0.8405 | 2026-11-04 |
| [Premier League](https://www.football-data.co.uk/data.php) | 100 | 46 | 44 | 1.0347 → 1.0402 | 2026-11-08 |
| [EFL Championship](https://www.football-data.co.uk/data.php) | 180 | 79 | 78 | 1.0701 → 1.0729 | 2026-11-07 |
| [La Liga](https://www.football-data.co.uk/data.php) | 119 | 65 | 63 | 0.9728 → 0.9691 | 2026-11-08 |
| [Serie A](https://www.football-data.co.uk/data.php) | 110 | 65 | 67 | 0.8999 → 0.8793 | 2026-11-08 |
| [Ligue 1](https://www.football-data.co.uk/data.php) | 90 | 49 | 50 | 0.9900 → 0.9915 | 2026-11-08 |
| [Bundesliga](https://api.openligadb.de/getmatchdata/bl1/2026) | 81 | 43 | 46 | 0.9616 → 0.9814 | 2026-11-08 |
| [2. Bundesliga](https://api.openligadb.de/getmatchdata/bl2/2026) | 99 | 49 | 49 | 1.0521 → 1.0521 | 2026-11-08 |
| [3. Liga](https://api.openligadb.de/getmatchdata/bl3/2026) | 130 | 60 | 60 | 1.0459 → 1.0459 | 2026-11-08 |
| [DFB-Pokal](https://api.openligadb.de/getmatchdata/dfb/2026) | 48 | 31 | 31 | 0.9134 → 0.9134 | 2026-10-28 |
| [Europa League](https://api.openligadb.de/getmatchdata/uel2026/2026) | 72 | 29 | 29 | 1.0572 → 1.0572 | 2026-11-05 |
| [Conference League](https://api.openligadb.de/getmatchdata/uecl2026/2026) | 54 | 26 | 26 | 1.0678 → 1.0678 | 2026-11-05 |
| [A-League Men](https://fixturedownload.com/results/aleague-men-2026) | 23 | 10 | 10 | 1.1344 → 1.1344 | 2026-11-08 |
| [Eredivisie](https://fixturedownload.com/results/eredivisie-2026) | 102 | 52 | 54 | 1.0162 → 1.0151 | 2026-11-07 |
| [MLS](https://fixturedownload.com/results/mls-2026) | 510 | 235 | 235 | 1.0604 → 1.0607 | 2026-11-08 |
| [Liga Portugal](https://fixturedownload.com/results/primeira-liga-2026) | 91 | 51 | 53 | 0.9179 → 0.8808 | 2026-11-06 |
| [Brasileirão Série A](https://www.football-data.co.uk/brazil.php) | 338 | 168 | 163 | 1.0231 → 1.0308 | 2026-11-06 |
| [FIFA World Cup](https://github.com/openfootball/worldcup.json) | 104 | 46 | 46 | 1.0686 → 1.0686 | 2026-07-19 |
| [UEFA European Championship](https://github.com/openfootball/euro.json) | 51 | 22 | 22 | 1.0780 → 1.0750 | 2024-07-14 |
| [FIFA Women's World Cup](https://fixturedownload.com/results/fifa-womens-world-cup-2023) | 64 | 33 | 33 | 1.0219 → 0.9990 | 2023-08-20 |
| [FIFA Club World Cup](https://github.com/openfootball/club-worldcup) | 63 | 30 | 30 | 1.0624 → 1.0624 | 2025-07-13 |
| [Women's Champions League](https://www.uefa.com/womenschampionsleague/news/02a9-2184a4a3a3d8-3d2bf80e5c0e-1000--women-s-champions-league-2026-27-league-phase-fixtures-and-/) | 36 | 15 | 15 | 1.1100 → 1.1100 | 2026-11-12 |

The archives vary in coverage. The Europa League and Conference League had no prior-season regulation results in the tested OpenLigaDB feeds, so the replay used default settings. The MLS replay covers the regular season. Recent Brazilian, Dutch, and Portuguese fixtures outside the archives are outside the scored sample. The replay uses the model's standard home and away inputs for tournament fixtures, including neutral venues.

The Champions League figure above uses settings fixed at the start of the season. The separate 47-of-72 result in the README uses the dashboard's tuning split on the full historical snapshot.
