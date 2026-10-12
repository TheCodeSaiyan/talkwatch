using System.Text.Json;
using System.Text.Json.Serialization;

namespace TalkWatch.Core.Talk;

/// <summary><c>GET /proxy/talk/api/info</c>: Talk's own version and the update channel it follows.</summary>
public sealed record TalkInfo
{
    public string? Version { get; init; }

    /// <summary>'release' for general releases; 'release-candidate' or 'early-access' for pre-release builds.</summary>
    public string? UpdateChannel { get; init; }

    public string? HostDeviceModel { get; init; }

    /// <summary>Since Talk 5.4.5: whether Talk can reach its cloud services. Older versions send nothing.</summary>
    public TalkCloudConnectivity? CloudConnectivity { get; init; }

    /// <summary>Since Talk 5.4.5: whether Talk is running degraded. Changes also arrive live, as DEGRADATION_UPDATED.</summary>
    public TalkDegradation? Degradation { get; init; }
}

/// <summary>
/// Whether Talk can reach its cloud services, as Talk 5.4.5 reports it. Only the healthy state has been seen, where reason
/// and since are null, so their types are not known: they are kept as sent, and a shape Talk has not shown yet can't stop
/// the version check.
/// </summary>
public sealed record TalkCloudConnectivity
{
    public bool? Degraded { get; init; }
    public JsonElement? Reason { get; init; }
    public JsonElement? Since { get; init; }
}

/// <summary>Whether Talk is running degraded, as Talk 5.4.5 reports it. As with the cloud, only the healthy state has been seen.</summary>
public sealed record TalkDegradation
{
    public bool? Degraded { get; init; }
    public JsonElement? DegradationLevel { get; init; }
    public JsonElement? LoadLevel { get; init; }
    public bool? TargetedForSuspension { get; init; }
    public JsonElement? SuspendedAt { get; init; }
    public JsonElement? Since { get; init; }
}

/// <summary>Reading the loosely typed values Talk sends in its service state.</summary>
public static class TalkValue
{
    /// <summary>A string or number as words ("no_route" reads "no route"), or null for anything else.</summary>
    public static string? Words(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } s when !string.IsNullOrWhiteSpace(s.GetString()) => s.GetString()!.Replace('_', ' '),
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null,
    };

    /// <summary>A time sent as an ISO string, or as Unix seconds or milliseconds; null for anything else.</summary>
    public static DateTimeOffset? Time(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } s when DateTimeOffset.TryParse(s.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) => t,
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt64(out var unix) && unix > 0 =>
            unix > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix),
        _ => null,
    };
}

/// <summary><c>GET /proxy/talk/api/ucore/system_info</c>: the console's UniFi OS version and release channel.</summary>
public sealed record ConsoleSystemInfo
{
    public HardwareInfo? Hardware { get; init; }

    [JsonPropertyName("ucore_version")]
    public string? UcoreVersion { get; init; }

    [JsonPropertyName("releaseChannel")]
    public string? ReleaseChannel { get; init; }

    public sealed record HardwareInfo
    {
        /// <summary>The UniFi OS version, such as 5.1.33.</summary>
        [JsonPropertyName("firmwareVersion")]
        public string? FirmwareVersion { get; init; }

        public string? Shortname { get; init; }
    }
}

/// <summary>What the console is running, and on which channels.</summary>
public sealed record ConsoleVersions(string? UnifiOs, string? UnifiOsChannel, string? Talk, string? TalkChannel)
{
    /// <summary>The oldest versions TalkWatch was tested against: the ones the discovery spike captured, which the fixtures replay.</summary>
    public static readonly ConsoleVersions TestedFrom = new("5.1.33", "release-candidate", "5.3.2", "release-candidate");

    /// <summary>The newest versions TalkWatch was tested against, from a capture after the console's last update.</summary>
    public static readonly ConsoleVersions TestedTo = new("5.1.33", "release-candidate", "5.4.5", "release-candidate");

    /// <summary>The tested Talk versions as words, such as "5.3.2 to 5.4.5".</summary>
    public static string TestedTalk => Range(TestedFrom.Talk, TestedTo.Talk);

    /// <summary>The tested UniFi OS versions as words.</summary>
    public static string TestedUnifiOs => Range(TestedFrom.UnifiOs, TestedTo.UnifiOs);

    private static string Range(string? from, string? to) => from == to ? to ?? "" : $"{from} to {to}";

    public static bool IsPreRelease(string? channel) =>
        channel is not null && !channel.Equals("release", StringComparison.OrdinalIgnoreCase) && !channel.Equals("stable", StringComparison.OrdinalIgnoreCase);
}

public enum VersionFit
{
    /// <summary>Within the versions TalkWatch was tested against, oldest to newest.</summary>
    Tested,

    /// <summary>Newer than tested. Most updates do not touch what TalkWatch reads; the drift check catches those that do.</summary>
    NewerUntested,

    /// <summary>Older than tested: endpoints may predate what TalkWatch expects.</summary>
    OlderUntested,

    /// <summary>The console did not say.</summary>
    Unknown,
}

public static class Compatibility
{
    /// <summary>How <paramref name="running"/> compares with the tested range; the worse of UniFi OS and Talk.</summary>
    public static VersionFit Assess(ConsoleVersions running, ConsoleVersions testedFrom, ConsoleVersions testedTo)
    {
        var os = Compare(running.UnifiOs, testedFrom.UnifiOs, testedTo.UnifiOs);
        var talk = Compare(running.Talk, testedFrom.Talk, testedTo.Talk);
        return new[] { os, talk }.Max();
    }

    private static VersionFit Compare(string? running, string? from, string? to)
    {
        if (!TryParse(running, out var r) || !TryParse(from, out var f) || !TryParse(to, out var t))
        {
            return VersionFit.Unknown;
        }

        return r.CompareTo(t) > 0 ? VersionFit.NewerUntested : r.CompareTo(f) < 0 ? VersionFit.OlderUntested : VersionFit.Tested;
    }

    // Versions such as 5.3.2 or 5.1.33; anything after a '-' or '+' (build tags) is ignored.
    private static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var core = value.Split('-', '+')[0].Trim();
        return Version.TryParse(core.Contains('.', StringComparison.Ordinal) ? core : core + ".0", out version!);
    }
}
