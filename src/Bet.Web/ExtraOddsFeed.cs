using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bet.Web;

internal sealed class ExtraOddsFeed(HttpClient http, IConfiguration configuration, OddsFeed odds)
{
    private static readonly Dictionary<string, MarketSpec> Specs = new(StringComparer.Ordinal)
    {
        ["alternate_totals"] = new("Goals", "Match goals", Shape.GoalTotal),
        ["team_totals"] = new("Goals", "Team goals", Shape.TeamGoalTotal),
        ["alternate_team_totals"] = new("Goals", "Team goals", Shape.TeamGoalTotal),
        ["btts"] = new("Goals", "Both teams to score", Shape.YesNo),
        ["correct_score"] = new("Goals", "Correct score", Shape.CorrectScore),
        ["double_chance"] = new("Result", "Double chance", Shape.DoubleChance),
        ["draw_no_bet"] = new("Result", "Draw no bet", Shape.Side),
        ["spreads"] = new("Result", "Goal handicap", Shape.Handicap),
        ["alternate_spreads"] = new("Result", "Goal handicap", Shape.Handicap),
        ["h2h_3_way_h1"] = new("Halves", "Half-time result", Shape.Side),
        ["h2h_3_way_h2"] = new("Halves", "Second-half result", Shape.Side),
        ["halftime_fulltime"] = new("Halves", "Half-time / full-time", Shape.Named),
        ["double_chance_h1"] = new("Halves", "First-half double chance", Shape.Named),
        ["totals_h1"] = new("Halves", "First-half goals", Shape.Total),
        ["totals_h2"] = new("Halves", "Second-half goals", Shape.Total),
        ["correct_score_h1"] = new("Halves", "First-half correct score", Shape.Named),
        ["btts_h1"] = new("Halves", "Both teams to score in the first half", Shape.YesNo),
        ["btts_h2"] = new("Halves", "Both teams to score in the second half", Shape.YesNo),
        ["alternate_totals_corners"] = new("Corners", "Total corners", Shape.Total),
        ["alternate_team_totals_corners"] = new("Corners", "Team corners", Shape.TeamTotal),
        ["alternate_spreads_corners"] = new("Corners", "Corner handicap", Shape.Handicap),
        ["corners_1x2"] = new("Corners", "Most corners", Shape.Side),
        ["alternate_totals_cards"] = new("Cards", "Total cards / bookings", Shape.Total),
        ["alternate_spreads_cards"] = new("Cards", "Card / booking handicap", Shape.Handicap),
        ["player_goal_scorer_anytime"] = new("Player props", "Anytime goalscorer", Shape.PlayerYesNo),
        ["player_first_goal_scorer"] = new("Player props", "First goalscorer", Shape.PlayerYesNo),
        ["player_last_goal_scorer"] = new("Player props", "Last goalscorer", Shape.PlayerYesNo),
        ["player_shots"] = new("Player props", "Player shots", Shape.PlayerTotal),
        ["player_shots_alternate"] = new("Player props", "Alternate player shots", Shape.PlayerTotal),
        ["player_shots_on_target"] = new("Player props", "Player shots on target", Shape.PlayerTotal),
        ["player_shots_on_target_alternate"] = new("Player props", "Alternate player shots on target", Shape.PlayerTotal),
        ["player_assists"] = new("Player props", "Player assists", Shape.PlayerTotal),
        ["player_assists_alternate"] = new("Player props", "Alternate player assists", Shape.PlayerTotal),
        ["player_to_receive_card"] = new("Player props", "Player to receive a card", Shape.PlayerYesNo),
        ["player_to_receive_red_card"] = new("Player props", "Player to receive a red card", Shape.PlayerYesNo),
        ["to_qualify"] = new("Result", "To qualify", Shape.Side),
    };

    private static readonly string RequestedMarkets = string.Join(',', Specs.Keys);
    private readonly string? _key = configuration["BET_ODDS_API_KEY"]?.Trim();
    private readonly TimeSpan _lifetime = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("BET_ODDS_CACHE_SECONDS", 300), 60, 600));
    private readonly ConcurrentDictionary<string, TimedCache<OddsFeed.OddsEvent[]>> _eventLists = new();
    private readonly ConcurrentDictionary<(string Sport, string Id, DateTimeOffset Kickoff), TimedCache<OddsFeed.OddsEvent[]>> _events = new();

    public async Task<FeedResult<Market[]>> GetAsync(string? sport, FootballMatch match, CancellationToken cancellationToken)
    {
        if (match.Status != "upcoming" || match.Kickoff <= DateTimeOffset.UtcNow)
            return new([], null, "Extra markets are available before kickoff.");
        if (!odds.IsConfigured)
            return new([], null, "Connect The Odds API to see available corners, cards and player prices.");
        if (string.IsNullOrEmpty(sport))
            return new([], null, "This competition has no mapped bookmaker feed.");

        var sportPath = Uri.EscapeDataString(sport);
        var events = await _eventLists.GetOrAdd(sport, static _ => new()).GetAsync(_lifetime,
            async token => await ReadAsync<OddsFeed.OddsEvent[]>($"sports/{sportPath}/events", "", token) ?? [],
            cancellationToken);
        if (OddsFeed.MatchEvent(match, events.Value ?? []) is not { Id.Length: > 0 } matched)
            return new([], null, events.Warning ?? "No matching event is currently listed in The Odds API.");

        var now = DateTimeOffset.UtcNow;
        foreach (var cached in _events.Keys)
        {
            if (cached.Kickoff <= now)
                _events.TryRemove(cached, out _);
        }

        var result = await _events.GetOrAdd((sport, matched.Id, matched.Kickoff), static _ => new()).GetAsync(_lifetime,
            async token =>
            {
                var response = await ReadAsync<OddsFeed.OddsEvent>(
                    $"sports/{sportPath}/events/{Uri.EscapeDataString(matched.Id)}/odds",
                    $"&regions={odds.Region}&markets={RequestedMarkets}&oddsFormat=decimal", token);
                return response is null ? [] : [response];
            }, cancellationToken);

        var quotedEvent = OddsFeed.MatchEvent(match, result.Value ?? []);
        var markets = quotedEvent is not null && quotedEvent.Id == matched.Id
            ? ExtractMarkets(match, quotedEvent, DateTimeOffset.UtcNow)
            : [];
        return new(markets, result.FetchedAt, result.Warning ?? (markets.Length == 0
            ? $"No recent extra market prices are available in the {odds.Region.ToUpperInvariant()} feed for this match."
            : null));
    }

    private async Task<T?> ReadAsync<T>(string path, string query, CancellationToken cancellationToken) where T : class
    {
        try
        {
            var url = $"https://api.the-odds-api.com/v4/{path}?apiKey={Uri.EscapeDataString(_key!)}{query}";
            using var response = await http.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new FeedException($"The Odds API returned HTTP {(int)response.StatusCode} for extra markets. Check the key, plan and quota.");

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken) ??
                throw new FeedException("The Odds API returned an empty response for extra markets.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException ||
                                          exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new FeedException("The Odds API did not return usable extra market prices.");
        }
    }

    internal static Market[] ExtractMarkets(FootballMatch match, OddsFeed.OddsEvent quotedEvent, DateTimeOffset now)
    {
        if (match.Kickoff <= now || quotedEvent.Kickoff <= now)
            return [];

        var selections = new List<PricedSelection>();
        foreach (var book in quotedEvent.Bookmakers ?? [])
        {
            if (string.IsNullOrWhiteSpace(book.Title) || string.IsNullOrEmpty(book.Key) ||
                book.Key.Contains("_ex", StringComparison.Ordinal) || book.Key is "matchbook" or "smarkets")
                continue;

            foreach (var market in book.Markets ?? [])
            {
                var updatedAt = market.LastUpdate ?? book.LastUpdate;
                if (market.Key is null || !Specs.TryGetValue(market.Key, out var spec) ||
                    updatedAt is null || updatedAt < now.AddMinutes(-15) || updatedAt > now.AddMinutes(2))
                    continue;

                foreach (var outcome in market.Outcomes ?? [])
                {
                    if (outcome.Price > 1 && MapSelection(match, quotedEvent, market.Key, spec, outcome) is { } selection)
                        selections.Add(new(selection, book.Key, new Quote(book.Title, outcome.Price, updatedAt.Value)));
                }
            }
        }

        return selections.GroupBy(entry => entry.Selection.MarketId).Select(BuildMarket).ToArray();
    }

    private static Market BuildMarket(IGrouping<string, PricedSelection> market)
    {
        var first = market.First().Selection;
        var sides = market.GroupBy(entry => entry.Selection.OutcomeId).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.GroupBy(entry => entry.BookKey).Select(book => book.OrderByDescending(entry => entry.Quote.UpdatedAt).First())
                .OrderByDescending(entry => entry.Quote.Price).ToArray()).ToArray();
        var fair = first.Complementary && sides.Length == 2 ? MedianProbability(sides[0], sides[1]) : null;
        var outcomes = sides.Select((side, index) => new Outcome(side[0].Selection.OutcomeId, side[0].Selection.OutcomeName, null,
            side[0].Quote.Price, side[0].Quote.Bookmaker, side.Select(entry => entry.Quote).ToArray(),
            fair is { } probability ? (index == 0 ? probability : 1 - probability) : null)).ToArray();
        return new(market.Key, first.MarketName, outcomes, first.Group);
    }

    private static double? MedianProbability(PricedSelection[] first, PricedSelection[] second)
    {
        var estimates = first.Join(second, a => a.BookKey, b => b.BookKey, (a, b) =>
        {
            var inverseA = 1.0 / (double)a.Quote.Price;
            var inverseB = 1.0 / (double)b.Quote.Price;
            return inverseA / (inverseA + inverseB);
        }).Order().ToArray();

        return estimates.Length == 0 ? null : (estimates[(estimates.Length - 1) / 2] + estimates[estimates.Length / 2]) / 2;
    }

    private static Selection? MapSelection(FootballMatch match, OddsFeed.OddsEvent quotedEvent, string key, MarketSpec spec,
        OddsFeed.OddsOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome.Name))
            return null;

        var name = outcome.Name.Trim();
        var subject = outcome.Description?.Trim();
        var outcomeKey = name.ToLowerInvariant();
        var id = $"odds:{key}";
        var title = spec.Title;
        var complementary = false;

        switch (spec.Shape)
        {
            case Shape.GoalTotal:
            case Shape.TeamGoalTotal:
            case Shape.TeamTotal:
            case Shape.PlayerTotal:
            case Shape.Total:
                if (outcome.Point is not { } point || point < 0 || outcomeKey is not ("over" or "under"))
                    return null;

                var line = FormatLine(point);
                var side = TeamSide(subject, quotedEvent);
                if (spec.Shape is Shape.TeamGoalTotal or Shape.TeamTotal)
                {
                    if (side is not ("home" or "away"))
                        return null;
                    title = $"{TeamName(match, side)} {title.ToLowerInvariant()}";
                    id += $":{side}";
                }
                else if (spec.Shape == Shape.PlayerTotal)
                {
                    if (string.IsNullOrEmpty(subject))
                        return null;
                    title = $"{subject} · {title}";
                    id += $":{StableId(subject)}";
                }

                var halfPoint = point % 1 == 0.5m;
                if (halfPoint && point < 10 && (spec.Shape is Shape.GoalTotal or Shape.TeamGoalTotal))
                {
                    var modeledId = $"totals{(int)(point * 10):00}";
                    id = spec.Shape == Shape.GoalTotal ? modeledId : $"{side}-{modeledId}";
                }
                else
                {
                    if (spec.Shape == Shape.TeamGoalTotal)
                        id = $"odds:team_totals:{side}";
                    id += $":{line}";
                }

                title += $" · {line}";
                name = $"{name} {line}";
                complementary = halfPoint;
                break;

            case Shape.YesNo:
            case Shape.PlayerYesNo:
                if (outcomeKey is not ("yes" or "no"))
                    return null;
                if (spec.Shape == Shape.PlayerYesNo)
                {
                    if (string.IsNullOrEmpty(subject))
                        return null;
                    title = $"{subject} · {title}";
                    id += $":{StableId(subject)}";
                }
                if (key == "btts")
                    id = "btts";
                complementary = true;
                break;

            case Shape.Side:
                if (TeamSide(name, quotedEvent) is not { } selectionSide)
                    return null;
                outcomeKey = selectionSide;
                name = TeamName(match, selectionSide);
                break;

            case Shape.Handicap:
                var handicapSide = TeamSide(name, quotedEvent);
                if (outcome.Point is not { } spread || handicapSide is not ("home" or "away"))
                    return null;
                var homeSpread = handicapSide == "home" ? spread : -spread;
                id = key is "spreads" or "alternate_spreads" ? "odds:spreads" : id;
                id += $":{FormatLine(homeSpread)}";
                title += $" · {match.Home} {SignedLine(homeSpread)}";
                name = $"{TeamName(match, handicapSide)} {SignedLine(spread)}";
                outcomeKey = handicapSide;
                break;

            case Shape.DoubleChance:
                var pair = outcomeKey.Split(" or ", StringSplitOptions.TrimEntries);
                if (pair.Length != 2 || TeamSide(pair[0], quotedEvent) is not { } first ||
                    TeamSide(pair[1], quotedEvent) is not { } second || first == second)
                    return null;
                outcomeKey = (first, second) switch
                {
                    ("home", "draw") or ("draw", "home") => "home-draw",
                    ("away", "draw") or ("draw", "away") => "draw-away",
                    _ => "home-away",
                };
                id = "double-chance";
                break;

            case Shape.CorrectScore:
                var scores = name.Split(['-', ':', '–'], StringSplitOptions.TrimEntries);
                if (scores.Length != 2 || !int.TryParse(scores[0], NumberStyles.None, CultureInfo.InvariantCulture, out var home) ||
                    !int.TryParse(scores[1], NumberStyles.None, CultureInfo.InvariantCulture, out var away) ||
                    home > 99 || away > 99)
                    return null;
                id = "correct-score";
                outcomeKey = name = $"{home}-{away}";
                break;

            case Shape.Named:
                outcomeKey = StableId(name);
                break;
        }

        return new(id, title, spec.Group, outcomeKey, name, complementary);
    }

    private static string? TeamSide(string? name, OddsFeed.OddsEvent quotedEvent)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (string.Equals(name, "draw", StringComparison.OrdinalIgnoreCase))
            return "draw";
        if (string.Equals(name, quotedEvent.Home, StringComparison.OrdinalIgnoreCase))
            return "home";
        return string.Equals(name, quotedEvent.Away, StringComparison.OrdinalIgnoreCase) ? "away" : null;
    }

    private static string TeamName(FootballMatch match, string side) => side switch
    {
        "home" => match.Home,
        "away" => match.Away,
        _ => "Draw",
    };

    private static string StableId(string name) => Uri.EscapeDataString(name.Normalize().ToLowerInvariant());
    private static string FormatLine(decimal point) => point.ToString("G29", CultureInfo.InvariantCulture);
    private static string SignedLine(decimal point) => point > 0 ? $"+{FormatLine(point)}" : FormatLine(point);

    private sealed record MarketSpec(string Group, string Title, Shape Shape);
    private sealed record Selection(string MarketId, string MarketName, string Group, string OutcomeId, string OutcomeName, bool Complementary);
    private sealed record PricedSelection(Selection Selection, string BookKey, Quote Quote);
    private enum Shape { GoalTotal, TeamGoalTotal, TeamTotal, PlayerTotal, Total, YesNo, PlayerYesNo, Side, Handicap, DoubleChance, CorrectScore, Named }
}
