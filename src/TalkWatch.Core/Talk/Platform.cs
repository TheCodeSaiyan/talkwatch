using System.Text.Json.Serialization;

namespace TalkWatch.Core.Talk;

/// <summary><c>GET /proxy/talk/api/info</c>: Talk's own version and the update channel it follows.</summary>
public sealed record TalkInfo
{
    public string? Version { get; init; }

    /// <summary>'release' for general releases; 'release-candidate' or 'early-access' for pre-release builds.</summary>
    public string? UpdateChannel { get; init; }

    public string? HostDeviceModel { get; init; }
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
