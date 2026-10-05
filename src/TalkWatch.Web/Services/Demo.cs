using TalkWatch.Replay;

namespace TalkWatch.Web.Services;

/// <summary>
/// Demo mode: TalkWatch reads a console replayed from the fixtures that ship in the image, so it can be tried without
/// one. Every number in them is fictional and every recording synthetic.
/// </summary>
public sealed class DemoOptions
{
    public const string Section = "Demo";

    /// <summary>
    /// Run on the replayed console instead of a real one, with call times moved so the newest is an hour old. The live
    /// feed is off, and every page says the data is made up.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The fixtures to replay: a folder of captured responses, and its '-voicemail' sibling if there is one.</summary>
    public string Fixtures { get; set; } = "/app/demo/talk-5.3.2";

    /// <summary>
    /// How often a new call arrives on the replay, stamped now, so alerts fire and Now has something happening; 0 for
    /// none. Each kind of call comes in turn: missed, the same caller again, voicemail, answered, poor quality, hung up
    /// in the menu.
    /// </summary>
    public int CallEveryMinutes { get; set; } = 4;

    /// <summary>
    /// Set the demo up with something to look at: alerts in TalkWatch and example flows for the bootstrap admin, who is
    /// linked to someone in a ring group and carries an outside phone, as the guest does; people holding roles on the
    /// number; and two reports, each run once so there is a copy to open. Each part is set up only while there is none,
    /// so changes made in the demo stay, and a demo from before a part existed gets it at its next start.
    /// </summary>
    public bool Seed { get; set; } = true;

    /// <summary>
    /// A shared account for anyone trying the demo, shown on the sign-in page with a button that signs in as it. It is an
    /// admin, short of API tokens, so every page and button is there to try; what would change the site for whoever
    /// comes next (people, roles, roles on numbers, group mappings, retention, the mail and Telegram settings) is refused
    /// when saved, as is any channel but the browser's own. It can't turn on two-factor sign-in, and its password is set
    /// back to this one each start. Empty for no guest.
    /// </summary>
    public string GuestUsername { get; set; } = "guest";

    /// <summary>
    /// How often the demo starts over: every call, alert, flow, channel and report it has gathered is cleared and the
    /// example set up again, and the banner counts down to it. People and roles are kept, so nobody is signed out. 0
    /// for never.
    /// </summary>
    public int ResetMinutes { get; set; } = 30;

    /// <summary>The guest's password, published on the sign-in page: the data is made up, and nothing the guest changes outlasts the next start-over.</summary>
    public string GuestPassword { get; set; } = "try-talkwatch";

    /// <summary>Whether this person is the demo's shared guest.</summary>
    public bool IsGuest(string? username) => Enabled && GuestUsername.Length > 0 && string.Equals(username, GuestUsername, StringComparison.OrdinalIgnoreCase);
}

public static class Demo
{
    /// <summary>After the console client is registered: the replay replaces the client's handler, so it has to come later.</summary>
    public static void AddDemo(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(DemoOptions.Section).Get<DemoOptions>() ?? new DemoOptions();
        builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection(DemoOptions.Section));
        if (!options.Enabled)
        {
            return;
        }

        // The replay answers whatever address it is given; these only switch polling on and sign it in.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Talk:ConsoleUrl"] = "https://demo.console",
            ["Talk:Username"] = FixtureConsole.Username,
            ["Talk:Password"] = FixtureConsole.Password,
        });

        var console = new FixtureConsole(options.Fixtures) { Rewrite = DemoNames.Apply };
        console.ShiftTimes(DateTimeOffset.UtcNow.AddHours(-1));
        builder.Services.AddHttpClient(TalkSession.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => console);
        builder.Services.AddSingleton(console);
        builder.Services.AddSingleton<DemoActivity>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DemoActivity>());
        builder.Services.AddSingleton<DemoScenarios>();
    }
}
