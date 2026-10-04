using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Core.Tests;

/// <summary>
/// Who a call to one of the account's numbers can reach: the people the Operator board shows, and the contacts calls are
/// put through to. Console accounts no number reaches are not people to hand a call to, and contacts are never people.
/// </summary>
public class OperatorReachTests
{
    private static readonly NumberNormaliser Uk = new("GB");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TalkUser User(string uuid, string? did = null) => new() { Uuid = uuid, FullName = uuid, Ext = "10" + uuid.Length, Did = did };

    private static TalkContact Contact(int id, string uuid) => new() { Id = id, Uuid = uuid, FirstName = uuid };

    private static TalkGroup.Member Meta(string id, bool user) => new() { MemberId = id, GroupMemberData = new TalkGroup.MemberData { MemberIsUser = user } };

    private static SwitchboardNode Node(string id, string type, string? parent, IReadOnlyList<string>? numbers = null, int? internalId = null) =>
        new(id, internalId, type, id, null, parent, numbers ?? [], null);

    [Fact]
    public async Task On_the_captured_console_only_the_user_a_number_reaches_and_the_contact_a_switchboard_puts_calls_through_to_count()
    {
        var talk = new TalkClient(new FixtureConsole(FixtureConsole.DefaultDirectory).CreateClient());
        var directory = await talk.GetDirectoryAsync(Uk, Ct);
        directory.Switchboard = await talk.GetSwitchboardAsync(Ct);

        var reach = directory.Reachable();

        // Read from Talk's own field names: who a group's members are, the user a number rings, the contact a node puts calls to.
        Assert.True(directory.Groups[0].MemberListMeta?[0].GroupMemberData?.MemberIsUser);
        Assert.Equal("abe3a229-7539-48dd-a8e3-e23fd4d9f72f", directory.Numbers.Single(n => n.Did == "+441174960404").UserId);
        Assert.Contains(directory.Switchboard, n => n.ContactUuid == "ac7a4901-2722-4e41-9f20-87f95df72cb2");
        Assert.Contains(directory.Switchboard, n => n.GroupId == directory.Groups[0].Id);
        Assert.True(reach.Complete);
        // Nico Archer has a number of their own and is in the ring group; the five others have extensions and nothing rings them.
        Assert.Equal(["abe3a229-7539-48dd-a8e3-e23fd4d9f72f"], reach.Users);
        Assert.True(directory.Users.Count(u => !u.HideFromUserList) > 1);
        // The main line's switchboard puts calls through to one contact; the other two are only in the address book.
        Assert.Equal(["ac7a4901-2722-4e41-9f20-87f95df72cb2"], reach.Contacts.Select(c => c.Uuid));
    }

    [Fact]
    public void A_number_reaches_its_users_groups_switchboards_and_where_an_unanswered_group_call_goes_next()
    {
        var users = new[] { User("own", did: "+441144960001"), User("sales"), User("support"), User("assigned"), User("overflow"), User("admin"), User("contact-in-users") };
        var groups = new[]
        {
            new TalkGroup { Id = "1", Name = "Sales", MemberList = ["sales", "mobile"], MemberListMeta = [Meta("sales", true), Meta("mobile", false)], DidList = ["01144960002"], TransferToUserUuid = "overflow" },
            new TalkGroup { Id = "2", Name = "Support", MemberList = ["support"] },
            new TalkGroup { Id = "3", Name = "Nobody rings this", MemberList = ["admin"] },
        };
        var numbers = new[]
        {
            new TalkNumber { Did = "+441144960003", UserId = "assigned" },
            new TalkNumber { Did = "+441144960004", SmartAttendant = new TalkNumber.AttendantNode { InternalId = "9" } },
        };
        var directory = new LineDirectory(users, groups, numbers, Uk)
        {
            Contacts = [Contact(1, "mobile"), Contact(2, "plumber"), Contact(3, "locum"), Contact(4, "contact-in-users")],
            Switchboard =
            [
                Node("swb_1", SwitchboardNode.Root, null, internalId: 9),
                Node("swb_2", SwitchboardNode.Menu, "swb_1"),
                Node("grp_2_2", "group", "swb_2") with { GroupId = "2" },
                Node("cnt_2", "contact", "swb_2") with { ContactUuid = "locum" },
            ],
        };

        var reach = directory.Reachable();

        Assert.Equal(new HashSet<string> { "own", "sales", "support", "assigned", "overflow" }, reach.Users.ToHashSet());
        Assert.Equal(new HashSet<string> { "mobile", "locum" }, reach.Contacts.Select(c => c.Uuid!).ToHashSet());
        Assert.DoesNotContain("admin", reach.Users);
        Assert.DoesNotContain("contact-in-users", reach.Users);
    }

    // The board narrowed to one number in the switcher: only the groups, people and contacts a call to it can reach.
    [Fact]
    public void One_number_reaches_only_its_own_groups_people_and_contacts()
    {
        var directory = TwoNumbers();

        var attendant = directory.Reachable("+441144960004", Uk);
        var sales = directory.Reachable("+441144960002", Uk);
        var assigned = directory.Reachable("+441144960003", Uk);

        Assert.Equal(["2"], attendant.Groups.Order());
        Assert.Equal(["support"], attendant.Users.Order());
        Assert.Equal(["locum"], attendant.Contacts.Select(c => c.Uuid!).Order());
        Assert.Equal(["1"], sales.Groups.Order());
        Assert.Equal(["overflow", "sales"], sales.Users.Order());
        Assert.Equal(["mobile"], sales.Contacts.Select(c => c.Uuid!).Order());
        Assert.Empty(assigned.Groups);
        Assert.Equal(["assigned"], assigned.Users.Order());
        // All numbers together, as before: every group a call can ring.
        Assert.Equal(["1", "2"], directory.Reachable().Groups.Order());
    }

    // The outside numbers a ring group puts calls to, shown in its team on the board: members that are contacts (marked so,
    // or known to the directory as contacts), and where an unanswered call goes next.
    [Fact]
    public void A_groups_outside_numbers_are_its_contact_members_and_its_forward()
    {
        var group = new TalkGroup
        {
            Id = "1", MemberList = ["sales", "mobile", "locum"], MemberListMeta = [Meta("sales", true), Meta("mobile", false)], TransferToContactUuid = "answering",
        };

        var outside = group.ContactsIn(new HashSet<string> { "locum", "answering" }).ToList();

        Assert.Equal(["mobile", "locum", "answering"], outside);
        Assert.Empty(new TalkGroup { Id = "2", MemberList = ["sales"] }.ContactsIn(new HashSet<string>()));
    }

    [Theory]
    [InlineData("ring_group", true)]
    [InlineData(null, true)]
    [InlineData("paging_group", false)]
    [InlineData("paging", false)]
    public void Only_a_ring_group_takes_calls(string? type, bool takes) =>
        Assert.Equal(takes, new TalkGroup { Id = "1", GroupType = type }.TakesCalls);

    private static LineDirectory TwoNumbers() => new(
        [User("own", did: "+441144960001"), User("sales"), User("support"), User("assigned"), User("overflow"), User("admin")],
        [
            new TalkGroup { Id = "1", Name = "Sales", MemberList = ["sales", "mobile"], MemberListMeta = [Meta("sales", true), Meta("mobile", false)], DidList = ["01144960002"], TransferToUserUuid = "overflow" },
            new TalkGroup { Id = "2", Name = "Support", MemberList = ["support"] },
            new TalkGroup { Id = "3", Name = "Nobody rings this", MemberList = ["admin"] },
        ],
        [
            new TalkNumber { Did = "+441144960003", UserId = "assigned" },
            new TalkNumber { Did = "+441144960004", SmartAttendant = new TalkNumber.AttendantNode { InternalId = "9" } },
        ],
        Uk)
    {
        Contacts = [Contact(1, "mobile"), Contact(3, "locum")],
        Switchboard =
        [
            Node("swb_1", SwitchboardNode.Root, null, internalId: 9),
            Node("swb_2", SwitchboardNode.Menu, "swb_1"),
            Node("grp_2_2", "group", "swb_2") with { GroupId = "2" },
            Node("cnt_2", "contact", "swb_2") with { ContactUuid = "locum" },
        ],
    };

    [Fact]
    public void When_numbers_go_to_a_switchboard_that_has_not_been_read_nobody_is_hidden_but_contacts_are_still_not_people()
    {
        var directory = new LineDirectory(
            [User("a"), User("b"), User("contact-in-users")], [],
            [new TalkNumber { Did = "+441144960004", SmartAttendant = new TalkNumber.AttendantNode { InternalId = "9" } }], Uk)
        {
            Contacts = [Contact(4, "contact-in-users")],
        };

        var reach = directory.Reachable();

        Assert.False(reach.Complete);
        Assert.Equal(new HashSet<string> { "a", "b" }, reach.Users.ToHashSet());
    }
}
