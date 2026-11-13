using System.Globalization;

namespace Bet.Web;

internal sealed record SameGameLeg(string? MarketId, string? OutcomeId);
internal sealed record SameGameRequest(string? Competition, string? MatchId, SameGameLeg?[]? Legs);
internal sealed record SameGameEstimate(double? Probability, string? Reason);
internal sealed record MatchMarketsResponse(Market[] Markets, string[] Warnings, DateTimeOffset? OddsFetchedAt);

internal static class SameGameMarkets
{
    private const string ResultGroup = "Match result";
    private const string GoalsGroup = "Goals";
    private const string CorrectScore = "correct-score";
    private static readonly ScoreMarket[] Definitions = CreateDefinitions();
    private static readonly Dictionary<string, ScoreMarket> ById = Definitions.ToDictionary(market => market.Id);

    public static Market[] Build(FootballMatch match, Prediction? prediction, IEnumerable<Market> offered)
    {
        var odds = offered.GroupBy(market => market.Id).ToDictionary(group => group.Key, group => group.First() with
        {
            Outcomes = group.SelectMany(market => market.Outcomes).GroupBy(outcome => outcome.Id).Select(MergeQuotes).ToArray(),
        });
        var markets = new List<Market>();
        foreach (var definition in Definitions)
        {
            var quotedMarket = odds.GetValueOrDefault(definition.Id);
            var quotedOutcomes = (quotedMarket?.Outcomes ?? []).ToDictionary(outcome => outcome.Id);
            var scoreSelections = definition.Selections;
            if (definition.Id == CorrectScore)
            {
                var additional = quotedOutcomes.Keys.Except(scoreSelections.Select(selection => selection.Id)).Select(ParseScore)
                    .OfType<ScoreSelection>();
                scoreSelections = scoreSelections.Concat(additional).ToArray();
            }

            var outcomes = new List<Outcome>();
            foreach (var selection in scoreSelections)
            {
                quotedOutcomes.Remove(selection.Id, out var quote);
                var chance = prediction?.ScoreDistribution is { Length: > 0 } scores
                    ? Math.Clamp(scores.Where(selection.Wins).Sum(score => score.Probability), 0, 1)
                    : (double?)null;
                if (chance is null && quote is null)
                    continue;

                outcomes.Add(new(selection.Id, WithTeams(selection.Name, match), chance, quote?.Price, quote?.Bookmaker,
                    quote?.Quotes ?? [], quote?.ImpliedProbability));
            }

            outcomes.AddRange(quotedOutcomes.Values);
            if (outcomes.Count > 0)
                markets.Add(new(definition.Id, WithTeams(definition.Name, match), outcomes.ToArray(), definition.Group,
                    outcomes.Any(outcome => outcome.Probability.HasValue)));
        }

        markets.AddRange(odds.Values.Where(market => !ById.ContainsKey(market.Id)));
        return markets.ToArray();
    }

    public static SameGameEstimate Estimate(Prediction? prediction, SameGameLeg?[] legs)
    {
        var selections = legs.Select(leg => FindSelection(leg!.MarketId!, leg.OutcomeId!)).ToArray();
        if (selections.Any(selection => selection is null))
            return new(null, "A joint estimate is available for match results and goal selections. Corners, cards and player markets need separate match data and a joint model.");
        if (prediction?.ScoreDistribution is not { Length: > 0 } scores)
            return new(null, "A score forecast is unavailable for this fixture.");

        bool AllWin(Scoreline score) => selections.All(selection => selection!.Wins(score));

        var chance = Math.Clamp(scores.Where(AllWin).Sum(score => score.Probability), 0, 1);
        if (chance > 0)
            return new(chance, null);

        // Confirm logical compatibility separately from the tiny score probabilities pruned by Python.
        for (var home = 0; home <= 24; home++)
        {
            for (var away = 0; away <= 24; away++)
            {
                if (AllWin(new(home, away, 0)))
                    return new(null, "This combination falls below the score model’s reporting precision.");
            }
        }

        return new(0, "These selections cannot all win on the same scoreline.");
    }

    private static ScoreSelection? FindSelection(string marketId, string outcomeId)
    {
        if (marketId == CorrectScore)
            return ParseScore(outcomeId);

        return ById.GetValueOrDefault(marketId)?.Selections.FirstOrDefault(selection => selection.Id == outcomeId);
    }

    private static ScoreSelection? ParseScore(string id)
    {
        var parts = id.Split('-');
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var home) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var away) && home <= 24 && away <= 24 &&
            id == $"{home}-{away}"
            ? Score(home, away) : null;
    }

    private static Outcome MergeQuotes(IGrouping<string, Outcome> group)
    {
        var source = group.First();
        var quotes = group.SelectMany(outcome => outcome.Quotes).OrderByDescending(quote => quote.UpdatedAt)
            .ThenByDescending(quote => quote.Price).DistinctBy(quote => quote.Bookmaker).OrderByDescending(quote => quote.Price).ToArray();
        var best = quotes.FirstOrDefault();
        return source with { Price = best?.Price, Bookmaker = best?.Bookmaker, Quotes = quotes,
            ImpliedProbability = group.Select(outcome => outcome.ImpliedProbability).FirstOrDefault(value => value.HasValue) };
    }

    private static ScoreMarket[] CreateDefinitions()
    {
        var markets = new List<ScoreMarket>
        {
            new("h2h", "Match result (90 min)", ResultGroup,
            [
                new("home", "{home} to win", score => score.HomeGoals > score.AwayGoals),
                new("draw", "Draw", score => score.HomeGoals == score.AwayGoals),
                new("away", "{away} to win", score => score.HomeGoals < score.AwayGoals),
            ]),
            new("double-chance", "Double chance", ResultGroup,
            [
                new("home-draw", "{home} or draw", score => score.HomeGoals >= score.AwayGoals),
                new("draw-away", "{away} or draw", score => score.HomeGoals <= score.AwayGoals),
                new("home-away", "Either team to win", score => score.HomeGoals != score.AwayGoals),
            ]),
            new("win-to-nil", "Win to nil", ResultGroup,
            [
                new("home", "{home} to win to nil", score => score.HomeGoals > 0 && score.AwayGoals == 0),
                new("away", "{away} to win to nil", score => score.AwayGoals > 0 && score.HomeGoals == 0),
            ]),
            new("winning-margin", "Winning margin", ResultGroup,
            [
                new("home-1", "{home} by 1", score => score.HomeGoals - score.AwayGoals == 1),
                new("home-2", "{home} by 2", score => score.HomeGoals - score.AwayGoals == 2),
                new("home-3plus", "{home} by 3+", score => score.HomeGoals - score.AwayGoals >= 3),
                new("draw", "Draw", score => score.HomeGoals == score.AwayGoals),
                new("away-1", "{away} by 1", score => score.AwayGoals - score.HomeGoals == 1),
                new("away-2", "{away} by 2", score => score.AwayGoals - score.HomeGoals == 2),
                new("away-3plus", "{away} by 3+", score => score.AwayGoals - score.HomeGoals >= 3),
            ]),
            new("btts", "Both teams to score", GoalsGroup,
            [
                new("yes", "Yes", score => score.HomeGoals > 0 && score.AwayGoals > 0),
                new("no", "No", score => score.HomeGoals == 0 || score.AwayGoals == 0),
            ]),
            new("exact-total-goals", "Exact total goals", GoalsGroup, Enumerable.Range(0, 5)
                .Select(goals => new ScoreSelection($"{goals}", $"{goals}", score => score.HomeGoals + score.AwayGoals == goals))
                .Append(new("5plus", "5+", score => score.HomeGoals + score.AwayGoals >= 5)).ToArray()),
            new(CorrectScore, "Correct score (home – away)", GoalsGroup, Enumerable.Range(0, 4)
                .SelectMany(home => Enumerable.Range(0, 4).Select(away => Score(home, away))).ToArray()),
        };

        markets.AddRange(Enumerable.Range(0, 7).Select(goals => Total("totals", "Total goals", goals, score => score.HomeGoals + score.AwayGoals)));
        markets.AddRange(Enumerable.Range(0, 5).Select(goals => Total("home-totals", "{home} goals", goals, score => score.HomeGoals)));
        markets.AddRange(Enumerable.Range(0, 5).Select(goals => Total("away-totals", "{away} goals", goals, score => score.AwayGoals)));
        return markets.ToArray();
    }

    private static ScoreMarket Total(string id, string name, int goals, Func<Scoreline, int> value) =>
        new($"{id}{goals}5", $"{name} {goals}.5", GoalsGroup,
        [
            new("over", $"Over {goals}.5", score => value(score) > goals),
            new("under", $"Under {goals}.5", score => value(score) <= goals),
        ]);

    private static ScoreSelection Score(int home, int away) =>
        new($"{home}-{away}", $"{home}–{away}", score => score.HomeGoals == home && score.AwayGoals == away);

    private static string WithTeams(string label, FootballMatch match) => label.Replace("{home}", match.Home).Replace("{away}", match.Away);

    private sealed record ScoreMarket(string Id, string Name, string Group, ScoreSelection[] Selections);
    private sealed record ScoreSelection(string Id, string Name, Func<Scoreline, bool> Wins);
}
