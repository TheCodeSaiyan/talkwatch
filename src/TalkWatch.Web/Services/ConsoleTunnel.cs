using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

public enum TunnelState
{
    /// <summary>No tunnel: the console is reached directly.</summary>
    Off,

    /// <summary>Started, and waiting for the far end to answer.</summary>
    Starting,

    /// <summary>The far end has answered recently.</summary>
    Up,

    /// <summary>Not running, or not set up so it can: <see cref="TunnelStatus.Detail"/> says why.</summary>
    Down,
}

/// <summary>Where the tunnel is, for the Console page and the status on every page.</summary>
public sealed record TunnelStatus(ConsoleRoute Route, TunnelState State, string? Detail, DateTimeOffset Since, DateTimeOffset? LastHandshake = null);

/// <summary>
/// Runs the way to a console that isn't on TalkWatch's own network: wireproxy, a WireGuard client, to the site gateway's
/// VPN server; or Tailscale's own tailscaled, joined to a tailnet with a subnet router on the site's network. Both run
/// in userspace, as the app's own unprivileged user, and offer a SOCKS5 proxy on loopback that the console connection
/// goes through (<see cref="ConsoleHandler"/>). The console's address stays its LAN address, so its certificate pin and
/// host name are exactly as on the site.
/// </summary>
public sealed partial class ConsoleTunnel(ConsoleConnection connection, TimeProvider clock, ILogger<ConsoleTunnel> logger) : BackgroundService
{
    /// <summary>Where the helpers are in the image; tests point it elsewhere.</summary>
    public static string HelperDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "tunnel");

    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LookUpEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(5);

    // WireGuard renews its session every two minutes while traffic flows, and TalkWatch polls every minute; a handshake
    // older than this means the gateway has stopped answering.
    private static readonly TimeSpan HandshakeFresh = TimeSpan.FromMinutes(3);

    private readonly Lock _lock = new();
    private CancellationTokenSource? _cycle;
    private TunnelStatus _status = new(ConsoleRoute.Direct, TunnelState.Off, null, DateTimeOffset.MinValue);

    /// <summary>The loopback port the SOCKS5 proxy listens on, chosen once so a restarted tunnel keeps it.</summary>
    public int SocksPort { get; } = FreePort();

    public TunnelStatus Status
    {
        get { lock (_lock) { return _status; } }
    }

    public event Action? StatusChanged;

    /// <summary>The proxy the console connection goes through for this target, or null to go direct.</summary>
    public Uri? ProxyFor(ConsoleTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Route == ConsoleRoute.Direct ? null : new Uri($"socks5://127.0.0.1:{SocksPort.ToString(CultureInfo.InvariantCulture)}");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        connection.Changed += Restart;
        try
        {
            var retry = FirstRetry;
            while (!stoppingToken.IsCancellationRequested)
            {
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (_lock)
                {
                    _cycle = cycle;
                }

                try
                {
                    var target = await connection.GetAsync(cycle.Token);
                    await RunAsync(target, cycle.Token);
                    retry = FirstRetry;
                    continue;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException) when (cycle.IsCancellationRequested)
                {
                    // The settings changed: start again with the new ones, straight away.
                    retry = FirstRetry;
                    continue;
                }
#pragma warning disable CA1031 // The tunnel must outlive any failure: a down tunnel is reported, and tried again.
                catch (Exception e)
#pragma warning restore CA1031
                {
                    SetStatus(Status.Route, TunnelState.Down, e.Message);
                    LogDown(logger, retry.TotalSeconds, e.Message);
                }

                await Task.Delay(retry, clock, cycle.Token).ContinueWith(_ => { }, TaskScheduler.Default);
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, LongestRetry.Ticks));
            }
        }
        finally
        {
            connection.Changed -= Restart;
        }
    }

    private void Restart()
    {
        lock (_lock)
        {
            try
            {
                _cycle?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Between cycles: the next one reads the new settings anyway.
            }
        }
    }

    private async Task RunAsync(ConsoleTarget target, CancellationToken cancellationToken)
    {
        if (target.Route == ConsoleRoute.Direct)
        {
            SetStatus(ConsoleRoute.Direct, TunnelState.Off, null);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return;
        }

        if (target.Problem is { } problem)
        {
            // Nothing to try again until the settings change.
            SetStatus(target.Route, TunnelState.Down, problem);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return;
        }

        var helpers = target.Route == ConsoleRoute.WireGuard ? new[] { "wireproxy" } : ["tailscaled", "tailscale"];
        if (helpers.FirstOrDefault(h => !File.Exists(HelperPath(h))) is { } missing)
        {
            SetStatus(target.Route, TunnelState.Down, $"This build of TalkWatch doesn't have {missing}: the published image does.");
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return;
        }

        // A folder only this user can read: the helpers' settings hold the private key or auth key.
        var folder = Directory.CreateTempSubdirectory("talkwatch-tunnel-");
        try
        {
            if (target.Route == ConsoleRoute.WireGuard)
            {
                await RunWireGuardAsync(target.WireGuard!, folder.FullName, cancellationToken);
            }
            else
            {
                await RunTailscaleAsync(target, folder.FullName, cancellationToken);
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private async Task RunWireGuardAsync(WireGuardConfig config, string folder, CancellationToken cancellationToken)
    {
        SetStatus(ConsoleRoute.WireGuard, TunnelState.Starting, $"Looking up {config.EndpointHost}");
        var address = await LookUpAsync(config, cancellationToken);
        if (IsCloudflareProxy(address))
        {
            LogBehindCloudflare(logger, config.EndpointHost, address);
        }
        var conf = Path.Combine(folder, "wireproxy.conf");
        await WritePrivateAsync(conf, config.ToWireproxy(address, SocksPort), cancellationToken);

        var infoPort = FreePort();
        using var wireproxy = new Helper("wireproxy", ["-s", "-c", conf, "-i", $"127.0.0.1:{infoPort.ToString(CultureInfo.InvariantCulture)}"]);
        SetStatus(ConsoleRoute.WireGuard, TunnelState.Starting, $"Waiting for the gateway at {config.Endpoint} to answer");
        LogStarted(logger, "WireGuard", config.Endpoint, address);

        using var metrics = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{infoPort.ToString(CultureInfo.InvariantCulture)}"), Timeout = TimeSpan.FromSeconds(5) };
        var lookedUp = clock.GetUtcNow();
        while (true)
        {
            await wireproxy.WaitAsync(CheckEvery, clock, cancellationToken);
            if (wireproxy.Exited)
            {
                throw new InvalidOperationException("wireproxy stopped: " + wireproxy.LastError());
            }

            var handshake = await LastHandshakeAsync(metrics, cancellationToken);
            if (handshake is { } at && clock.GetUtcNow() - at < HandshakeFresh)
            {
                SetStatus(ConsoleRoute.WireGuard, TunnelState.Up, $"Through {config.Endpoint}", at);
            }
            else if (IsCloudflareProxy(address))
            {
                // A dynamic DNS name kept in Cloudflare with its proxy on: the name answers with Cloudflare's address,
                // and Cloudflare's proxy carries web traffic, not WireGuard. It happened on the first real gateway tried.
                SetStatus(ConsoleRoute.WireGuard, TunnelState.Starting,
                    $"{config.EndpointHost} points at Cloudflare's proxy ({address}), which doesn't carry WireGuard. In Cloudflare, set the record to DNS only (the grey cloud); TalkWatch looks the name up again every few minutes.",
                    handshake);
            }
            else
            {
                SetStatus(ConsoleRoute.WireGuard, TunnelState.Starting,
                    $"The gateway at {config.Endpoint} ({address}) hasn't answered{(handshake is { } last ? " since " + last.ToString("u", CultureInfo.InvariantCulture) : " yet")}. Is UDP {config.EndpointPort.ToString(CultureInfo.InvariantCulture)} open to it?",
                    handshake);
            }

            // A dynamic DNS name moves when the site's address changes; wireproxy would keep sending to the old one.
            if (config.EndpointIsName && clock.GetUtcNow() - lookedUp >= LookUpEvery)
            {
                lookedUp = clock.GetUtcNow();
                var now = await LookUpAsync(config, cancellationToken);
                if (!now.Equals(address))
                {
                    LogMoved(logger, config.EndpointHost, address, now);
                    return;
                }
            }
        }
    }

    private async Task RunTailscaleAsync(ConsoleTarget target, string folder, CancellationToken cancellationToken)
    {
        SetStatus(ConsoleRoute.Tailscale, TunnelState.Starting, "Starting Tailscale");
        var socket = Path.Combine(folder, "tailscaled.sock");
        // State in memory registers an ephemeral node: TalkWatch joins afresh each time, and leaves nothing behind in the
        // tailnet. Tailscale's own log upload is off: nothing but the tunnel leaves.
        using var tailscaled = new Helper("tailscaled",
            ["--tun=userspace-networking", $"--socks5-server=127.0.0.1:{SocksPort.ToString(CultureInfo.InvariantCulture)}", "--state=mem:", $"--socket={socket}", $"--statedir={folder}", "--no-logs-no-support"]);

        for (var waited = 0; !File.Exists(socket); waited++)
        {
            if (tailscaled.Exited || waited >= 30)
            {
                throw new InvalidOperationException("tailscaled didn't start: " + tailscaled.LastError());
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), clock, cancellationToken);
        }

        var key = Path.Combine(folder, "auth-key");
        await WritePrivateAsync(key, TailscaleAuthKey(target.TailscaleAuthKey!), cancellationToken);
        var up = await Helper.RunAsync("tailscale", [.. TailscaleUp(socket, key, target.TailscaleTags)], TimeSpan.FromSeconds(90), cancellationToken);
        File.Delete(key);
        if (up.ExitCode != 0)
        {
            throw new InvalidOperationException("Tailscale didn't join the tailnet: " + up.Output);
        }

        LogStarted(logger, "Tailscale", "the tailnet", null);
        while (true)
        {
            var state = await TailscaleStateAsync(socket, cancellationToken);
            SetStatus(ConsoleRoute.Tailscale, state == "Running" ? TunnelState.Up : TunnelState.Starting,
                state == "Running" ? "Joined the tailnet" : $"Tailscale is {state ?? "not answering"}");
            await tailscaled.WaitAsync(CheckEvery, clock, cancellationToken);
            if (tailscaled.Exited)
            {
                throw new InvalidOperationException("tailscaled stopped: " + tailscaled.LastError());
            }
        }
    }

    /// <summary>
    /// The arguments for joining the tailnet. The key is read from a file, never given on a command line; routes from the
    /// site's subnet router are taken, which Linux leaves off by default.
    /// </summary>
    public static IReadOnlyList<string> TailscaleUp(string socket, string keyFile, string? tags)
    {
        List<string> args = [$"--socket={socket}", "up", $"--auth-key=file:{keyFile}", "--hostname=talkwatch", "--accept-routes", "--timeout=60s"];
        if (!string.IsNullOrWhiteSpace(tags))
        {
            args.Add($"--advertise-tags={string.Join(',', tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))}");
        }

        return args;
    }

    /// <summary>
    /// An OAuth client secret registers the node itself, so it is told to make it ephemeral and approved; an auth key
    /// already carries those choices, made when it was created.
    /// </summary>
    public static string TailscaleAuthKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        key = key.Trim();
        return key.StartsWith("tskey-client-", StringComparison.Ordinal) && !key.Contains('?', StringComparison.Ordinal)
            ? key + "?ephemeral=true&preauthorized=true"
            : key;
    }

    // Cloudflare's proxy addresses, as it publishes them at cloudflare.com/ips-v4 and /ips-v6.
    private static readonly IPNetwork[] Cloudflare =
    [
        .. new[]
        {
            "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18", "108.162.192.0/18",
            "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
            "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
            "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32", "2a06:98c0::/29", "2c0f:f248::/32",
        }.Select(r => IPNetwork.Parse(r)),
    ];

    /// <summary>
    /// Whether an address is Cloudflare's proxy, which a gateway's dynamic DNS name answers with when its record is
    /// proxied (the orange cloud): web traffic gets through, WireGuard's UDP never reaches the gateway.
    /// </summary>
    public static bool IsCloudflareProxy(IPAddress address) => Cloudflare.Any(n => n.Contains(address));

    /// <summary>The latest handshake in wireproxy's metrics, which are WireGuard's own: last_handshake_time_sec=…</summary>
    public static DateTimeOffset? LastHandshake(string metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        long latest = 0;
        foreach (var line in metrics.Split('\n'))
        {
            if (line.StartsWith("last_handshake_time_sec=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan("last_handshake_time_sec=".Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                latest = Math.Max(latest, seconds);
            }
        }

        return latest > 0 ? DateTimeOffset.FromUnixTimeSeconds(latest) : null;
    }

    private static async Task<DateTimeOffset?> LastHandshakeAsync(HttpClient metrics, CancellationToken cancellationToken)
    {
        try
        {
            return LastHandshake(await metrics.GetStringAsync(new Uri("/metrics", UriKind.Relative), cancellationToken));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<string?> TailscaleStateAsync(string socket, CancellationToken cancellationToken)
    {
        var status = await Helper.RunAsync("tailscale", [$"--socket={socket}", "status", "--json", "--peers=false"], TimeSpan.FromSeconds(15), cancellationToken);
        if (status.ExitCode != 0)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(status.Output);
            return json.RootElement.TryGetProperty("BackendState", out var state) ? state.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The gateway's address now: the endpoint itself, or what its name points to, IPv4 first.</summary>
    private static async Task<IPAddress> LookUpAsync(WireGuardConfig config, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(config.EndpointHost, out var address))
        {
            return address;
        }

        try
        {
            var found = await Dns.GetHostAddressesAsync(config.EndpointHost, cancellationToken);
            return found.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault()
                ?? throw new InvalidOperationException($"{config.EndpointHost} has no address.");
        }
        catch (SocketException e)
        {
            throw new InvalidOperationException($"Couldn't look up {config.EndpointHost}: {e.Message}", e);
        }
    }

    private static async Task WritePrivateAsync(string path, string contents, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, contents, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private void SetStatus(ConsoleRoute route, TunnelState state, string? detail, DateTimeOffset? handshake = null)
    {
        lock (_lock)
        {
            if (_status.Route == route && _status.State == state && _status.Detail == detail && _status.LastHandshake == handshake)
            {
                return;
            }

            _status = new TunnelStatus(route, state, detail, _status.Route == route && _status.State == state ? _status.Since : clock.GetUtcNow(), handshake);
        }

        StatusChanged?.Invoke();
    }

    private static string HelperPath(string name) => Path.Combine(HelperDirectory, name);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A helper process, its last few lines of complaint kept for the status, and killed when done with.</summary>
    private sealed class Helper : IDisposable
    {
        private readonly Process _process;
        private readonly Queue<string> _errors = new();

        public Helper(string name, IReadOnlyList<string> args)
        {
            var start = new ProcessStartInfo(HelperPath(name)) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            _process = new Process { StartInfo = start, EnableRaisingEvents = true };
            _process.ErrorDataReceived += (_, e) => Keep(e.Data);
            _process.OutputDataReceived += (_, e) => Keep(e.Data);
            _process.Start();
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();
        }

        public bool Exited => _process.HasExited;

        /// <summary>Waits until the process ends or <paramref name="wait"/> passes, whichever is first.</summary>
        public async Task WaitAsync(TimeSpan wait, TimeProvider clock, CancellationToken cancellationToken)
        {
            using var timeout = new CancellationTokenSource(wait, clock);
            using var either = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            try
            {
                await _process.WaitForExitAsync(either.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        public string LastError()
        {
            lock (_errors)
            {
                return _errors.Count == 0 ? $"exit code {(_process.HasExited ? _process.ExitCode : 0)}" : string.Join(" ", _errors);
            }
        }

        private void Keep(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (_errors)
            {
                _errors.Enqueue(line.Trim());
                while (_errors.Count > 5)
                {
                    _errors.Dequeue();
                }
            }
        }

        /// <summary>Runs a helper to the end and returns what it printed.</summary>
        public static async Task<(int ExitCode, string Output)> RunAsync(string name, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var start = new ProcessStartInfo(HelperPath(name)) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start)!;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var errors = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return (-1, $"no answer within {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds");
            }

            return (process.ExitCode, ((await output) + " " + (await errors)).Trim());
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tunnel to the console: {Kind} to {Endpoint} {Address}.")]
    private static partial void LogStarted(ILogger logger, string kind, string endpoint, IPAddress? address);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} moved from {Old} to {New}; restarting the tunnel.")]
    private static partial void LogMoved(ILogger logger, string name, IPAddress old, IPAddress @new);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Name} points at Cloudflare's proxy ({Address}), which doesn't carry WireGuard: set its record to DNS only.")]
    private static partial void LogBehindCloudflare(ILogger logger, string name, IPAddress address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tunnel to the console down; trying again in {Seconds:0} s: {Why}")]
    private static partial void LogDown(ILogger logger, double seconds, string why);
}
