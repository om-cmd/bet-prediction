namespace Bet.Web;

internal sealed record FeedResult<T>(T? Value, DateTimeOffset? FetchedAt, string? Warning) where T : class;

internal sealed class TimedCache<T> where T : class
{
    private readonly SemaphoreSlim _gate = new(1);
    private FeedResult<T> _result = new(null, null, null);
    private DateTimeOffset _nextFetch;

    public async Task<FeedResult<T>> GetAsync(TimeSpan lifetime, Func<CancellationToken, Task<T>> fetch,
        CancellationToken cancellationToken, TimeSpan? maxStale = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow < _nextFetch)
                return _result;

            try
            {
                var value = await fetch(cancellationToken);
                var now = DateTimeOffset.UtcNow;
                _result = new(value, now, null);
                _nextFetch = now + lifetime;
            }
            catch (FeedException exception)
            {
                _result = maxStale is { } grace && _result.Value is not null && _result.FetchedAt >= DateTimeOffset.UtcNow - grace
                    ? _result with { Warning = exception.Message + " Showing the last successful response." }
                    : new(null, null, exception.Message);
                _nextFetch = DateTimeOffset.UtcNow.AddSeconds(60);
            }

            return _result;
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed class FeedException(string message) : Exception(message);
