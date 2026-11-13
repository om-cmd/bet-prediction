using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bet.Web;

internal sealed record HistoryRow(string Id, string Competition, DateTimeOffset Kickoff, string HomeTeamId,
    string AwayTeamId, int HomeGoals, int AwayGoals, DateTimeOffset? CompletedAt);
internal sealed record ForecastFixture(string Id, string Competition, DateTimeOffset Kickoff, string HomeTeamId, string AwayTeamId);
internal sealed record ForecastRequest(HistoryRow[] History, ForecastFixture[] Fixtures, DateTimeOffset AsOf);
internal sealed record Scoreline(int HomeGoals, int AwayGoals, double Probability);
internal sealed record Prediction(string Id, double HomeWin, double Draw, double AwayWin, double Over25, double Under25,
    double BothTeamsScore, double BothTeamsScoreNo, double ExpectedHomeGoals, double ExpectedAwayGoals, Scoreline[] TopScorelines,
    Scoreline[]? ScoreDistribution = null, double ScoreDistributionOmittedMass = 0);
internal sealed record Tuning(string Status, int HistoryMatches, int ValidationMatches, int TestMatches, double PoissonWeight,
    double? HybridLogLoss, double? PoissonLogLoss, double? EloLogLoss, double? BaselineLogLoss, double? TotalGoalsLogLoss,
    double? Over25LogLoss, double? BothTeamsScoreLogLoss, double? PredictedTotalGoals, double? ObservedTotalGoals, double? GoalRateScale,
    int? EloHalfLifeDays = null, int? EloUpdate = null, int? TrainingMatches = null, int? CorrectPicks = null,
    int? BaselineCorrectPicks = null, DateTimeOffset? FirstTestKickoff = null, DateTimeOffset? LastTestKickoff = null);
internal sealed record ForecastResponse(Prediction[] Predictions, Tuning Tuning);

internal sealed class ForecastRunner(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _python = configuration["BET_PYTHON"]?.Trim() is { Length: > 0 } path ? path : "python";
    private readonly string _script = Path.Combine(AppContext.BaseDirectory, "forecasting", "model.py");
    private readonly ConcurrentDictionary<string, ModelCache> _models = new();

    public Task<FeedResult<ForecastResponse>> GetAsync(string competition, ForecastRequest request, CancellationToken cancellationToken)
    {
        // New completed results or changed fixtures invalidate the cached prediction. Price changes do not.
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        var fingerprint = Convert.ToHexString(SHA256.HashData(payload));
        var cache = _models.AddOrUpdate(competition, _ => new(fingerprint, new()),
            (_, existing) => existing.Fingerprint == fingerprint ? existing : new(fingerprint, new()));

        return cache.Result.GetAsync(TimeSpan.FromDays(1), token => RunAsync(payload, token), cancellationToken);
    }

    private async Task<ForecastResponse> RunAsync(byte[] payload, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new System.Text.UTF8Encoding(false),
            },
        };
        process.StartInfo.ArgumentList.Add(_script);
        process.StartInfo.Environment["PYTHONUTF8"] = "1";

        try
        {
            if (!process.Start())
                throw new FeedException("Python did not start. Set BET_PYTHON to your Python interpreter.");

            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(token);
                var stderr = process.StandardError.ReadToEndAsync(token);
                await process.StandardInput.BaseStream.WriteAsync(payload, token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(token);
                await stderr;
                var output = await stdout;
                if (process.ExitCode != 0)
                    throw new FeedException("The Python model exited with an error. Check Python 3.11+ and the model script.");

                return JsonSerializer.Deserialize<ForecastResponse>(output, JsonOptions) ??
                    throw new FeedException("The Python model returned no forecast.");
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
        }
        catch (Win32Exception)
        {
            throw new FeedException("Python could not be started. Set BET_PYTHON to a Python 3.11+ executable.");
        }
        catch (Exception exception) when (exception is JsonException or IOException ||
                                          exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new FeedException("The Python forecast could not finish or returned an invalid response.");
        }
    }

    private sealed record ModelCache(string Fingerprint, TimedCache<ForecastResponse> Result);
}
