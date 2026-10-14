namespace TalkWatch.Core.Talk;

public enum HealthIssue
{
    /// <summary>Talk says the greeting's audio is missing (5.4.5 and later).</summary>
    MissingAudio,

    /// <summary>Talk is still making the greeting's audio, so callers hear nothing or the old one.</summary>
    GeneratingAudio,

    /// <summary>Nothing for a call to go to: a menu or time node with nothing under it, or a group, user or contact node that names nobody.</summary>
    EmptyTarget,

    /// <summary>Dialling by extension is limited to an allow-list with nobody on it.</summary>
    EmptyAllowList,

    /// <summary>A split by opening hours with no out-of-hours branch, or one that leads nowhere.</summary>
    ClosedBranchWithNoAction,

    /// <summary>A caller who presses nothing is sent to a node that is no longer in the tree.</summary>
    NoKeyPressTargetMissing,
}

/// <summary>Something wrong with a node's setup.</summary>
/// <param name="Disabled">The node, or one above it, is switched off, so no caller meets this yet: shown to admins as what a draft would raise, not to operators.</param>
public sealed record HealthFlag(string NodeId, HealthIssue Issue, bool Disabled);

/// <summary>Health flags worked out from a switchboard tree's setup, with nothing fetched beyond it.</summary>
public static class NodeHealth
{
    public static IReadOnlyList<HealthFlag> Of(IReadOnlyList<SwitchboardNode> nodes)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        var children = nodes.Where(n => n.ParentId is not null).ToLookup(n => n.ParentId!);
        var flags = new List<HealthFlag>();
        foreach (var node in nodes)
        {
            var setup = node.Setup;
            var disabled = IsDisabled(node, byId);
            void Flag(HealthIssue issue) => flags.Add(new HealthFlag(node.Id, issue, disabled));

            if (setup.Greeting is { } greeting)
            {
                if (greeting.Missing == true)
                {
                    Flag(HealthIssue.MissingAudio);
                }

                if (greeting.Generating == true)
                {
                    Flag(HealthIssue.GeneratingAudio);
                }
            }

            var under = children[node.Id].ToList();
            var empty = node.Type switch
            {
                SwitchboardNode.Root or SwitchboardNode.Menu or "time" => under.Count == 0 && setup.NoKeyPress?.TransferToNodeId is null,
                "group" => node.GroupId is null,
                "user" => node.UserUuid is null,
                "contact" => node.ContactUuid is null && setup.TargetDid is null,
                _ => false,
            };
            if (empty)
            {
                Flag(HealthIssue.EmptyTarget);
            }

            if (setup.DialByExt is { Enabled: true, LimitedToAllowList: true, AllowList.Count: 0 })
            {
                Flag(HealthIssue.EmptyAllowList);
            }

            var timed = under.Where(n => n.Type == "time").ToList();
            if (timed.Count > 0)
            {
                var closed = timed.FirstOrDefault(n => n.Setup.OpeningHours?.IsDefault == true);
                if (closed is null || !children[closed.Id].Any())
                {
                    Flag(HealthIssue.ClosedBranchWithNoAction);
                }
            }

            if (setup.NoKeyPress?.TransferToNodeId is { } target && !byId.ContainsKey(target) && !nodes.Any(n => n.InternalId?.ToString(System.Globalization.CultureInfo.InvariantCulture) == target))
            {
                Flag(HealthIssue.NoKeyPressTargetMissing);
            }
        }

        return flags;
    }

    private static bool IsDisabled(SwitchboardNode node, Dictionary<string, SwitchboardNode> byId)
    {
        for (SwitchboardNode? at = node; at is not null; at = at.ParentId is { } parent && byId.TryGetValue(parent, out var up) ? up : null)
        {
            if (at.Setup.Disabled == true)
            {
                return true;
            }
        }

        return false;
    }
}
