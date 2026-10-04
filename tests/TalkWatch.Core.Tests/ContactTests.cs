using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Core.Tests;

public class ContactTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Contacts_name_their_numbers_and_the_contact_lines_calls_name_by_id_or_uuid()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var talk = new TalkClient(console.CreateClient());
        await talk.SignInAsync(FixtureConsole.Username, FixtureConsole.Password, Ct);
        var numbers = new NumberNormaliser("GB");

        var directory = await talk.GetDirectoryAsync(numbers, Ct);

        var harper = directory.Contacts.Single(c => c.Id == 2);
        Assert.Equal("Harper13 Hayden14", harper.DisplayName);
        Assert.Equal("Harper13 Hayden14 (mobile)", directory.CallerNames(numbers)["+447700900880"]);
        var lines = directory.Lines(numbers).Where(l => l.Kind == LineKind.Contact).ToList();
        Assert.Contains((LineKind.Contact, "2", "Harper13 Hayden14", (string?)null), lines);
        Assert.Contains((LineKind.Contact, harper.Uuid!, "Harper13 Hayden14", (string?)null), lines);
        Assert.Equal(directory.Contacts.Count, lines.Count(l => int.TryParse(l.Key, out _)));
    }

    [Fact]
    public void A_contact_with_no_name_is_called_by_its_organisation_or_its_number_in_the_list()
    {
        Assert.Equal("Acme", new TalkContact { Id = 7, Organization = "Acme" }.DisplayName);
        Assert.Equal("Contact 7", new TalkContact { Id = 7, FirstName = " " }.DisplayName);
    }
}
