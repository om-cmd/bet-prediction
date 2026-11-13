namespace Bet.Web;

internal sealed record Competition(string Id, string Name, int FootballId, string? OddsKey, int StartMonth = 7,
    int FirstSeason = 0, int SeasonInterval = 1, string Group = "Domestic leagues", string? Country = null,
    int[]? ProviderSeasons = null)
{
    public int[] RecentSeasons(DateTimeOffset now)
    {
        if (ProviderSeasons is { Length: > 0 })
            return ProviderSeasons.Take(3).ToArray();

        var year = now.Month >= StartMonth ? now.Year : now.Year - 1;
        var current = FirstSeason + (year - FirstSeason) / SeasonInterval * SeasonInterval;
        return [current, current - SeasonInterval, current - 2 * SeasonInterval];
    }
}

internal static class Competitions
{
    public const string DemoId = "DEMO";

    public static readonly Competition[] All =
    [
        new("UCL", "Champions League", 2, "soccer_uefa_champs_league", Group: "Continental clubs"),
        new("UEL", "Europa League", 3, "soccer_uefa_europa_league", Group: "Continental clubs"),
        new("UECL", "Conference League", 848, "soccer_uefa_europa_conference_league", Group: "Continental clubs"),
        new("UWCL", "Women's Champions League", 525, "soccer_uefa_champs_league_women", Group: "Continental clubs"),
        new("EPL", "Premier League", 39, "soccer_epl", Country: "England"),
        new("CHAMPIONSHIP", "EFL Championship", 40, "soccer_efl_champ", Country: "England"),
        new("LALIGA", "La Liga", 140, "soccer_spain_la_liga", Country: "Spain"),
        new("BUNDESLIGA", "Bundesliga", 78, "soccer_germany_bundesliga", Country: "Germany"),
        new("BUNDESLIGA-2", "2. Bundesliga", 79, null, Country: "Germany"),
        new("3-LIGA", "3. Liga", 80, null, Country: "Germany"),
        new("DFB-POKAL", "DFB-Pokal", 81, null, Group: "Domestic cups", Country: "Germany"),
        new("SERIEA", "Serie A", 135, "soccer_italy_serie_a", Country: "Italy"),
        new("LIGUE1", "Ligue 1", 61, "soccer_france_ligue_one", Country: "France"),
        new("EREDIVISIE", "Eredivisie", 88, "soccer_netherlands_eredivisie", Country: "Netherlands"),
        new("LIGA-PORTUGAL", "Liga Portugal", 94, "soccer_portugal_primeira_liga", Country: "Portugal"),
        new("A-LEAGUE", "A-League Men", 188, "soccer_australia_aleague", Country: "Australia"),
        new("MLS", "Major League Soccer", 253, "soccer_usa_mls", StartMonth: 1, Country: "USA"),
        new("BRASILEIRAO", "Brasileirão Série A", 71, "soccer_brazil_campeonato", StartMonth: 1, Country: "Brazil"),
        new("WORLD-CUP", "FIFA World Cup", 1, "soccer_fifa_world_cup", 1, 2026, 4, "National teams"),
        new("WOMENS-WORLD-CUP", "FIFA Women's World Cup", 8, "soccer_fifa_world_cup_womens", 1, 2023, 4, "National teams"),
        new("CLUB-WORLD-CUP", "FIFA Club World Cup", 15, "soccer_fifa_club_world_cup", 1, 2025, 4, "International competitions"),
        new("EURO", "UEFA European Championship", 4, "soccer_uefa_european_championship", 1, 2024, 4, "National teams"),
        new(DemoId, "Demo League", 0, null, Group: "Demo"),
    ];

    public static Competition? Find(IEnumerable<Competition> catalog, string id) =>
        catalog.FirstOrDefault(competition => string.Equals(competition.Id, id, StringComparison.OrdinalIgnoreCase));
}
