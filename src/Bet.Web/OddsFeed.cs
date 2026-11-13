using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bet.Web;

internal sealed record Quote(string Bookmaker, decimal Price, DateTimeOffset UpdatedAt);

internal sealed class OddsFeed(HttpClient http, IConfiguration configuration)
{
    public const string SourceUrl = "https://the-odds-api.com/liveapi/guides/v4/";

    private readonly string? _key = configuration["BET_ODDS_API_KEY"]?.Trim();
    private readonly TimeSpan _lifetime = TimeSpan.FromSeconds(Math.Max(60, configuration.GetValue("BET_ODDS_CACHE_SECONDS", 300)));
    private readonly ConcurrentDictionary<string, TimedCache<OddsEvent[]>> _competitions = new();

    public bool IsConfigured => !string.IsNullOrEmpty(_key);
    public string Region { get; } = GetRegion(configuration["BET_ODDS_REGION"]);

    public Task<FeedResult<OddsEvent[]>> GetAsync(string sport, CancellationToken cancellationToken) =>
        _competitions.GetOrAdd(sport, static _ => new()).GetAsync(_lifetime,
            token => LoadAsync(sport, token), cancellationToken);

    private static string GetRegion(string? value)
    {
        var region = value?.Trim().ToLowerInvariant();
        return region is "uk" or "eu" or "us" or "au" ? region : "au";
    }

    private async Task<OddsEvent[]> LoadAsync(string sport, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.the-odds-api.com/v4/sports/{sport}/odds?regions={Region}&markets=h2h,totals&oddsFormat=decimal&apiKey={Uri.EscapeDataString(_key!)}";
            using var response = await http.GetAsync(url, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return [];
            if (!response.IsSuccessStatusCode)
                throw new FeedException($"The Odds API returned HTTP {(int)response.StatusCode}. Check the key, plan and quota.");

            return await response.Content.ReadFromJsonAsync<OddsEvent[]>(cancellationToken) ??
                throw new FeedException("The Odds API returned an empty response.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException ||
                                          exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new FeedException("The Odds API did not return usable prices.");
        }
    }

    public static OddsEvent? MatchEvent(FootballMatch match, OddsEvent[] events)
    {
        var home = TeamKey(match.Home);
        var away = TeamKey(match.Away);
        var candidates = events.Where(item =>
            (item.Kickoff - match.Kickoff).Duration() <= TimeSpan.FromMinutes(30) &&
            TeamKey(item.Home) == home && TeamKey(item.Away) == away).Take(2).ToArray();

        return candidates.Length == 1 ? candidates[0] : null;
    }

    public static Dictionary<(string Market, string Outcome), Quote[]> GetQuotes(OddsEvent? item, DateTimeOffset now)
    {
        var quotes = new List<(string Market, string Outcome, Quote Quote)>();
        if (item?.Bookmakers is null || item.Kickoff <= now)
            return [];

        foreach (var book in item.Bookmakers)
        {
            if (string.IsNullOrWhiteSpace(book.Title) || book.Key is null ||
                book.Key.Contains("_ex", StringComparison.Ordinal) || book.Key is "matchbook" or "smarkets")
                continue;

            foreach (var market in book.Markets ?? [])
            {
                var updatedAt = market.LastUpdate ?? book.LastUpdate;
                if (updatedAt is null || updatedAt < now.AddMinutes(-15) || updatedAt > now.AddMinutes(2))
                    continue;

                foreach (var outcome in market.Outcomes ?? [])
                {
                    if (outcome.Price <= 1)
                        continue;

                    var selection = GetSelection(item, market.Key, outcome);
                    if (selection is { } match)
                        quotes.Add((match.Market, match.Outcome, new(book.Title, outcome.Price, updatedAt.Value)));
                }
            }
        }

        return quotes.GroupBy(entry => (entry.Market, entry.Outcome)).ToDictionary(group => group.Key,
            group => group.Select(entry => entry.Quote).OrderByDescending(quote => quote.Price)
                .DistinctBy(quote => quote.Bookmaker).ToArray());
    }

    private static (string Market, string Outcome)? GetSelection(OddsEvent item, string? market, OddsOutcome outcome)
    {
        if (market == "h2h")
        {
            var name = TeamKey(outcome.Name);
            if (name == "draw") return ("h2h", "draw");
            if (name.Length > 0 && name == TeamKey(item.Home)) return ("h2h", "home");
            if (name.Length > 0 && name == TeamKey(item.Away)) return ("h2h", "away");
        }

        if (market == "totals" && outcome.Point == 2.5m)
        {
            if (outcome.Name == "Over") return ("totals25", "over");
            if (outcome.Name == "Under") return ("totals25", "under");
        }

        return null;
    }

    private static string TeamKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var characters = name.ToLowerInvariant().Replace('ø', 'o').Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray();
        var key = string.Concat(new string(characters).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word is not ("fc" or "cf" or "sc" or "fk" or "sk")));

        return key switch
        {
            "afcbournemouth" => "bournemouth",
            "asroma" => "roma",
            "athleticbilbao" or "athleticclub" => "athletic",
            "atleticomadrid" or "atleticodemadrid" => "atletico",
            "bayer04leverkusen" => "bayerleverkusen",
            "bayernmunchen" or "bayernmunich" or "fcbayernmunchen" => "bayern",
            "benfica" or "slbenfica" => "benfica",
            "borussiamonchengladbach" or "borussiamgladbach" => "monchengladbach",
            "brightonandhovealbion" or "brightonhovealbion" => "brighton",
            "clubbruggekv" => "clubbrugge",
            "como1907" => "como",
            "eintrachtfrankfurt" => "frankfurt",
            "fcinternazionalemilano" or "internazionalemilano" or "intermilan" => "inter",
            "galatasarayistanbul" => "galatasaray",
            "lasklinz" => "lask",
            "losclille" or "lilleosc" => "lille",
            "manunited" or "manutd" => "manchesterunited",
            "mancity" => "manchestercity",
            "newcastleunited" => "newcastle",
            "nottinghamforest" or "nottmforest" => "nottingham",
            "parissaintgermain" or "parissg" => "psg",
            "psveindhoven" => "psv",
            "rasenballleipzig" or "rbleipzig" => "leipzig",
            "rcstrasbourgalsace" or "rcstrasbourg" => "strasbourg",
            "realbetisbalompie" => "realbetis",
            "shakhtardonetsk" or "shakhtardoneck" => "shakhtar",
            "slaviaprague" => "slaviapraha",
            "sportinglisbon" or "sportingcp" => "sporting",
            "tottenhamhotspur" => "tottenham",
            "vfbstuttgart" => "stuttgart",
            "wolverhamptonwanderers" or "wolverhampton" => "wolves",
            _ => key,
        };
    }

    public sealed record OddsEvent([property: JsonPropertyName("commence_time")] DateTimeOffset Kickoff,
        [property: JsonPropertyName("home_team")] string? Home, [property: JsonPropertyName("away_team")] string? Away,
        Bookmaker[]? Bookmakers, string? Id = null);

    public sealed record Bookmaker(string? Key, string? Title, [property: JsonPropertyName("last_update")] DateTimeOffset? LastUpdate,
        OddsMarket[]? Markets);

    public sealed record OddsMarket(string? Key, [property: JsonPropertyName("last_update")] DateTimeOffset? LastUpdate,
        OddsOutcome[]? Outcomes);

    public sealed record OddsOutcome(string? Name, decimal Price, decimal? Point, string? Description = null);
}
