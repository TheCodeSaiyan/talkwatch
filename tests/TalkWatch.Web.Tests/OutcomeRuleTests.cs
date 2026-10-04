using System.Text.RegularExpressions;
using TalkWatch.Core.Calls;

namespace TalkWatch.Web.Tests;

/// <summary>
/// Every rule about missed, voicemail or unanswered calls asks the shared checks in <see cref="CallOutcomes"/>, so an
/// outcome added there counts in every call-back list, count, filter and alert at once.
/// </summary>
public sealed partial class OutcomeRuleTests
{
    private static string Root()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "TalkWatch.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void No_rule_names_missed_or_voicemail_itself()
    {
        var src = Path.Combine(Root(), "src");
        var offenders = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                // Where the outcomes and the shared checks are defined.
                && Path.GetFileName(f) != "CallOutcome.cs")
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(src, f), Line: i + 1, Text: line)))
            // A switch arm that labels an outcome ("Missed" => ...) is naming it, not testing for it.
            .Where(l => Named().IsMatch(l.Text) && !Arm().IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Use CallOutcomes.MissedKinds, VoicemailKinds or UnansweredKinds (or IsMissed, IsVoicemail, IsUnanswered):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_shared_checks_agree_with_each_other()
    {
        Assert.Equal([.. CallOutcomes.MissedKinds, .. CallOutcomes.VoicemailKinds], CallOutcomes.UnansweredKinds);
        Assert.All(Enum.GetValues<CallOutcome>(), o => Assert.Equal(o.IsMissed() || o.IsVoicemail(), o.IsUnanswered()));
        Assert.Empty(CallOutcomes.MissedKinds.Intersect(CallOutcomes.VoicemailKinds));
    }

    [GeneratedRegex(@"CallOutcome\.(Missed|Voicemail)\b")]
    private static partial Regex Named();

    [GeneratedRegex(@"^\s*CallOutcome\.(Missed|Voicemail)\s*=>")]
    private static partial Regex Arm();
}
