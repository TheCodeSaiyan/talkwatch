using TalkWatch.Core.Talk;

namespace TalkWatch.Web.Services;

/// <summary>
/// The one signed-in session with the console, shared by the poller and the live listener. UniFi OS rate-limits
/// sign-in, so everything that talks to the console goes through here: one sign-in, and one back-off when refused.
/// </summary>
public sealed partial class TalkSession(
    IHttpClientFactory clients, IHttpMessageHandlerFactory handlers, ConsoleConnection connection, IngestionStatus status,
    TimeProvider clock, ILogger<TalkSession> logger) : IDisposable
{
    public const string HttpClientName = "talk";

    /// <summary>How long to wait after a 429 that did not say, and after the console refuses the credentials.</summary>
    public static readonly TimeSpan RateLimitBackOff = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RefusedSignInBackOff = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _signIn = new(1, 1);
    // Made once, whoever asks first: the poller and the live listener start together, and two clients sharing the one
    // cookie jar, each thinking itself signed in or not, signed in over each other's session until the console refused.
    private readonly Lazy<TalkClient> _client = new(() => new TalkClient(clients.CreateClient(HttpClientName)), LazyThreadSafetyMode.ExecutionAndPublication);

    private Action? _onChanged;

    public TalkClient Client
    {
        get
        {
            if (_onChanged is null && Interlocked.CompareExchange(ref _onChanged, SettingsChanged, null) is null)
            {
                connection.Changed += SettingsChanged;
            }

            return _client.Value;
        }
    }

    // New settings from the Console page: a new console, account or way to it needs a new sign-in, tried straight away
    // rather than after a back-off the old settings earned.
    private void SettingsChanged()
    {
        _client.Value.Forget();
        status.BackOffUntil = null;
    }

    /// <summary>The console as the settings now name it; null while none is set.</summary>
    public async ValueTask<Uri?> ConsoleUrlAsync(CancellationToken cancellationToken) => (await connection.GetAsync(cancellationToken)).Url;

    public bool BackingOff => status.BackOffUntil is { } until && clock.GetUtcNow() < until;

    /// <summary>
    /// For the WebSocket: the same handler chain as <see cref="Client"/>, so the same cookie and certificate trust. The
    /// handler is kept for the life of the app (see Program.cs), so this is the handler that holds the session.
    /// </summary>
    public HttpMessageInvoker CreateInvoker() => new(handlers.CreateHandler(HttpClientName), disposeHandler: false);

    /// <summary>Signed in, signing in if needed. False while backing off or when the console refuses.</summary>
    public async Task<bool> EnsureSignedInAsync(CancellationToken cancellationToken)
    {
        if (BackingOff)
        {
            return false;
        }

        if (Client.IsSignedIn)
        {
            return true;
        }

        await _signIn.WaitAsync(cancellationToken);
        try
        {
            if (Client.IsSignedIn)
            {
                return true;
            }

            var target = await connection.GetAsync(cancellationToken);
            await Client.SignInAsync(target.Username, target.Password, cancellationToken);
            return true;
        }
        catch (TalkRateLimitedException e)
        {
            BackOff(e.RetryAfter ?? RateLimitBackOff, e);
            return false;
        }
        catch (TalkApiException e)
        {
            // A refused sign-in is not retried every minute: repeated failures are how an account gets locked.
            BackOff(RefusedSignInBackOff, e);
            return false;
        }
        finally
        {
            _signIn.Release();
        }
    }

    public void BackOff(TimeSpan wait, Exception e)
    {
        status.BackOffUntil = clock.GetUtcNow() + wait;
        status.LastError = e.Message;
        LogBackOff(logger, wait.TotalMinutes, e);
    }

    public void Dispose()
    {
        if (_onChanged is not null)
        {
            connection.Changed -= SettingsChanged;
        }

        _signIn.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The console refused; not trying again for {Minutes:0.#} minute(s).")]
    private static partial void LogBackOff(ILogger logger, double minutes, Exception exception);
}
