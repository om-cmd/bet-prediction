using System.Text.Json;

namespace Bet.Web;

internal sealed record CompetitionView(string Id, string Name, string Group, string? Country, bool KeyFree);
internal sealed record StorageView(string Mode, string Message);
internal sealed record Dashboard(string Mode, CompetitionView[] Competitions, CompetitionView Competition, DateTimeOffset FetchedAt,
    DateTimeOffset? FixturesFetchedAt, DateTimeOffset? HistoryFetchedAt, DateTimeOffset? OddsFetchedAt,
    DateTimeOffset? ForecastAsOf, DateTimeOffset? LatestForecastResultKickoff,
    string? SourceUrl, string OddsSourceUrl, string OddsRegion, MatchView[] Matches, ModelView? Model,
    Multiple[] SuggestedMultiples, string[] Warnings, StorageView? Storage);
internal sealed record ModelPerformance(ModelView? Model, CompetitionView[] Competitions, CompetitionView Competition,
    DateTimeOffset? HistoryFetchedAt, DateTimeOffset? ForecastAsOf, DateTimeOffset? LatestForecastResultKickoff,
    string? SourceUrl, string[] Warnings);
internal sealed record MatchView(string Id, DateTimeOffset Kickoff, string Home, string Away, string Status,
    int? HomeScore, int? AwayScore, double? ExpectedHomeGoals, double? ExpectedAwayGoals, Scoreline[] TopScorelines, Market[] Markets);
internal sealed record Market(string Id, string Name, Outcome[] Outcomes, string? Group = null, bool ScoreModel = false);
internal sealed record Outcome(string Id, string Name, double? Probability, decimal? Price, string? Bookmaker, Quote[] Quotes,
    double? ImpliedProbability = null);
internal sealed record ModelView(string Name, int TrainingMatches, int ValidationMatches, int TestMatches, bool Tuned,
    double BlendWeight, double? LogLoss, double? BaselineLogLoss, double? PoissonLogLoss, double? EloLogLoss,
    double? TotalGoalsLogLoss, double? Over25LogLoss, double? BothTeamsScoreLogLoss, double? PredictedTotalGoals,
    double? ObservedTotalGoals, double? GoalScale, int? EloHalfLifeDays = null, int? EloUpdate = null,
    int? CorrectPicks = null, int? BaselineCorrectPicks = null,
    DateTimeOffset? FirstTestKickoff = null, DateTimeOffset? LastTestKickoff = null);
internal sealed record Leg(string MatchId, string MarketId, string OutcomeId);
internal sealed record Multiple(string Name, Leg[] Legs, double Probability, decimal? Price, string? Bookmaker, double? ExpectedValue);

internal sealed class DashboardService(FootballFeed football, OpenLigaFeed openLiga, OddsFeed odds, ExtraOddsFeed extraOdds, ForecastRunner model, SqlMatchStore sql,
    IConfiguration configuration, ILogger<DashboardService> logger)
{
    private readonly Lazy<Snapshot?> _snapshot = new(() => ReadSnapshot(logger));
    private readonly int _upcomingDays = Math.Clamp(configuration.GetValue("BET_UPCOMING_DAYS", 35), 1, 365);
    private readonly int _resultsDays = Math.Clamp(configuration.GetValue("BET_RESULTS_DAYS", 21), 1, 365);

    public async Task<Dashboard> GetAsync(Competition competition, FeedResult<Competition[]> catalog, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (catalog.Warning is { } catalogWarning)
            warnings.Add(catalogWarning);
        var feed = await LoadFixturesAsync(competition, DateTimeOffset.UtcNow, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        warnings.AddRange(feed.Warnings);
        var upcoming = Upcoming(feed, now);
        var forecastTask = ForecastAsync(competition, feed, upcoming, now, cancellationToken);
        var pricesTask = upcoming.Length > 0 && odds.IsConfigured && competition.OddsKey is { } sport
            ? odds.GetAsync(sport, cancellationToken)
            : Task.FromResult(new FeedResult<OddsFeed.OddsEvent[]>(null, null, null));
        await Task.WhenAll(forecastTask, pricesTask);
        var forecasts = await forecastTask;
        if (forecasts.Warning is { } forecastWarning)
            warnings.Add(forecastWarning);

        var prices = await pricesTask;
        if (prices.Warning is { } priceWarning)
            warnings.Add(priceWarning);
        var evaluatedAt = DateTimeOffset.UtcNow;
        var predictionById = (forecasts.Value?.Predictions ?? []).ToDictionary(prediction => prediction.Id);
        var matches = feed.Matches.Where(match => IsVisible(match, evaluatedAt)).OrderBy(match => match.Kickoff).ThenBy(match => match.Id)
            .Select(match => ToView(match, predictionById.GetValueOrDefault(match.Id), prices.Value ?? [], evaluatedAt)).ToArray();

        var competitionViews = catalog.Value!.Select(ToCompetitionView).ToArray();
        return new(feed.Mode, competitionViews, ToCompetitionView(competition), evaluatedAt, feed.FetchedAt, feed.FetchedAt,
            prices.FetchedAt, forecasts.AsOf, forecasts.LatestResultKickoff, feed.SourceUrl, OddsFeed.SourceUrl, odds.Region,
            matches, ToModelView(forecasts.Value?.Tuning),
            SuggestMultiples(matches), warnings.Distinct().ToArray(), feed.Storage);
    }

    public async Task<ModelPerformance> GetPerformanceAsync(Competition competition, FeedResult<Competition[]> catalog,
        CancellationToken cancellationToken)
    {
        var feed = await LoadFixturesAsync(competition, DateTimeOffset.UtcNow, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var forecast = await ForecastAsync(competition, feed, Upcoming(feed, now), now, cancellationToken, includeTuning: true);
        var warnings = feed.Warnings.Concat(new[] { catalog.Warning, forecast.Warning }.OfType<string>()).Distinct().ToArray();
        return new(ToModelView(forecast.Value?.Tuning), catalog.Value!.Select(ToCompetitionView).ToArray(),
            ToCompetitionView(competition), feed.FetchedAt, forecast.AsOf, forecast.LatestResultKickoff, feed.SourceUrl, warnings);
    }

    public async Task<MatchMarketsResponse?> GetMatchMarketsAsync(Competition competition, string matchId, CancellationToken cancellationToken)
    {
        var context = await LoadMatchAsync(competition, matchId, cancellationToken);
        if (context is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        var extraTask = odds.IsConfigured
            ? extraOdds.GetAsync(competition.OddsKey, context.Match, cancellationToken)
            : Task.FromResult(new FeedResult<Market[]>([], null, null));
        var pricesTask = odds.IsConfigured && competition.OddsKey is { } sport
            ? odds.GetAsync(sport, cancellationToken)
            : Task.FromResult(new FeedResult<OddsFeed.OddsEvent[]>(null, null, null));
        await Task.WhenAll(extraTask, pricesTask);
        var extra = await extraTask;
        var prices = await pricesTask;
        if (context.Match.Kickoff <= DateTimeOffset.UtcNow)
            return null;

        var featured = ToView(context.Match, null, prices.Value ?? [], now).Markets
            .Select(market => market with { Outcomes = market.Outcomes.Where(outcome => outcome.Quotes.Length > 0).ToArray() })
            .Where(market => market.Outcomes.Length > 0);
        var warnings = new[] { context.Warning, extra.Warning }.OfType<string>().ToList();
        if (!odds.IsConfigured)
            warnings.Add("This score model covers results and goals. The key-free feed does not supply the historical corners, cards or player statistics needed for those estimates.");

        var markets = SameGameMarkets.Build(context.Match, context.Prediction, featured.Concat(extra.Value ?? []));
        return new(markets, warnings.Distinct().ToArray(), extra.FetchedAt ?? prices.FetchedAt);
    }

    public async Task<SameGameEstimate?> GetSameGameEstimateAsync(Competition competition, string matchId, SameGameLeg?[] legs,
        CancellationToken cancellationToken)
    {
        var context = await LoadMatchAsync(competition, matchId, cancellationToken);
        return context is null ? null : SameGameMarkets.Estimate(context.Prediction, legs);
    }

    private async Task<MatchContext?> LoadMatchAsync(Competition competition, string matchId, CancellationToken cancellationToken)
    {
        var feed = await LoadFixturesAsync(competition, DateTimeOffset.UtcNow, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var upcoming = Upcoming(feed, now);
        var match = upcoming.FirstOrDefault(item => item.Id == matchId);
        if (match is null)
            return null;

        var forecasts = await ForecastAsync(competition, feed, upcoming, now, cancellationToken);
        return match.Kickoff <= DateTimeOffset.UtcNow ? null :
            new(match, forecasts.Value?.Predictions.FirstOrDefault(prediction => prediction.Id == match.Id), forecasts.Warning);
    }

    private FootballMatch[] Upcoming(FixtureData feed, DateTimeOffset now) => feed.Matches
        .Where(match => match.Status == "upcoming" && match.Kickoff > now && match.Kickoff <= now.AddDays(_upcomingDays))
        .OrderBy(match => match.Kickoff).ThenBy(match => match.Id).ToArray();

    private static ModelView? ToModelView(Tuning? tuning) => tuning is null ? null : new("Poisson + Elo",
        tuning.TrainingMatches ?? tuning.HistoryMatches,
        tuning.ValidationMatches, tuning.TestMatches, tuning.Status == "tuned", tuning.PoissonWeight, tuning.HybridLogLoss,
        tuning.BaselineLogLoss, tuning.PoissonLogLoss, tuning.EloLogLoss, tuning.TotalGoalsLogLoss, tuning.Over25LogLoss,
        tuning.BothTeamsScoreLogLoss, tuning.PredictedTotalGoals, tuning.ObservedTotalGoals, tuning.GoalRateScale,
        tuning.EloHalfLifeDays, tuning.EloUpdate, tuning.CorrectPicks, tuning.BaselineCorrectPicks,
        tuning.FirstTestKickoff, tuning.LastTestKickoff);

    private async Task<FixtureData> LoadFixturesAsync(Competition competition, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (competition.Id == Competitions.DemoId)
            return new("demo", DemoFixtures(now), now, null, []);

        var warnings = new List<string>();
        if (OpenLigaFeed.Supports(competition))
        {
            var open = await openLiga.GetAsync(competition, cancellationToken);
            if (open.Warning is { } warning)
                warnings.Add(warning);
            if (open.Value is { Length: > 0 } matches)
                return new("openliga", matches, open.FetchedAt, OpenLigaFeed.SourceUrl, warnings.ToArray());
        }

        if (football.IsConfigured)
        {
            var requests = competition.RecentSeasons(now).Select((season, offset) => football.GetSeasonAsync(competition,
                season, offset == 0, cancellationToken)).ToArray();
            var seasons = await Task.WhenAll(requests);
            warnings.AddRange(seasons.Select(result => result.Warning).OfType<string>());
            if (seasons[0].Value is { } current && (current.Length > 0 || competition.Id != "UCL"))
            {
                var matches = seasons.SelectMany(result => result.Value ?? []).DistinctBy(match => match.Id).ToArray();
                return new("live", matches, seasons[0].FetchedAt, FootballFeed.SourceUrl, warnings.ToArray());
            }

            if (seasons[0].Warning is null)
                warnings.Add("API-Football returned an empty current Champions League season.");
        }
        StorageView? storage = null;
        if (sql.IsConfigured)
        {
            try
            {
                var stored = await sql.LoadMatchesAsync(competition.Id, now.AddYears(-10), now.AddDays(_upcomingDays), cancellationToken);
                storage = new("sqlserver", stored.Matches.Length > 0 ? "Read-only SQL Server fixtures" : "No SQL Server fixtures for this competition");
                if (stored.Matches.Length > 0)
                    return new("sqlserver", stored.Matches, stored.FetchedAt, null, warnings.ToArray(), storage);
            }
            catch (FeedException exception)
            {
                storage = new("unavailable", exception.Message);
                warnings.Add(exception.Message);
            }
        }

        if (competition.Id == "UCL" && _snapshot.Value is { } snapshot)
        {
            var matches = snapshot.Matches.Select(match => new FootballMatch(match.Id, match.Kickoff, match.Home, match.Away,
                match.HomeTeamId, match.AwayTeamId, match.HomeScore.HasValue && match.AwayScore.HasValue ? "finished" : "upcoming",
                match.HomeScore, match.AwayScore, match.HomeScore, match.AwayScore)).ToArray();
            return new("snapshot", matches, snapshot.AsOf, snapshot.SourceUrl, warnings.ToArray(), storage);
        }

        if (warnings.Count == 0)
            warnings.Add("The key-free score feed does not cover this competition.");
        return new("unavailable", [], null, null, warnings.ToArray(), storage);
    }

    private async Task<ForecastData> ForecastAsync(Competition competition, FixtureData feed, FootballMatch[] fixtures,
        DateTimeOffset now, CancellationToken cancellationToken, bool includeTuning = false)
    {
        if (fixtures.Length == 0 && !includeTuning)
            return new(null, null);

        var cutoff = (feed.Mode is "snapshot" or "sqlserver") && feed.FetchedAt is { } observed && observed < now ? observed : now;
        var history = feed.Matches.Where(match => match.Status == "finished" &&
                match.RegulationHomeScore is >= 0 and <= 30 && match.RegulationAwayScore is >= 0 and <= 30)
            .OrderBy(match => match.Kickoff).ThenBy(match => match.Id)
            .Select(match => new HistoryRow(match.Id, competition.Id, match.Kickoff, match.HomeTeamId, match.AwayTeamId,
                match.RegulationHomeScore!.Value, match.RegulationAwayScore!.Value, EarlyFinalObservation(match, feed)))
            .Where(row => AvailableAt(row) < cutoff).ToArray();

        if (history.Length == 0)
            return new(null, "No completed regulation scores were available for the model.");

        // Keep the cache stable between equivalent fixture pulls while still advancing time decay daily.
        var dayStart = new DateTimeOffset(cutoff.UtcDateTime.Date, TimeSpan.Zero);
        var latestCompletion = history.Max(AvailableAt).AddMilliseconds(1);
        var liveOrigin = latestCompletion > dayStart ? latestCompletion : dayStart;
        var asOf = (feed.Mode is "snapshot" or "sqlserver") || liveOrigin > cutoff ? cutoff : liveOrigin;
        var targets = fixtures.Select(match => new ForecastFixture(match.Id, competition.Id, match.Kickoff, match.HomeTeamId, match.AwayTeamId)).ToArray();
        var forecasts = await model.GetAsync(competition.Id, new(history, targets, asOf), cancellationToken);
        return forecasts.Value is null ? new(null, forecasts.Warning) :
            new(forecasts.Value, forecasts.Warning, asOf, history[^1].Kickoff);
    }

    // Live fixtures supply a final status at fetch time. Older records use the worker's three-hour availability delay.
    private static DateTimeOffset? EarlyFinalObservation(FootballMatch match, FixtureData feed) =>
        (feed.Mode is "live" or "openliga") && feed.FetchedAt is { } fetched && fetched > match.Kickoff && fetched < match.Kickoff.AddHours(3)
            ? fetched : null;

    private static DateTimeOffset AvailableAt(HistoryRow row) => row.CompletedAt ?? row.Kickoff.AddHours(3);

    private bool IsVisible(FootballMatch match, DateTimeOffset now) => match.Status != "closed" &&
        match.Kickoff >= now.AddDays(-_resultsDays) && match.Kickoff <= now.AddDays(_upcomingDays) &&
        (match.Status != "upcoming" || match.Kickoff > now);

    private static CompetitionView ToCompetitionView(Competition competition) =>
        new(competition.Id, competition.Name, competition.Group, competition.Country, OpenLigaFeed.Supports(competition));

    private static MatchView ToView(FootballMatch match, Prediction? prediction, OddsFeed.OddsEvent[] events, DateTimeOffset now)
    {
        Market[] markets = [];
        if (match.Status == "upcoming" && match.Kickoff > now)
        {
            var quotes = OddsFeed.GetQuotes(OddsFeed.MatchEvent(match, events), now);

            Outcome Selection(string market, string id, string name, double? probability)
            {
                var options = quotes.GetValueOrDefault((market, id)) ?? [];
                var best = options.FirstOrDefault();
                return new(id, name, probability, best?.Price, best?.Bookmaker, options);
            }

            markets =
            [
                new("h2h", "Match result (90 min)", [Selection("h2h", "home", match.Home, prediction?.HomeWin),
                    Selection("h2h", "draw", "Draw", prediction?.Draw), Selection("h2h", "away", match.Away, prediction?.AwayWin)]),
                new("totals25", "Total goals 2.5", [Selection("totals25", "over", "Over 2.5", prediction?.Over25),
                    Selection("totals25", "under", "Under 2.5", prediction?.Under25)]),
                new("btts", "Both teams to score", [Selection("btts", "yes", "Yes", prediction?.BothTeamsScore),
                    Selection("btts", "no", "No", prediction?.BothTeamsScoreNo)]),
            ];
        }

        return new(match.Id, match.Kickoff, match.Home, match.Away, match.Status, match.HomeScore, match.AwayScore,
            prediction?.ExpectedHomeGoals, prediction?.ExpectedAwayGoals, prediction?.TopScorelines ?? [], markets);
    }

    private static Multiple[] SuggestMultiples(MatchView[] matches)
    {
        var choices = matches.SelectMany(match => match.Markets.Where(market => market.Id == "h2h")
            .SelectMany(market => market.Outcomes.Where(outcome => outcome.Probability.HasValue)
                .Select(outcome => (Leg: new Leg(match.Id, market.Id, outcome.Id), Outcome: outcome))))
            .OrderByDescending(choice => choice.Outcome.Probability).DistinctBy(choice => choice.Leg.MatchId).Take(3).ToArray();

        // For a fixed leg count, the largest independence product comes from the strongest outcome in each of the top fixtures.
        return new[] { 2, 3 }.Where(count => choices.Length >= count).Select(count =>
        {
            var picks = choices.Take(count).ToArray();
            var chance = picks.Aggregate(1.0, (product, pick) => product * pick.Outcome.Probability!.Value);
            var quote = picks.SelectMany(pick => pick.Outcome.Quotes).GroupBy(item => item.Bookmaker)
                .Where(book => book.Count() == count)
                .Select(book => (Bookmaker: book.Key, Price: book.Aggregate(1m, (product, item) => product * item.Price)))
                .OrderByDescending(book => book.Price).FirstOrDefault();
            var priced = quote.Bookmaker is not null;
            return new Multiple($"Highest model chance · {count} legs", picks.Select(pick => pick.Leg).ToArray(), chance,
                priced ? quote.Price : null, quote.Bookmaker, priced ? chance * (double)quote.Price - 1 : null);
        }).ToArray();
    }

    private static Snapshot? ReadSnapshot(ILogger logger)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "uefa-ucl-snapshot.json");
            return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The local UEFA fixture snapshot could not be read.");
            return null;
        }
    }

    private static FootballMatch[] DemoFixtures(DateTimeOffset now)
    {
        string[] teams = ["Northport", "Redbridge", "Westhaven", "Southfield"];
        var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var matches = new List<FootballMatch>();
        for (var offset = -120; offset <= 4; offset++)
        {
            if (offset == 0)
                continue;

            var kickoff = day.AddDays(offset).AddHours(18);
            var dayNumber = (int)(kickoff - DateTimeOffset.UnixEpoch).TotalDays;
            var home = dayNumber % teams.Length;
            var away = (home + 1 + dayNumber / teams.Length % (teams.Length - 1)) % teams.Length;
            var finished = offset < 0;
            int? homeGoals = finished ? (dayNumber + home * 2) % 5 : null;
            int? awayGoals = finished ? (dayNumber + away * 3) % 4 : null;
            matches.Add(new($"demo-{dayNumber}", kickoff, teams[home], teams[away], teams[home], teams[away],
                finished ? "finished" : "upcoming", homeGoals, awayGoals, homeGoals, awayGoals));
        }
        return matches.ToArray();
    }

    private sealed record FixtureData(string Mode, FootballMatch[] Matches, DateTimeOffset? FetchedAt, string? SourceUrl, string[] Warnings,
        StorageView? Storage = null);
    private sealed record ForecastData(ForecastResponse? Value, string? Warning, DateTimeOffset? AsOf = null,
        DateTimeOffset? LatestResultKickoff = null);
    private sealed record MatchContext(FootballMatch Match, Prediction? Prediction, string? Warning);
    private sealed record Snapshot(string SourceUrl, DateTimeOffset AsOf, SnapshotMatch[] Matches);
    private sealed record SnapshotMatch(string Id, DateTimeOffset Kickoff, string Home, string Away, string HomeTeamId, string AwayTeamId,
        int? HomeScore, int? AwayScore);
}
