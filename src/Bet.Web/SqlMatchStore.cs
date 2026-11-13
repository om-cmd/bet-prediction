using System.Data;
using Microsoft.Data.SqlClient;

namespace Bet.Web;

internal sealed record StoredFixtures(FootballMatch[] Matches, DateTimeOffset? FetchedAt);

internal sealed class SqlMatchStore(IConfiguration configuration, ILogger<SqlMatchStore> logger)
{
    private readonly string? _connectionString = configuration["BET_SQLSERVER_CONNECTION_STRING"]?.Trim() is { Length: > 0 } configured
        ? configured
        : configuration.GetConnectionString("BetSqlServer")?.Trim();

    public bool IsConfigured => !string.IsNullOrEmpty(_connectionString);

    public async Task<StoredFixtures> LoadMatchesAsync(string competition, DateTimeOffset from, DateTimeOffset through,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return new([], null);

        try
        {
            var settings = new SqlConnectionStringBuilder(_connectionString) { ApplicationIntent = ApplicationIntent.ReadOnly };
            await using var connection = new SqlConnection(settings.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            using var command = new SqlCommand("""
                SELECT FixtureId, Kickoff, HomeName, AwayName, HomeTeamId, AwayTeamId, Status,
                    HomeScore, AwayScore, RegulationHomeScore, RegulationAwayScore, ObservedAt
                FROM dbo.BetFixtureFeed
                WHERE Competition = @competition AND Kickoff BETWEEN @from AND @through
                ORDER BY Kickoff, FixtureId;
                """, connection) { CommandTimeout = 5 };
            command.Parameters.Add(new SqlParameter("@competition", SqlDbType.VarChar, 32) { Value = competition });
            command.Parameters.Add(new SqlParameter("@from", SqlDbType.DateTimeOffset) { Value = from, Scale = 7 });
            command.Parameters.Add(new SqlParameter("@through", SqlDbType.DateTimeOffset) { Value = through, Scale = 7 });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var matches = new List<FootballMatch>();
            DateTimeOffset? fetchedAt = null;
            while (await reader.ReadAsync(cancellationToken))
            {
                matches.Add(new(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    ReadScore(reader, 7), ReadScore(reader, 8), ReadScore(reader, 9), ReadScore(reader, 10)));
                var observed = reader.GetFieldValue<DateTimeOffset>(11);
                if (fetchedAt is null || observed > fetchedAt)
                    fetchedAt = observed;
            }

            return new(matches.DistinctBy(match => match.Id).ToArray(), fetchedAt);
        }
        catch (SqlException exception)
        {
            logger.LogWarning("Read-only fixture lookup failed with SQL error {Number}.", exception.Number);
            throw new FeedException("The read-only SQL Server fixture feed was unavailable. Check access to dbo.BetFixtureFeed.");
        }
        catch (ArgumentException)
        {
            throw new FeedException("The SQL Server connection string could not be parsed.");
        }
        catch (Exception exception) when (exception is InvalidCastException or System.Data.SqlTypes.SqlNullValueException)
        {
            throw new FeedException("The SQL Server fixture feed does not match the documented columns and types.");
        }
    }

    private static int? ReadScore(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
