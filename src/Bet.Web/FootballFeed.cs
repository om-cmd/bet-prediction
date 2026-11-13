using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bet.Web;

internal sealed record FootballMatch(string Id, DateTimeOffset Kickoff, string Home, string Away, string HomeTeamId,
    string AwayTeamId, string Status, int? HomeScore, int? AwayScore, int? RegulationHomeScore, int? RegulationAwayScore);

internal sealed class FootballFeed(HttpClient http, IConfiguration configuration)
{
    public const string SourceUrl = "https://www.api-football.com/documentation-v3";

    private readonly string? _key = configuration["BET_API_FOOTBALL_KEY"]?.Trim();
    private readonly TimeSpan _fixtureLifetime = TimeSpan.FromSeconds(Math.Max(60, configuration.GetValue("BET_FIXTURE_CACHE_SECONDS", 120)));
    private readonly ConcurrentDictionary<(int League, int Season), TimedCache<FootballMatch[]>> _seasons = new();
    private readonly TimedCache<Competition[]> _catalog = new();

    public bool IsConfigured => !string.IsNullOrEmpty(_key);

    public async Task<FeedResult<Competition[]>> GetCompetitionsAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return new(Competitions.All, null, null);

        var result = await _catalog.GetAsync(TimeSpan.FromHours(24), LoadCompetitionsAsync, cancellationToken,
            maxStale: TimeSpan.FromDays(7));
        return result with { Value = result.Value ?? Competitions.All };
    }

    public Task<FeedResult<FootballMatch[]>> GetSeasonAsync(Competition league, int season, bool current,
        CancellationToken cancellationToken) =>
        _seasons.GetOrAdd((league.FootballId, season), static _ => new()).GetAsync(
            current ? _fixtureLifetime : TimeSpan.FromHours(24), token => LoadSeasonAsync(league, season, token),
            cancellationToken, maxStale: current ? TimeSpan.FromHours(6) : TimeSpan.FromDays(7));

    private async Task<FootballMatch[]> LoadSeasonAsync(Competition league, int season, CancellationToken cancellationToken)
    {
        var data = await FetchAsync<ProviderMatch>($"fixtures?league={league.FootballId}&season={season}&timezone=UTC",
            $"fixtures for {league.Name} season {season}", cancellationToken);
        return data.Select(Normalize).OfType<FootballMatch>().ToArray();
    }

    private async Task<Competition[]> LoadCompetitionsAsync(CancellationToken cancellationToken)
    {
        var data = await FetchAsync<ProviderCompetition>("leagues", "the competition catalog", cancellationToken);
        if (data.Length == 0)
            throw new FeedException("API-Football returned an empty competition catalog.");

        var configured = Competitions.All.ToDictionary(competition => competition.FootballId);
        var additional = new List<Competition>();
        foreach (var item in data)
        {
            if (item.League is not { Id: > 0, Name.Length: > 0 } league || item.Seasons is not { Length: > 0 } seasons)
                continue;

            var years = seasons.OrderByDescending(season => season.Current).ThenByDescending(season => season.Year)
                .Select(season => season.Year).Where(year => year >= 1900).Distinct().ToArray();
            if (years.Length == 0)
                continue;

            if (configured.TryGetValue(league.Id, out var existing))
            {
                configured[league.Id] = existing with { ProviderSeasons = years };
                continue;
            }

            var country = item.Country?.Name;
            var group = country is null or "World" ? "International competitions" :
                league.Type == "Cup" ? "Domestic cups" : "Domestic leagues";
            additional.Add(new($"AF-{league.Id}", league.Name, league.Id, null, Group: group, Country: country, ProviderSeasons: years));
        }

        return Competitions.All.Select(item => configured[item.FootballId]).Concat(additional.DistinctBy(item => item.Id)
            .OrderBy(item => item.Country, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private async Task<T[]> FetchAsync<T>(string path, string description, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://v3.football.api-sports.io/{path}");
            request.Headers.Add("x-apisports-key", _key);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new FeedException($"API-Football returned HTTP {(int)response.StatusCode} for {description}.");

            var data = await response.Content.ReadFromJsonAsync<ProviderResponse<T>>(cancellationToken);
            if (data?.Response is null || HasErrors(data.Errors))
                throw new FeedException($"API-Football could not supply {description}. Check the key, season access and quota.");

            return data.Response;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException ||
                                          exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new FeedException($"API-Football did not return usable data for {description}.");
        }
    }

    private static bool HasErrors(JsonElement errors) => errors.ValueKind switch
    {
        JsonValueKind.Array => errors.GetArrayLength() > 0,
        JsonValueKind.Object => errors.EnumerateObject().Any(),
        JsonValueKind.Undefined or JsonValueKind.Null => false,
        _ => true,
    };

    private static FootballMatch? Normalize(ProviderMatch match)
    {
        if (match.Fixture is not { Id: > 0 } fixture || fixture.Date == default ||
            match.Teams?.Home is not { Id: > 0, Name.Length: > 0 } home ||
            match.Teams.Away is not { Id: > 0, Name.Length: > 0 } away)
            return null;

        var providerStatus = fixture.Status?.Short;
        var status = providerStatus switch
        {
            "NS" => "upcoming",
            "TBD" => "unconfirmed",
            "1H" or "HT" or "2H" or "ET" or "BT" or "P" or "LIVE" => "live",
            "FT" or "AET" or "PEN" => "finished",
            "PST" or "SUSP" or "INT" => "postponed",
            _ => "closed",
        };

        // Feed goals may include extra time. The score.fulltime field is the regulation score.
        var regulation = status == "finished" ? match.Score?.Fulltime : null;
        if (providerStatus == "FT" && (regulation?.Home is null || regulation.Away is null))
            regulation = match.Goals;

        return new(fixture.Id.ToString(), fixture.Date, home.Name, away.Name, home.Id.ToString(), away.Id.ToString(),
            status, match.Goals?.Home, match.Goals?.Away, regulation?.Home, regulation?.Away);
    }

    private sealed record ProviderResponse<T>(JsonElement Errors, T[]? Response);
    private sealed record ProviderCompetition(ProviderLeague? League, ProviderCountry? Country, ProviderSeason[]? Seasons);
    private sealed record ProviderLeague(int Id, string Name, string? Type);
    private sealed record ProviderCountry(string? Name);
    private sealed record ProviderSeason(int Year, bool Current);
    private sealed record ProviderMatch(Fixture? Fixture, Teams? Teams, ScorePair? Goals, Scores? Score);
    private sealed record Fixture(long Id, DateTimeOffset Date, Status? Status);
    private sealed record Status(string? Short);
    private sealed record Teams(Team? Home, Team? Away);
    private sealed record Team(long Id, string Name);
    private sealed record Scores(ScorePair? Fulltime);
    private sealed record ScorePair(int? Home, int? Away);
}
