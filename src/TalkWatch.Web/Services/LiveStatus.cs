using TalkWatch.Core.Talk;

namespace TalkWatch.Web.Services;

public sealed record LiveUser(string Uuid, string Name, string? Ext, string? Presence, bool OnCall)
{
    /// <summary>"busy" on a call, "dnd" for do not disturb, "offline", otherwise "available": as the Operator board shows them.</summary>
    public string State => OnCall ? "busy" : Presence?.ToLowerInvariant() switch
    {
        "dnd" or "do_not_disturb" or "busy" => "dnd",
        "offline" or "unavailable" => "offline",
        _ => "available",
    };
}

public sealed record LiveDevice(string Mac, string? Model, string? Name, string? UserUuid, string? Ext, string? Status, bool? Registered, DateTimeOffset? LastSeen, bool? UpdateAvailable = null);

/// <summary>
/// What is happening on the console right now, kept from Talk's live WebSocket and the user directory. Pages
/// subscribe to <see cref="Changed"/> and re-render; nothing here is stored, it is rebuilt on every connection.
/// </summary>
public sealed class LiveStatus
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LiveUser> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uuidByTalkId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LiveDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public bool Connected { get; private set; }

    public DateTimeOffset? LastEventAt { get; private set; }

    public string? LastError { get; private set; }

    public (IReadOnlyList<LiveUser> Users, IReadOnlyList<LiveDevice> Devices) Snapshot()
    {
        lock (_gate)
        {
            return ([.. _users.Values.OrderBy(u => u.Ext, StringComparer.Ordinal)], [.. _devices.Values.OrderBy(d => d.Ext, StringComparer.Ordinal)]);
        }
    }

    public void SetConnected(bool connected, string? error = null)
    {
        Connected = connected;
        LastError = error;
        Changed?.Invoke();
    }

    /// <summary>
    /// The people a call to one of the account's numbers can reach: not console accounts no call reaches, and not
    /// contacts, however Talk lists them.
    /// </summary>
    public void SetDirectory(LineDirectory directory)
    {
        var reach = directory.Reachable();
        SetDirectory(directory.Users.Where(u => reach.Users.Contains(u.Uuid)));
    }

    /// <summary>The people to follow, in place of any before: someone no longer listed is no longer shown.</summary>
    public void SetDirectory(IEnumerable<TalkUser> users)
    {
        lock (_gate)
        {
            var listed = users.Where(u => !u.HideFromUserList).ToList();
            foreach (var gone in _users.Keys.Except(listed.Select(u => u.Uuid), StringComparer.Ordinal).ToList())
            {
                _users.Remove(gone);
            }

            foreach (var user in listed)
            {
                var previous = _users.GetValueOrDefault(user.Uuid);
                _users[user.Uuid] = new LiveUser(user.Uuid, user.DisplayName, user.Ext, previous?.Presence, previous?.OnCall ?? user.HasActiveCalls);
                if (user.Id is { } id)
                {
                    _uuidByTalkId[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = user.Uuid;
                }
            }
        }

        Changed?.Invoke();
    }

    public void Apply(LiveMessage message, DateTimeOffset at)
    {
        lock (_gate)
        {
            LastEventAt = at;
            switch (message.Event)
            {
                case LiveMessage.DevicesUpdated:
                    foreach (var d in message.Devices())
                    {
                        _devices[d.Mac] = new LiveDevice(d.Mac, d.Model, d.DisplayName, d.UserId, d.Ext, d.Status, d.SipReg, d.LastSeen, d.UpdateAvailable);
                    }

                    break;

                case LiveMessage.UserStoreUpdated when message.UserPresence() is { } presence:
                    if (_users.TryGetValue(presence.UserUuid, out var user))
                    {
                        _users[presence.UserUuid] = user with { Presence = presence.Status ?? user.Presence, OnCall = presence.OnCall ?? user.OnCall };
                    }

                    break;

                case LiveMessage.UsersOnActiveCalls:
                    // The full list of who is on a call: everyone not in it is not. Ids may be uuids or Talk's numeric ids.
                    var onCall = message.UsersOnCalls().Select(id => _uuidByTalkId.GetValueOrDefault(id, id)).ToHashSet(StringComparer.Ordinal);
                    foreach (var (uuid, u) in _users.ToList())
                    {
                        _users[uuid] = u with { OnCall = onCall.Contains(uuid) };
                    }

                    break;
            }
        }

        Changed?.Invoke();
    }
}
