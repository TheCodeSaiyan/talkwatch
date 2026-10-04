using System.Text.Json;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Core.Tests;

public class LiveTests
{
    private static IEnumerable<string> Messages() =>
        Directory.GetFiles(FixtureConsole.DefaultDirectory, "*-ws-proxy-talk.json").Select(File.ReadAllText);

    [Fact]
    public void Every_captured_live_message_parses()
    {
        var parsed = Messages().Select(LiveMessage.Parse).ToList();

        Assert.NotEmpty(parsed);
        Assert.All(parsed, m => Assert.NotNull(m));
    }

    [Fact]
    public void Call_log_updates_carry_calls_in_the_call_log_shape()
    {
        var records = Messages().Select(LiveMessage.Parse).Where(m => m!.Event == LiveMessage.CallLogUpdated).SelectMany(m => m!.CallRecords()).ToList();

        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.False(string.IsNullOrEmpty(r.Uuid)));
    }

    [Fact]
    public void Devices_name_the_user_they_belong_to()
    {
        var devices = Messages().Select(LiveMessage.Parse).Where(m => m!.Event == LiveMessage.DevicesUpdated).SelectMany(m => m!.Devices()).ToList();

        Assert.NotEmpty(devices);
        Assert.All(devices, d => Assert.False(string.IsNullOrEmpty(d.Mac)));
        Assert.Contains(devices, d => d.UserId is { Length: > 0 } && d.Status is "online" or "offline");
    }

    [Fact]
    public void User_store_updates_give_presence_and_whether_someone_is_on_a_call()
    {
        var presence = Messages().Select(LiveMessage.Parse).Where(m => m!.Event == LiveMessage.UserStoreUpdated).Select(m => m!.UserPresence()).ToList();

        Assert.NotEmpty(presence);
        Assert.All(presence, p => Assert.False(string.IsNullOrEmpty(p!.Value.UserUuid)));
        Assert.Contains(presence, p => p!.Value.OnCall.HasValue);
    }

    [Fact]
    public void The_user_directory_reads_with_uuids_and_names()
    {
        var file = Directory.GetFiles(FixtureConsole.DefaultDirectory, "*-http-proxy-talk-api-users.json").First();

        var users = JsonSerializer.Deserialize<List<TalkUser>>(File.ReadAllBytes(file), TalkJson.Options)!;

        Assert.NotEmpty(users);
        Assert.All(users, u => Assert.True(Guid.TryParse(u.Uuid, out _)));
        Assert.All(users, u => Assert.False(string.IsNullOrEmpty(u.DisplayName)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"no_event":1}""")]
    [InlineData("[1,2]")]
    public void Anything_else_is_ignored_rather_than_thrown(string text) => Assert.Null(LiveMessage.Parse(text));
}
