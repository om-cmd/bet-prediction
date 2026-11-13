using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bet.Web;

internal sealed class OpenLigaFeed(HttpClient http, IConfiguration configuration)
{
    public const string SourceUrl = "https://www.openligadb.de/";
    private static readonly Dictionary<string, string> Shortcuts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UCL"] = "ucl", ["UEL"] = "uel", ["UECL"] = "uecl", ["EPL"] = "pl", ["LALIGA"] = "la1",
        ["BUNDESLIGA"] = "bl1", ["BUNDESLIGA-2"] = "bl2", ["3-LIGA"] = "bl3", ["DFB-POKAL"] = "dfb",
    };
    private readonly ConcurrentDictionary<(string Shortcut, int Season), TimedCache<ProviderMatch[]>> _seasons = new();
    private readonly TimeSpan _currentLifetime = TimeSpan.FromSeconds(Math.Max(60, configuration.GetValue("BET_FIXTURE_CACHE_SECONDS", 120)));

    public static bool Supports(Competition competition) => Shortcuts.ContainsKey(competition.Id);

    public async Task<FeedResult<FootballMatch[]>> GetAsync(Competition competition, CancellationToken cancellationToken)
    {
        if (!Shortcuts.TryGetValue(competition.Id, out var shortcut))
            return new(null, null, null);

        var start = DateTimeOffset.UtcNow;
        var season = start.Month >= competition.StartMonth ? start.Year : start.Year - 1;
        var currentShortcut = shortcut is "uel" or "uecl" ? $"{shortcut}{season}" : shortcut;
        var current = await FetchAsync(currentShortcut, season, current: true, cancellationToken);
        if (current.Value is not { Length: > 0 } currentRows)
            return new(null, current.FetchedAt, current.Warning ?? "OpenLigaDB has no current fixtures for this competition.");

        var history = await Task.WhenAll(PreviousSeasons(shortcut, season).Select(key =>
            FetchAsync(key.Shortcut, key.Season, current: false, cancellationToken)));
        var now = DateTimeOffset.UtcNow;
        var matches = currentRows.Concat(history.SelectMany(feed => feed.Value ?? []))
            .Select(item => Normalize(item, now)).OfType<FootballMatch>().DistinctBy(match => match.Id).ToArray();
        var warning = current.Warning ?? history.Select(feed => feed.Warning).OfType<string>().FirstOrDefault();
        return matches.Length > 0 ? new(matches, current.FetchedAt, warning) :
            new(null, current.FetchedAt, "OpenLigaDB returned no usable matches for this competition.");
    }

    private Task<FeedResult<ProviderMatch[]>> FetchAsync(string shortcut, int season, bool current, CancellationToken cancellationToken) =>
        _seasons.GetOrAdd((shortcut, season), static _ => new()).GetAsync(current ? _currentLifetime : TimeSpan.FromDays(1),
            token => LoadAsync(shortcut, season, token), cancellationToken, maxStale: current ? TimeSpan.FromHours(6) : TimeSpan.FromDays(7));

    private static IEnumerable<(string Shortcut, int Season)> PreviousSeasons(string shortcut, int season)
    {
        if (shortcut is "bl1" or "bl2" or "bl3" or "dfb")
            return [(shortcut, season - 1), (shortcut, season - 2)];

        // Community-maintained cups have season-specific shortcuts.
        if (shortcut == "ucl" && season == 2026)
            return [("ucl", 2025), ("ucl2024", 2024)];
        if (shortcut == "uel" && season == 2026)
            return [("uel", 2025), ("uel", 2024)];

        return [];
    }

    private async Task<ProviderMatch[]> LoadAsync(string shortcut, int season, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.openligadb.de/getmatchdata/{Uri.EscapeDataString(shortcut)}/{season}";
            using var response = await http.GetAsync(url, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return [];
            if (!response.IsSuccessStatusCode)
                throw new FeedException($"OpenLigaDB returned HTTP {(int)response.StatusCode} for season {season}.");

            return await response.Content.ReadFromJsonAsync<ProviderMatch[]>(cancellationToken) ?? [];
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException ||
                                          exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new FeedException($"OpenLigaDB scores could not be loaded for season {season}.");
        }
    }

    private static FootballMatch? Normalize(ProviderMatch match, DateTimeOffset now)
    {
        if (match.MatchId <= 0 || match.MatchDateTimeUtc is not { } kickoff || kickoff.Year < 1900 ||
            match.Team1 is not { TeamId: > 0, TeamName.Length: > 0 } home ||
            match.Team2 is not { TeamId: > 0, TeamName.Length: > 0 } away || home.TeamId == away.TeamId)
            return null;

        var results = match.MatchResults ?? [];
        var regulation = results.FirstOrDefault(result => result.ResultTypeKind == "After90Minutes" && result.HasScore);
        var afterExtraTime = results.FirstOrDefault(result => result.ResultTypeKind == "AfterExtraTime" && result.HasScore);
        var halftime = results.FirstOrDefault(result => result.ResultTypeKind == "HalfTime" && result.HasScore);
        var latestGoal = match.Goals?.Where(goal => goal.ScoreTeam1 >= 0 && goal.ScoreTeam2 >= 0)
            .OrderByDescending(goal => goal.ScoreTeam1 + goal.ScoreTeam2).ThenByDescending(goal => goal.GoalId).FirstOrDefault();

        var status = match.MatchIsFinished && kickoff <= now ? "finished" : kickoff > now ? "upcoming" :
            now - kickoff < TimeSpan.FromHours(3) && (latestGoal is not null || halftime is not null || regulation is not null) ? "live" : "pending";
        var display = status == "finished" ? afterExtraTime ?? regulation : status == "live" ? regulation ?? halftime : null;
        var liveGoal = status == "live" && regulation is null ? latestGoal : null;
        return new($"openliga-{match.MatchId}", kickoff, home.TeamName, away.TeamName,
            home.TeamId.ToString(CultureInfo.InvariantCulture), away.TeamId.ToString(CultureInfo.InvariantCulture), status,
            liveGoal?.ScoreTeam1 ?? display?.PointsTeam1, liveGoal?.ScoreTeam2 ?? display?.PointsTeam2,
            status == "finished" ? regulation?.PointsTeam1 : null, status == "finished" ? regulation?.PointsTeam2 : null);
    }

    private sealed record ProviderMatch(long MatchId, DateTimeOffset? MatchDateTimeUtc, bool MatchIsFinished,
        Team? Team1, Team? Team2, MatchResult[]? MatchResults, Goal[]? Goals);
    private sealed record Team(long TeamId, string TeamName);
    private sealed record MatchResult(string? ResultTypeKind, int? PointsTeam1, int? PointsTeam2)
    {
        public bool HasScore => PointsTeam1 is >= 0 and <= 30 && PointsTeam2 is >= 0 and <= 30;
    }
    private sealed record Goal(int GoalId, int ScoreTeam1, int ScoreTeam2);
}
