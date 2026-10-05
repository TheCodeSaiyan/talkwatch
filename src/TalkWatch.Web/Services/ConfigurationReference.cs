using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TalkWatch.Web.Services;

/// <summary>
/// Writes docs/configuration.md from the options classes: their sections, their properties' types and defaults, and
/// the doc comments on them. A test compares it with the committed file, so the reference cannot drift from the code.
/// </summary>
public static partial class ConfigurationReference
{
    /// <summary>Every options class, in the order the reference lists them.</summary>
    public static readonly Type[] Sections =
    [
        typeof(TalkOptions), typeof(SiteOptions), typeof(BootstrapOptions), typeof(DatabaseOptions), typeof(AudioOptions),
        typeof(ProxyOptions), typeof(SignInLimitOptions), typeof(SmtpOptions), typeof(TelegramOptions), typeof(OidcOptions), typeof(DemoOptions),
    ];

    /// <summary>Settings TalkWatch reads that are not its own options: the connection string, and standard ones.</summary>
    private static readonly (string Name, string Default, string Description)[] Others =
    [
        ("ConnectionStrings__TalkWatch", "required", "The PostgreSQL connection string, such as `Host=db;Database=talkwatch;Username=talkwatch`. Its password can be left out and given as `Database__Password`."),
        ("TALKWATCH_SECRETS_DIR", "`/run/secrets`", "Where secret files are read from. An environment variable only, since it says where the other settings come from."),
        ("OTEL_EXPORTER_OTLP_ENDPOINT", "none", "Where to send traces and metrics, such as `http://otel-collector:4317`. Both are off while this is unset. The other standard `OTEL_` settings apply as usual."),
        ("OTEL_SERVICE_NAME", "`talkwatch`", "The service name traces and metrics carry."),
    ];

    // A WireGuard config carries its private key.
    private static readonly string[] SecretNames = ["Password", "Secret", "Token", "AuthKey", "WireGuardConfig"];

    /// <summary>A property TalkWatch reads as a setting: public, settable, and not worked out from others.</summary>
    public static IEnumerable<PropertyInfo> Settings(Type options) =>
        options.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite);

    public static string SectionOf(Type options) => (string)options.GetField("Section")!.GetValue(null)!;

    /// <summary>The doc comment summary of a type or property, on one line, or null when it has none.</summary>
    public static string? Describe(XDocument docs, MemberInfo member)
    {
        var id = member is Type type ? $"T:{type.FullName}" : $"P:{member.DeclaringType!.FullName}.{member.Name}";
        var summary = docs.Descendants("member").FirstOrDefault(m => (string?)m.Attribute("name") == id)?.Element("summary");
        if (summary is null)
        {
            return null;
        }

        // <see cref="T:X.Y"/> becomes Y; the rest is the text, whitespace collapsed.
        foreach (var see in summary.Descendants("see").ToList())
        {
            see.ReplaceWith(((string?)see.Attribute("cref"))?.Split('.', ':').Last() ?? "");
        }

        return Whitespace().Replace(summary.Value, " ").Trim();
    }

    public static XDocument LoadDocs() =>
        XDocument.Load(Path.ChangeExtension(typeof(ConfigurationReference).Assembly.Location, ".xml"));

    public static string Build()
    {
        var docs = LoadDocs();
        var md = new StringBuilder();
        md.Append("""
            # Configuration

            <!-- Generated from the options classes in src/TalkWatch.Web/Services by ConfigurationReference; do not edit by hand. A test fails when this file and the code differ, and writes the new version. -->

            TalkWatch is configured by environment variables, with `__` between a section and a setting: `Talk__ConsoleUrl` sets `ConsoleUrl` in the `Talk` section.

            A setting can also come from a file in `/run/secrets` named after it, such as `/run/secrets/Talk__Password`, which overrides the environment. Use files for anything marked as a secret below; Docker and Compose secrets land there by default.

            """);

        foreach (var options in Sections)
        {
            md.Append(CultureInfo.InvariantCulture, $"\n## {SectionOf(options)}\n\n{Describe(docs, options)}\n\n");
            md.Append("| Setting | Default | Description |\n|---|---|---|\n");
            var defaults = Activator.CreateInstance(options);
            foreach (var setting in Settings(options))
            {
                var secret = SecretNames.Any(s => setting.Name.Contains(s, StringComparison.Ordinal)) ? " **Secret:** give it as a file." : "";
                md.Append(CultureInfo.InvariantCulture,
                    $"| `{SectionOf(options)}__{setting.Name}` | {Default(setting.GetValue(defaults))} | {Describe(docs, setting)}{secret} |\n");
            }
        }

        md.Append("\n## Not in a section\n\n| Setting | Default | Description |\n|---|---|---|\n");
        foreach (var (name, fallback, description) in Others)
        {
            md.Append(CultureInfo.InvariantCulture, $"| `{name}` | {fallback} | {description} |\n");
        }

        // The same text whatever line endings the source was checked out with: the header above is a raw string.
        return md.ToString().ReplaceLineEndings("\n");
    }

    private static string Default(object? value) => value switch
    {
        null or "" => "none",
        bool b => b ? "`true`" : "`false`",
        IFormattable f => $"`{f.ToString(null, CultureInfo.InvariantCulture)}`",
        _ => $"`{value}`",
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
