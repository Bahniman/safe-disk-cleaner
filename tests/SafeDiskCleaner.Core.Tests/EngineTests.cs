using System.Globalization;
using SafeDiskCleaner.Core;

namespace SafeDiskCleaner.Core.Tests;

/// <summary>Helpers and rule-table checks that do not touch real user folders.</summary>
public class EngineTests
{
    [Theory]
    [InlineData("566.36", "566.36", 0)]
    [InlineData("566.14", "566.36", -1)]
    [InlineData("app-1.0.9001", "app-1.0.10000", -1)]
    [InlineData("1.2", "1.2.0", -1)]
    [InlineData("2.0", "1.99.99", 1)]
    [InlineData("no digits", "0", 0)]
    public void VersionKey_compares_like_a_tuple(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(VersionKey.Parse(a).CompareTo(VersionKey.Parse(b))));
    }

    [Fact]
    public void Glob_matches_segments_and_skips_dot_names_like_python()
    {
        using var t = new TempDir();
        t.Dir("notion-updater");
        t.Dir("obsidian-updater");
        t.Dir(".hidden-updater");
        t.Dir("updater-not");
        t.File("x", "Discord", "Update.exe");
        t.File("x", "Slack", "Update.exe");
        t.Dir("Empty");

        List<string> updaters = PathUtil.Glob(Path.Combine(t.Root, "*-updater"));
        Assert.Equal(2, updaters.Count);
        Assert.All(updaters, u => Assert.EndsWith("-updater", u));

        List<string> squirrel = PathUtil.G(t.Root, "*", "Update.exe");
        Assert.Equal(2, squirrel.Count);

        Assert.Empty(PathUtil.Glob(Path.Combine(t.Root, "missing", "*")));
    }

    [Fact]
    public void Chromium_caches_are_recognised_by_their_contents()
    {
        using var t = new TempDir();
        t.File("x", "User Data", "Default", "Cache", "Cache_Data", "index");
        t.File("x", "User Data", "Default", "Code Cache", "js", "index");
        t.File("x", "User Data", "Default", "GPUCache", "data_0");
        t.File("x", "User Data", "Default", "Service Worker", "CacheStorage", "abc");
        t.File("x", "User Data", "Profile 1", "Cache", "my-notes.txt");        // a folder called Cache that is not one
        t.File("x", "User Data", "OptGuideOnDeviceModel", "Cache", "index");   // never touched
        t.File("x", "Profiles", "abcd.default-release", "cache2", "entries", "ABC");

        List<string> found = AppCaches.CacheDirs(Path.Combine(t.Root, "User Data"));
        Assert.Contains(found, p => p.EndsWith(@"Default\Cache", StringComparison.Ordinal));
        Assert.Contains(found, p => p.EndsWith(@"Default\Code Cache", StringComparison.Ordinal));
        Assert.Contains(found, p => p.EndsWith(@"Default\GPUCache", StringComparison.Ordinal));
        Assert.Contains(found, p => p.EndsWith(@"Service Worker\CacheStorage", StringComparison.Ordinal));
        Assert.DoesNotContain(found, p => p.Contains("Profile 1", StringComparison.Ordinal));
        Assert.DoesNotContain(found, p => p.Contains("OptGuideOnDeviceModel", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, found.Count);

        Assert.Single(AppCaches.CacheDirs(Path.Combine(t.Root, "Profiles")));
    }

    [Fact]
    public void Old_versions_keep_the_newest_and_the_extra_ones()
    {
        using var t = new TempDir();
        foreach (string v in new[] { "1.2.3", "1.10.0", "1.9.9", "notes" })
        {
            t.Dir("versions", v);
        }
        string b = Path.Combine(t.Root, "versions");
        List<string> old = RuleCatalog.OldVersions(b).Select(Path.GetFileName).ToList()!;
        Assert.Equal(["1.2.3", "1.9.9"], old.Order());

        List<string> kept = RuleCatalog.OldVersions(b, ["1.9.9"]).Select(Path.GetFileName).ToList()!;
        Assert.Equal(["1.2.3"], kept);
    }

    [Fact]
    public void Protect_file_parsing_skips_comments_and_expands_variables()
    {
        Environment.SetEnvironmentVariable("SDC_TEST_ROOT", @"D:\Games");
        List<string> paths = UserProtection.Parse(
        [
            "# comment",
            "",
            "   ",
            @"%SDC_TEST_ROOT%\Mods",
            @"""C:\Quoted Folder\Keep""",
            "C:",
        ]);
        Assert.Equal([@"D:\Games\Mods", @"C:\Quoted Folder\Keep"], paths);
    }

    [Fact]
    public void Protect_file_is_created_with_a_comment_header()
    {
        using var t = new TempDir();
        string file = UserProtection.EnsureFile(Path.Combine(t.Root, "sub", "protect.txt"));
        string text = File.ReadAllText(file);
        Assert.StartsWith("# Safe Disk Cleaner", text);
        Assert.Empty(UserProtection.Read(file));
    }

    [Fact]
    public void Run_log_appends_rows_and_a_total_and_reads_the_last_run()
    {
        using var t = new TempDir();
        string file = Path.Combine(t.Root, "runs.csv");
        var when = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Local);
        RunLog.Append([new LogRow(when, "temp", @"C:\x, with comma", 100, "deleted")], 1_500_000_000, when, file);
        RunLog.Append([new LogRow(when.AddDays(1), "recycle", "=SUM(A1)", 5, "done")], 42, when.AddDays(1), file);

        string[] lines = File.ReadAllLines(file);
        Assert.Equal("time,rule,path,bytes,result", lines[0]);
        Assert.Equal(5, lines.Length);
        Assert.Contains("\"C:\\x, with comma\"", lines[1]);
        Assert.Contains("'=SUM(A1)", lines[3]);
        Assert.Contains(",TOTAL,", lines[4]);

        (DateTime When, long Freed)? last = RunLog.ReadLast(file);
        Assert.NotNull(last);
        Assert.Equal(42, last.Value.Freed);
        Assert.Equal(when.AddDays(1), last.Value.When);
    }

    [Fact]
    public void Sizes_format_like_the_reference()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        Assert.Equal("1.5 GB", SizeFormat.Format(1610612736, c));
        Assert.Equal("512 MB", SizeFormat.Format(512L * 1024 * 1024, c));
        Assert.Equal("12 KB", SizeFormat.Format(12 * 1024, c));
        Assert.Equal("?", SizeFormat.Format(null, c));
        Assert.Equal("416.9 GB", SizeFormat.FormatGb((long)(416.9 * 1024 * 1024 * 1024), c));
    }

    [Fact]
    public void Rule_table_has_unique_ids_and_the_expected_defaults()
    {
        List<Rule> rules = RuleCatalog.Build();
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Title)));
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Why)));

        Rule recycle = rules.Single(r => r.Id == "recycle");
        Assert.False(recycle.DefaultChecked);
        Assert.Equal(RuleGroup.YourCall, recycle.Group);

        Rule dism = rules.Single(r => r.Id == "dism");
        Assert.False(dism.DefaultChecked);
        Assert.True(dism.NeedsAdmin);

        Assert.All(rules.Where(r => r.Group == RuleGroup.Windows), r => Assert.True(r.NeedsAdmin));
        Assert.All(rules.Where(r => r.Group == RuleGroup.Apps), r => Assert.NotNull(r.Busy));
        foreach (string id in new[] { "cache-chrome", "cache-brave", "cache-firefox", "cache-teams", "cache-spotify", "launcher-steam" })
        {
            Assert.Contains(rules, r => r.Id == id);
        }
    }

    [Fact]
    public void Apps_rules_skip_themselves_while_the_app_runs()
    {
        Rule chrome = RuleCatalog.Build().Single(r => r.Id == "cache-chrome");
        var running = new ProcessSnapshot([@"C:\Program Files\Google\Chrome\Application\chrome.exe"]);
        var idle = new ProcessSnapshot([@"C:\Windows\explorer.exe"]);
        Assert.Equal("Chrome is open, close it to clear this", chrome.Busy!(running));
        Assert.Null(chrome.Busy!(idle));
    }

    [Fact]
    public void Scan_runs_read_only_and_every_item_passed_the_guard()
    {
        // A real scan of this machine. It only reads; protect.txt is not created.
        ScanResult result = Scanner.Scan(admin: false, createProtectFile: false);
        Assert.NotEmpty(result.Rules);
        foreach (Rule r in result.Rules)
        {
            foreach (Item it in r.Items.Where(i => i.Path is not null))
            {
                Assert.Null(result.Guard.Check(it.Path!));
            }
            Assert.DoesNotContain(r.Skipped, s => s.Why.StartsWith("scan error", StringComparison.Ordinal));
        }
        Assert.All(result.Rules.Where(r => r.NeedsAdmin), r => Assert.Equal(Scanner.NeedsAdmin, r.Blocked));
        Console.WriteLine($"Scan took {result.Elapsed.TotalSeconds:0.0} s and found {SizeFormat.Format(result.Total)}.");
    }

    [Fact]
    public void Dry_run_clean_deletes_nothing_and_writes_no_log()
    {
        using var t = new TempDir();
        string cache = t.Dir("App", "Cache");
        string f = t.File("x", "App", "Cache", "data_0");
        var rule = new Rule("test", RuleGroup.Leftovers, "Test", "Test rule", (c, r) => []);
        rule.Items.Add(new Item(cache, size: 1));
        string log = Path.Combine(t.Root, "runs.csv");

        CleanResult res = Cleaner.Clean([rule], dryRun: true, guard: t.Guard(), logFile: log);

        Assert.True(File.Exists(f));
        Assert.False(File.Exists(log));
        Assert.Equal("dry run", rule.Items[0].Result);
        Assert.Equal(0, res.Problems);
    }

    [Fact]
    public void Clean_deletes_through_the_guard_and_logs_a_total()
    {
        using var t = new TempDir();
        string cache = t.Dir("App", "Cache");
        t.File(new string('x', 10_000), "App", "Cache", "data_1");
        string models = t.Dir("Models");
        string weights = t.File("w", "Models", "model.bin");
        var rule = new Rule("test", RuleGroup.Leftovers, "Test", "Test rule", (c, r) => []);
        rule.Items.Add(new Item(cache, size: 10_000));
        rule.Items.Add(new Item(models, size: 1));
        string log = Path.Combine(t.Root, "logs", "runs.csv");

        CleanResult res = Cleaner.Clean([rule], guard: t.Guard(protectedPaths: [models]), logFile: log);

        Assert.False(Directory.Exists(cache));
        Assert.True(File.Exists(weights));
        Assert.Equal("deleted", rule.Items[0].Result);
        Assert.StartsWith("refused: protected", rule.Items[1].Result);
        Assert.Equal(1, res.Problems);
        Assert.True(res.Logged);
        Assert.Contains(File.ReadAllLines(log), l => l.Contains(",TOTAL,", StringComparison.Ordinal));
    }
}
