using Bet.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(12) });
builder.Services.AddSingleton<FootballFeed>();
builder.Services.AddSingleton<OpenLigaFeed>();
builder.Services.AddSingleton<OddsFeed>();
builder.Services.AddSingleton<ExtraOddsFeed>();
builder.Services.AddSingleton<ForecastRunner>();
builder.Services.AddSingleton<SqlMatchStore>();
builder.Services.AddSingleton<DashboardService>();

var app = builder.Build();
app.UseStaticFiles();
app.MapRazorPages();
var api = app.MapGroup("/api");
api.AddEndpointFilter(async (context, next) =>
{
    context.HttpContext.Response.Headers.CacheControl = "no-store";
    return await next(context);
});
api.MapGet("/dashboard", async (string? competition, FootballFeed football, DashboardService dashboard,
    CancellationToken cancellationToken) =>
{
    var catalog = await football.GetCompetitionsAsync(cancellationToken);
    var selected = Competitions.Find(catalog.Value!, competition ?? "UCL");
    if (selected is null)
        return Results.BadRequest(new { error = "Choose a supported competition." });

    return Results.Ok(await dashboard.GetAsync(selected, catalog, cancellationToken));
});
api.MapGet("/model-performance", async (string? competition, FootballFeed football, DashboardService dashboard,
    CancellationToken cancellationToken) =>
{
    var catalog = await football.GetCompetitionsAsync(cancellationToken);
    var selected = Competitions.Find(catalog.Value!, competition ?? "UCL");
    return selected is null ? Results.BadRequest(new { error = "Choose a supported competition." }) :
        Results.Ok(await dashboard.GetPerformanceAsync(selected, catalog, cancellationToken));
});
api.MapGet("/match-markets", async (string? competition, string? matchId, FootballFeed football, DashboardService dashboard,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(matchId) || matchId.Length > 160)
        return Results.BadRequest(new { error = "Choose an upcoming fixture." });

    var catalog = await football.GetCompetitionsAsync(cancellationToken);
    var selected = Competitions.Find(catalog.Value!, competition ?? "UCL");
    if (selected is null)
        return Results.BadRequest(new { error = "Choose a supported competition." });

    var markets = await dashboard.GetMatchMarketsAsync(selected, matchId, cancellationToken);
    return markets is null ? Results.NotFound(new { error = "The fixture is unavailable for pre-match analysis." }) : Results.Ok(markets);
});
api.MapPost("/same-game", async (SameGameRequest request, FootballFeed football, DashboardService dashboard,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.MatchId) || request.MatchId.Length > 160 || request.Legs is not { Length: >= 2 and <= 12 } legs ||
        legs.Any(leg => !ValidSelectionId(leg?.MarketId) || !ValidSelectionId(leg?.OutcomeId)) ||
        legs.Select(leg => leg!.MarketId).Distinct().Count() != legs.Length)
        return Results.BadRequest(new { error = "Choose two to twelve outcomes from different markets in one fixture." });

    var catalog = await football.GetCompetitionsAsync(cancellationToken);
    var selected = Competitions.Find(catalog.Value!, request.Competition ?? "UCL");
    if (selected is null)
        return Results.BadRequest(new { error = "Choose a supported competition." });

    var estimate = await dashboard.GetSameGameEstimateAsync(selected, request.MatchId, legs, cancellationToken);
    return estimate is null ? Results.NotFound(new { error = "The fixture is unavailable for pre-match analysis." }) : Results.Ok(estimate);
});
app.Run();

static bool ValidSelectionId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 240;
