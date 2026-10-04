using TalkWatch.Capture;

const string PasswordVariable = "TALKWATCH_CONSOLE_PASSWORD";

try
{
    return args switch
    {
        ["keygen"] => KeyGen(),
        ["cert", var console] => await Cert(new Uri(console)),
        ["import-har", var har, .. var rest] => ImportHar(har, Options.Parse(rest, allowPositional: false)),
        ["fetch", .. var rest] => await Fetch(Options.Parse(rest, allowPositional: false)),
        ["list", .. var archives] when archives.Length > 0 => List(archives),
        ["fixtures", .. var rest] => Fixtures(Options.Parse(rest)),
        _ => Usage(),
    };
}
catch (Exception e) when (e is InvalidOperationException or InvalidDataException or NotSupportedException or HttpRequestException or IOException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("""
        talkwatch-capture: record the UniFi Talk API into encrypted archives, and turn them into safe fixtures.

          keygen
              Print a new capture key. Store it in 1Password; every other command reads it from TALKWATCH_CAPTURE_KEY.

          cert <https://console>
              Print the SHA-256 of the console's certificate, to check against the console and pass to --pin.

          import-har <file.har> --out <capture.twcap> [--unifi-os <version>] [--talk <version>] [--note <text>]
              Seal the API traffic from a browser HAR of the Talk UI. Delete the HAR afterwards: it is not encrypted.

          fetch --console <https://console> --user <local account> --pin <sha256> --out <capture.twcap>
                (--paths <file> | --paths-from <capture.twcap>) [--unifi-os <version>] [--talk <version>] [--note <text>]
              Sign in (password from TALKWATCH_CONSOLE_PASSWORD) and GET each path. --paths-from re-requests every
              GET path found in an earlier capture.

          list <capture.twcap>...
              Show what each archive holds. Paths are pseudonymised, and no bodies are shown.

          fixtures <capture.twcap>... --out tests/fixtures/<name> [--include <prefix>,...] [--repo <root>]
              Pseudonymise into fixtures, then run the fixture guard over them. Output that fails is deleted.
              --include keeps only paths starting with one of the prefixes, e.g. /proxy/talk/. Exact duplicate
              responses are always left out.
        """);
    return 2;
}

static int KeyGen()
{
    Console.WriteLine(CaptureKey.Generate());
    return 0;
}

static async Task<int> Cert(Uri console)
{
    Console.WriteLine(await ConsoleFetcher.ReadCertificateSha256Async(console, CancellationToken.None));
    return 0;
}

static int ImportHar(string har, Options options)
{
    var key = CaptureKey.FromEnvironment();
    List<CaptureEntry> entries;
    using (var stream = File.OpenRead(har))
    {
        entries = [.. HarImporter.Import(stream)];
    }

    var archive = new CaptureArchive(options.Manifest("har"), entries);
    archive.Save(options.Required("--out"), key);
    Console.WriteLine($"{entries.Count} entries sealed into {options.Required("--out")}. Now delete {har}: it holds the same data unencrypted.");
    return 0;
}

static async Task<int> Fetch(Options options)
{
    var key = CaptureKey.FromEnvironment();
    var password = Environment.GetEnvironmentVariable(PasswordVariable) is { Length: > 0 } p
        ? p
        : throw new InvalidOperationException($"Set {PasswordVariable} to the local account's password.");

    var paths = options.Get("--paths") is { } list
        ? File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Distinct().ToList()
        : CaptureArchive.Load(options.Required("--paths-from"), key).Entries
            .Where(e => e is { Kind: "http", Method: "GET" }).Select(e => e.Path).Distinct().ToList();

    using var fetcher = new ConsoleFetcher(new Uri(options.Required("--console")), ConsoleFetcher.CreateHandler(options.Get("--pin")));
    await fetcher.SignInAsync(options.Required("--user"), password, CancellationToken.None);

    var entries = new List<CaptureEntry>();
    foreach (var path in paths)
    {
        var entry = await fetcher.GetAsync(path, CancellationToken.None);
        entries.Add(entry);
        Console.WriteLine($"{entries.Count,4}/{paths.Count}  HTTP {entry.Status}");
    }

    new CaptureArchive(options.Manifest("fetch"), entries).Save(options.Required("--out"), key);
    Console.WriteLine($"{entries.Count} responses sealed into {options.Required("--out")}.");
    return 0;
}

static int List(string[] archives)
{
    var key = CaptureKey.FromEnvironment();
    var pseudonymiser = new Pseudonymiser(key.PseudonymKey);
    foreach (var file in archives)
    {
        var archive = CaptureArchive.Load(file, key);
        var m = archive.Manifest;
        Console.WriteLine($"{file}: {m.Source}, captured {m.CapturedUtc:u}, UniFi OS {m.UnifiOsVersion ?? "?"}, Talk {m.TalkVersion ?? "?"}, {archive.Entries.Count} entries");
        foreach (var entry in archive.Entries)
        {
            Console.WriteLine($"  {CaptureArchive.Describe(entry)}  {pseudonymiser.Path(entry.Path)}");
        }
    }

    return 0;
}

static int Fixtures(Options options)
{
    var key = CaptureKey.FromEnvironment();
    var archives = options.Positional.Select(f => CaptureArchive.Load(f, key)).ToList();
    if (archives.Count == 0)
    {
        throw new InvalidOperationException("Name at least one capture archive.");
    }

    var include = (options.Get("--include") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var (selected, outside, duplicates) = FixtureWriter.Select(archives, include);
    Console.WriteLine($"{outside} entries outside --include and {duplicates} exact duplicates left out.");

    var result = FixtureWriter.Write(Path.GetFullPath(options.Get("--repo") ?? "."), options.Required("--out"), selected, key);
    foreach (var finding in result.Findings)
    {
        Console.Error.WriteLine(finding);
    }

    if (result.Findings.Count > 0)
    {
        Console.Error.WriteLine("The fixture guard refused the output, so it has been deleted. Nothing was kept.");
        return 1;
    }

    Console.WriteLine($"{result.Files} files ({result.AudioFiles} synthetic audio), {result.NumbersReplaced} distinct numbers replaced, {result.Dropped} binary entries left out.");
    Console.WriteLine($"Before committing, read {options.Required("--out")}/fields.txt for any field that holds personal data the rules did not catch.");
    return 0;
}

internal sealed class Options
{
    private readonly Dictionary<string, string> _named = new(StringComparer.Ordinal);

    public List<string> Positional { get; } = [];

    /// <param name="allowPositional">False where a stray word is a mistake, such as a second command typed on the same line.</param>
    public static Options Parse(string[] args, bool allowPositional = true)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                options._named[args[i]] = i + 1 < args.Length ? args[++i] : throw new InvalidOperationException($"{args[i]} needs a value.");
            }
            else if (allowPositional)
            {
                options.Positional.Add(args[i]);
            }
            else
            {
                throw new InvalidOperationException($"Unexpected argument '{args[i]}'. Run each command on its own line.");
            }
        }

        return options;
    }

    public string? Get(string name) => _named.GetValueOrDefault(name);

    public string Required(string name) => Get(name) ?? throw new InvalidOperationException($"{name} is required.");

    public CaptureManifest Manifest(string source) =>
        new(DateTimeOffset.UtcNow, source, Get("--unifi-os"), Get("--talk"), Get("--note"));
}
