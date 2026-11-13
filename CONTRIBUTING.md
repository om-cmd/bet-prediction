# Contributing

1. Fork the repository and create a branch for your change.
2. Run the checks below and exercise the change locally. The Demo League works without external credentials.
3. Open a pull request into `main` explaining what changed and how you checked it. The maintainer reviews the pull request and decides when it merges.

```sh
dotnet build src/Bet.Web/Bet.Web.csproj --configuration Release
python -m unittest discover -s forecasting -v
```

## Forecast and feed changes

For a model change, compare the previous and proposed models on the same chronological holdout. List the competition, date range, match count, top-pick accuracy, and log loss. Document the time at which each result was available and any cutoff assumptions. Use 90-minute results and report how the model performs against the competition-frequency baseline.

For a feed change, explain which seasons are available and how the feed represents regulation time, extra time, and missing results. Use synthetic fixtures in tests. Keep API keys, connection strings, and provider files without a redistribution license out of pull requests.
