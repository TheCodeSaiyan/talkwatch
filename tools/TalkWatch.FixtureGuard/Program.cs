using TalkWatch.FixtureGuard;

// Usage: TalkWatch.FixtureGuard <repository root> [repository-relative paths...]
// With no paths, every file under tests/fixtures is scanned.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: TalkWatch.FixtureGuard <repository root> [paths...]");
    return 2;
}

var root = Path.GetFullPath(args[0]);
var paths = args.Length > 1
    ? args[1..]
    : Directory.Exists(Path.Combine(root, Guard.FixturesDirectory))
        ? Directory.EnumerateFiles(Path.Combine(root, Guard.FixturesDirectory), "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p))
            .ToArray()
        : [];

var findings = Guard.Scan(root, paths);
foreach (var finding in findings)
{
    Console.Error.WriteLine(finding);
}

if (findings.Count > 0)
{
    Console.Error.WriteLine($"fixture guard: {findings.Count} problem(s). Pseudonymise the data, or list a value that is not a phone number in {Guard.AllowListPath}.");
    return 1;
}

Console.WriteLine($"fixture guard: {paths.Length} file(s) checked, nothing found.");
return 0;
