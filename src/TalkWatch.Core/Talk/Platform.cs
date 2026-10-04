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
    /// <summary>The versions TalkWatch was built and tested against: the ones the discovery spike captured.</summary>
    public static readonly ConsoleVersions Tested = new("5.1.33", "release-candidate", "5.3.2", "release-candidate");

    public static bool IsPreRelease(string? channel) =>
        channel is not null && !channel.Equals("release", StringComparison.OrdinalIgnoreCase) && !channel.Equals("stable", StringComparison.OrdinalIgnoreCase);
}

public enum VersionFit
{
    /// <summary>The exact versions TalkWatch was tested against.</summary>
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
    /// <summary>How <paramref name="running"/> compares with <paramref name="tested"/>; the worse of UniFi OS and Talk.</summary>
    public static VersionFit Assess(ConsoleVersions running, ConsoleVersions tested)
    {
        var os = Compare(running.UnifiOs, tested.UnifiOs);
        var talk = Compare(running.Talk, tested.Talk);
        return new[] { os, talk }.Max();
    }

    private static VersionFit Compare(string? running, string? tested)
    {
        if (!TryParse(running, out var r) || !TryParse(tested, out var t))
        {
            return VersionFit.Unknown;
        }

        var order = r.CompareTo(t);
        return order == 0 ? VersionFit.Tested : order > 0 ? VersionFit.NewerUntested : VersionFit.OlderUntested;
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
