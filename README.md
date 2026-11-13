# Bet Prediction · Touchline

An open-source football forecasting dashboard. Touchline uses Elo ratings and a Poisson score model to estimate 90-minute results, goal totals, and combinations of markets. Confirmed scores update forecasts for upcoming matches. **Model performance** shows how the estimates scored on a chronological replay.

- Browse upcoming fixtures and results by competition.
- Inspect home win, draw, away win, over/under 2.5 goals, and both teams to score probabilities.
- Compare the model with a competition-frequency baseline using top-pick accuracy and log loss.
- Try the synthetic Demo League without API keys or a live data feed.

## Run locally

Install the .NET 10 SDK and Python 3.11 or newer. From the project folder, run the web app and open [the local demo](http://127.0.0.1:5077/?competition=DEMO) or [browse live fixtures](http://127.0.0.1:5077/).

```sh
dotnet run --project src/Bet.Web --urls http://127.0.0.1:5077
```

Set `BET_PYTHON` to your interpreter's executable if `python` on `PATH` points elsewhere. On systems that expose Python 3 as `python3`, set `BET_PYTHON=python3` in the environment before starting the app. The worker uses the Python standard library and reads one JSON request from stdin. The request contains `history`, `fixtures`, and an optional `asOf`. Historical rows carry team IDs, kickoff, and regulation goals. An optional `completedAt` records when the final score was known.

## Data feeds and refresh

- The default source is [OpenLigaDB](https://www.openligadb.de/). It requires no API key and provides [open data under ODbL](https://www.openligadb.de/lizenz). The league picker marks supported competitions with **Open feed**. Coverage includes the Champions League, Europa League, Conference League, Premier League, La Liga, Bundesliga, 2. Bundesliga, 3. Liga, and DFB-Pokal. Results and in-play scores depend on when the community updates each fixture.
- A local Champions League snapshot can be placed at `src/Bet.Web/Data/uefa-ucl-snapshot.json` for offline use. The app expects an `asOf` timestamp, a `sourceUrl`, and a `matches` array. Each match carries `id`, `kickoff`, `home`, `away`, `homeTeamId`, `awayTeamId`, and nullable `homeScore` and `awayScore`. Use data you have permission to store. The Demo League generates its own illustrative fixtures and results.
- The active page checks the selected competition every 60 seconds. `BET_FIXTURE_CACHE_SECONDS` defaults to 120 for current-season matches and accepts a minimum of 60 seconds. Previous seasons cache for a day. These are request intervals. The source controls how quickly a score becomes available.
- `BET_UPCOMING_DAYS` defaults to 35. `BET_RESULTS_DAYS` defaults to 21. Both accept values from 1 to 365.

Confirmed regulation-time results update the next pre-match forecast. In-progress scores appear in the view while the model waits for a final result. The dashboard shows the feed timestamp, forecast cutoff, and kickoff of the latest result used. Forecasts and tuning run in memory and reuse the previous output when the inputs match.

The existing [API-Football](https://www.api-football.com/documentation-v3) and [The Odds API](https://the-odds-api.com/liveapi/guides/v4/) integrations are optional. They activate only when configured with `BET_API_FOOTBALL_KEY` or `BET_ODDS_API_KEY`, respectively. Bookmaker markets vary by competition and region and can consume extra API quota when a match's market browser is opened.

## Optional read-only SQL Server feed

Set `BET_SQLSERVER_CONNECTION_STRING` or `ConnectionStrings__BetSqlServer` if you already have a SQL Server fixture feed. The application queries it when the other fixture feeds are unavailable. Use an account with `SELECT` permission on `dbo.BetFixtureFeed`. The app opens the connection with `ApplicationIntent=ReadOnly` and runs a parameterized `SELECT`.

The table or view should expose one row per `Competition` and `FixtureId`.

| Columns | SQL Server type and meaning |
| --- | --- |
| `Competition` | `varchar(32)`, dashboard competition ID such as `UCL` or `EPL` |
| `FixtureId`, `HomeTeamId`, `AwayTeamId` | String IDs |
| `HomeName`, `AwayName` | Display names |
| `Kickoff`, `ObservedAt` | `datetimeoffset`, kickoff and data observation time |
| `Status` | `upcoming`, `live`, `finished`, `postponed`, `unconfirmed`, or `closed` |
| `HomeScore`, `AwayScore` | Nullable `int`, scoreboard result |
| `RegulationHomeScore`, `RegulationAwayScore` | Nullable `int`, completed 90-minute result used for training |

## How the estimates are evaluated

The Python worker combines an Elo result model with a Poisson score model. It chooses the 1X2 blend, team-form shrinkage, Elo half-life, Elo update factor, and goal-rate scale on a chronological tuning block. The update factor controls how strongly a new result changes a team's rating. Only tuning matches whose scores were available before the test window can influence these settings.

Model Performance shows the test window, top-pick accuracy, and log loss from a historical replay. Each replay uses the scores available before kickoff, with a three-hour delay from kickoff when the final-score timestamp is unknown. Live forecasts are cached in memory.

A replay of the first 72 Champions League league-phase matches of 2026/27 gave 47 correct top picks (65.3%) against 40 (55.6%) for the frequency baseline. The model's 1X2 log loss was 0.844 against 0.933 for the baseline. It projected 3.44 goals per match against 4.04 observed. Other competitions use their own history and may score differently. [UEFA results used for the replay](https://www.uefa.com/uefachampionsleague/news/02a8-2174c9e9019d-f909a77bd77a-1000--2026-27-champions-league-all-the-league-phase-fixtures-and-results/)

Forecast inputs are fixture details and confirmed 90-minute results. Accounting for injuries, suspensions, or starting lineups requires timestamped player data and historical coverage to validate how those signals change the estimates.

The [22-competition benchmark](BENCHMARK.md) shows where the revised model improved and where it struggled.

The across-match slip uses one outcome per fixture. It multiplies individual probabilities under an independence assumption. Use **Build Same Game Multi** to combine outcomes from different markets within a match. For supported result and goal markets, the app adds the probabilities of scorelines where every leg wins. For example, Arsenal to win and over 2.5 goals only includes winning Arsenal scorelines with at least three goals. Corners, cards and player props require a separate statistics feed and joint model. Optional individual sportsbook quotes do not supply a combined same-game price.

## Contribute

Fork the repository, push your change to a branch in your fork, and open a pull request into `main`. The maintainer reviews and merges contributions. See [the contribution guide](CONTRIBUTING.md) for checks and evaluation requirements.

## License

The project code is available under the [MIT License](LICENSE). Third-party scores, fixtures, and odds remain subject to their providers' terms.
