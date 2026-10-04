using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public class ConfigurationReferenceTests
{
    private static string Committed()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "TalkWatch.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return Path.Combine(directory ?? throw new InvalidOperationException("Repository root not found."), "docs", "configuration.md");
    }

    [Fact]
    public void Every_setting_and_section_is_described()
    {
        var docs = ConfigurationReference.LoadDocs();

        var undescribed = ConfigurationReference.Sections
            .SelectMany(t => ConfigurationReference.Settings(t).Cast<System.Reflection.MemberInfo>().Prepend(t))
            .Where(m => string.IsNullOrWhiteSpace(ConfigurationReference.Describe(docs, m)))
            .Select(m => m is Type t ? t.Name : $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        Assert.True(undescribed.Count == 0, $"Give these a doc comment: {string.Join(", ", undescribed)}");
    }

    [Fact]
    public void The_committed_reference_matches_the_code()
    {
        var path = Committed();
        var generated = ConfigurationReference.Build();
        var committed = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";

        if (committed != generated)
        {
            // Written for review: the next run passes once it is committed.
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(generated));
            Assert.Fail($"docs/configuration.md was out of date with the code and has been rewritten; review and commit it.");
        }
    }
}
