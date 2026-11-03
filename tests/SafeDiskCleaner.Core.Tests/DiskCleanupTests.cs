using SafeDiskCleaner.Core;

namespace SafeDiskCleaner.Core.Tests;

/// <summary>Disk Cleanup parity: ALLOWED_FILES, the new roots, Windows' handlers, old drivers, admin gating.</summary>
public class DiskCleanupTests
{
    private static readonly string Win = KnownPaths.Windows;

    private static readonly string[] NewRuleIds =
    [
        "temp", "thumbnails", "inet-cache", "shader-cache", "memory-dump",
        "win-logs", "win-diag", "win-update", "old-drivers", "win-old", "win-esd",
    ];

    // ------------------------------------------------------------------ guard

    [Fact]
    public void Allowed_files_match_loose_files_directly_in_the_Windows_folder_only()
    {
        Guard g = Guard.CreateDefault();
        Assert.True(g.IsAllowedFile(Path.Combine(Win, "MEMORY.DMP")));
        Assert.True(g.IsAllowedFile(Path.Combine(Win, "setupact.log")));
        Assert.False(g.IsAllowedFile(Path.Combine(Win, "Minidump", "x.dmp")));
        Assert.False(g.IsAllowedFile(Path.Combine(Win, "System32", "x.dmp")));
        Assert.False(g.IsAllowedFile(Path.Combine(Win, "Logs", "setup.log")));
        Assert.False(g.IsAllowedFile(Path.Combine(Win, "explorer.exe")));
    }

    [Fact]
    public void Guard_refuses_Windows_folders_themselves()
    {
        Guard g = Guard.CreateDefault();
        Assert.Equal(Guard.TopLevel, g.Check(Path.Combine(Win, "Logs")));
        Assert.Equal(Guard.Outside, g.Check(Path.Combine(Win, "System32")));
        Assert.Equal(Guard.Outside, g.Check(Path.Combine(Win, "System32", "LogFiles", "..", "config")));
        Assert.Equal(Guard.Outside, g.Check(Path.Combine(Win, "explorer.exe")));
    }

    [Fact]
    public void Minidump_stays_protected_and_the_DirectX_cache_is_no_longer_protected()
    {
        Guard g = Guard.CreateDefault();
        Assert.Contains(g.ProtectedPaths, p => PathUtil.SamePath(p, Path.Combine(Win, "Minidump")));
        Assert.DoesNotContain(g.ProtectedPaths, p => PathUtil.SamePath(p, Path.Combine(KnownPaths.Local, "D3DSCache")));
        Assert.Contains(g.ProtectedPaths, p => PathUtil.SamePath(p, Path.Combine(KnownPaths.Local, "NVIDIA")));
    }

    [Fact]
    public void Allowed_files_never_open_a_protected_folder_inside_the_same_parent()
    {
        // A miniature C:\Windows built in a temp folder: a loose dump is allowed, the same name in Minidump is not.
        using var t = new TempDir();
        string win = t.Dir("Windows");
        string dump = t.File("x", "Windows", "MEMORY.DMP");
        string mini = t.File("x", "Windows", "Minidump", "110326-01.dmp");
        string exe = t.File("x", "Windows", "explorer.exe");
        var g = new Guard(new GuardConfig
        {
            AllowedRoots = [Path.Combine(win, "Logs")],
            AllowedFiles = [(win, GuardDefaults.WindowsFilePatterns)],
            NeverExact = [win],
            Protected = [Path.Combine(win, "Minidump")],
            InUse = [],
        });
        Assert.Null(g.Check(dump));
        Assert.Equal(Guard.Outside, g.Check(mini));
        Assert.Equal(Guard.Outside, g.Check(Path.Combine(win, "Minidump")));
        Assert.Equal(Guard.Outside, g.Check(exe));
    }

    [Fact]
    public void Every_path_a_rule_finds_goes_through_the_guard()
    {
        // A rule that tries to hand back a protected folder, a top-level folder and an outside path gets none of them.
        using var t = new TempDir();
        string prot = t.Dir("Models");
        t.File("x", "Models", "m.bin");
        string ok = t.Dir("Cache");
        t.File("x", "Cache", "data_0");
        using var other = new TempDir();
        string outside = other.Dir("Elsewhere");
        var rule = new Rule("probe", RuleGroup.Leftovers, "Probe", "Probe",
            (c, r) => [new Item(prot), new Item(t.Root), new Item(outside), new Item(ok)]);

        Scanner.ScanRule(rule, FakeContext(), t.Guard(protectedPaths: [prot]), admin: true);

        Assert.Single(rule.Items);
        Assert.True(PathUtil.SamePath(ok, rule.Items[0].Path!));
        Assert.Equal(3, rule.Skipped.Count);
    }

    [Fact]
    public void New_rules_never_return_a_protected_path_on_this_machine()
    {
        // Read-only: runs each new finder against this PC and checks every item it keeps against
        // the protected list directly (not only through Guard.Check). Nothing is deleted.
        bool admin = Tools.IsAdmin();
        Guard g = Guard.CreateDefault();
        var ctx = new ScanContext(ProcessSnapshot.Take());
        foreach (Rule r in RuleCatalog.Build().Where(r => NewRuleIds.Contains(r.Id)))
        {
            Scanner.ScanRule(r, ctx, g, admin);
            foreach (Item it in r.Items.Where(i => i.Path is not null))
            {
                string p = PathUtil.Norm(it.Path!);
                foreach (string prot in g.ProtectedPaths)
                {
                    Assert.False(PathUtil.Under(p, prot) || PathUtil.Under(prot, p), $"{r.Id}: {p} touches protected {prot}");
                }
                Assert.False(PathUtil.Under(p, Path.Combine(Win, "Minidump")), $"{r.Id}: {p} is in Minidump");
            }
            Assert.DoesNotContain(r.Skipped, s => s.Why.StartsWith("scan error", StringComparison.Ordinal));
            Console.WriteLine($"{r.Id}: {r.Items.Count} items, {SizeFormat.Format(r.Total)}, {r.Skipped.Count} left alone" +
                string.Concat(r.Skipped.Take(3).Select(s => $" | {s.What}: {s.Why}")));
        }
    }

    // ------------------------------------------------------------------ admin gating and defaults

    [Fact]
    public void Admin_rules_do_nothing_when_not_elevated()
    {
        bool called = false;
        var probe = new Rule("probe-admin", RuleGroup.Windows, "Probe", "Probe", (c, r) =>
        {
            called = true;
            return [];
        }, admin: true);
        Scanner.ScanRule(probe, FakeContext(), Guard.CreateDefault(), admin: false);
        Assert.False(called);
        Assert.Equal(Scanner.NeedsAdmin, probe.Blocked);
        Assert.False(probe.CanClean);

        foreach (Rule r in RuleCatalog.Build().Where(r => r.NeedsAdmin))
        {
            Scanner.ScanRule(r, FakeContext(), Guard.CreateDefault(), admin: false);
            Assert.Empty(r.Items);
            Assert.Equal(Scanner.NeedsAdmin, r.Blocked);
            Assert.Contains(r.Skipped, s => s.Why == Scanner.NeedsAdmin);
        }
    }

    [Fact]
    public void Clean_skips_admin_rules_when_not_elevated_and_touches_nothing()
    {
        using var t = new TempDir();
        string cache = t.Dir("Cache");
        string f = t.File("x", "Cache", "data_0");
        var rule = new Rule("probe-admin", RuleGroup.Windows, "Probe", "Probe", (c, r) => [], admin: true);
        rule.Items.Add(new Item(cache, size: 1));
        rule.Items.Add(Item.ForHandler("Update files (Update Cleanup)", "Update Cleanup"));
        var keys = new FakeVolumeCaches("Update Cleanup");
        bool ran = false;

        CleanResult res = Cleaner.Clean([rule], guard: t.Guard(), logFile: Path.Combine(t.Root, "runs.csv"),
            handlers: new DiskCleanupHandlers(keys, () => ran = true), admin: false);

        Assert.True(File.Exists(f));
        Assert.False(ran);
        Assert.All(rule.Items, i => Assert.Equal("skipped: needs admin", i.Result));
        Assert.Equal(2, res.Problems);
        Assert.Empty(keys.Flags);
    }

    [Fact]
    public void Risky_rules_are_off_by_default()
    {
        List<Rule> rules = RuleCatalog.Build();
        foreach (string id in new[] { "old-drivers", "memory-dump", "shader-cache", "win-old", "win-esd", "recycle" })
        {
            Rule r = rules.Single(x => x.Id == id);
            Assert.False(r.DefaultChecked, id);
        }
        Assert.Equal(RuleGroup.YourCall, rules.Single(r => r.Id == "memory-dump").Group);
        Assert.Equal(RuleGroup.YourCall, rules.Single(r => r.Id == "shader-cache").Group);
        Assert.True(rules.Single(r => r.Id == "memory-dump").NeedsAdmin);
        Assert.True(rules.Single(r => r.Id == "old-drivers").NeedsAdmin);
        Assert.True(rules.Single(r => r.Id == "win-update").DefaultChecked);
        Assert.DoesNotContain(rules, r => r.Id == "dism");
    }

    // ------------------------------------------------------------------ Windows' own handlers

    [Fact]
    public void Handler_run_clears_stale_flags_first_sets_only_ticked_ones_and_removes_them_after()
    {
        var keys = new FakeVolumeCaches("Update Cleanup", "Language Pack", "DownloadsFolder", "Previous Installations");
        keys.Flags["DownloadsFolder"] = 2; // left behind by a crashed earlier run
        Dictionary<string, int>? during = null;
        var runner = new DiskCleanupHandlers(keys, () => during = new Dictionary<string, int>(keys.Flags));

        IReadOnlyList<string> ran = runner.Run(["Update Cleanup", "Language Pack", "Not On This PC"]);

        Assert.Equal(["Update Cleanup", "Language Pack"], ran);
        Assert.NotNull(during);
        Assert.Equal(new Dictionary<string, int> { ["Update Cleanup"] = 2, ["Language Pack"] = 2 }, during);
        Assert.Empty(keys.Flags);
        Assert.Equal("StateFlags0777", DiskCleanupHandlers.FlagName);
        Assert.All(keys.ValueNames, n => Assert.Equal("StateFlags0777", n));
    }

    [Fact]
    public void Handler_flags_are_removed_even_when_Disk_Cleanup_fails()
    {
        var keys = new FakeVolumeCaches("Update Cleanup");
        var runner = new DiskCleanupHandlers(keys, () => throw new InvalidOperationException("cleanmgr crashed"));
        Assert.Throws<InvalidOperationException>(() => runner.Run(["Update Cleanup"]));
        Assert.Empty(keys.Flags);
    }

    [Fact]
    public void Clean_runs_all_ticked_handlers_in_one_pass_at_the_end()
    {
        using var t = new TempDir();
        var keys = new FakeVolumeCaches("Update Cleanup", "Previous Installations");
        int runs = 0;
        Dictionary<string, int>? during = null;
        var handlers = new DiskCleanupHandlers(keys, () =>
        {
            runs++;
            during = new Dictionary<string, int>(keys.Flags);
        });
        var update = new Rule("win-update", RuleGroup.Windows, "Update", "Update", (c, r) => [], admin: true);
        update.Items.Add(Item.ForHandler("Superseded Windows Update files (Update Cleanup)", "Update Cleanup"));
        var old = new Rule("win-old", RuleGroup.Windows, "Old", "Old", (c, r) => [], admin: true);
        old.Items.Add(Item.ForHandler("Windows.old", "Previous Installations", 1000));

        CleanResult res = Cleaner.Clean([update, old], guard: t.Guard(), logFile: Path.Combine(t.Root, "runs.csv"),
            handlers: handlers, admin: true);

        Assert.Equal(1, runs);
        Assert.Equal(2, during!.Count);
        Assert.Empty(keys.Flags);
        Assert.Equal("done by Windows", update.Items[0].Result);
        Assert.Equal("done by Windows", old.Items[0].Result);
        Assert.Equal(0, res.Problems);
    }

    [Fact]
    public void Handler_rows_appear_only_for_categories_this_PC_has()
    {
        var ctx = FakeContext(new FakeVolumeCaches("Update Cleanup", "Language Pack"));
        Rule r = RuleCatalog.Build().Single(x => x.Id == "win-update");
        Scanner.ScanRule(r, ctx, Guard.CreateDefault(), admin: true);
        Assert.Equal(["Update Cleanup", "Language Pack"], r.Items.Select(i => i.Handler));
        Assert.All(r.Items, i => Assert.Null(i.Size));
        Assert.True(r.CanClean);
    }

    // ------------------------------------------------------------------ old drivers

    [Fact]
    public void Driver_grouping_keeps_the_newest_and_ties_and_skips_in_use()
    {
        DriverPackage P(string oem, string inf, string provider, string ver) => new(oem, inf, provider, VersionKey.Parse(ver));
        var pkgs = new[]
        {
            P("oem10.inf", "netwtw10.inf", "Intel", "22.200.0.6"),
            P("oem11.inf", "netwtw10.inf", "Intel", "22.250.1.2"),
            P("oem12.inf", "NETWTW10.INF", "Intel", "23.10.0.1"),   // newest
            P("oem13.inf", "netwtw10.inf", "Intel", "23.10.0.1"),   // ties the newest: stays
            P("oem14.inf", "netwtw10.inf", "Intel", "21.0.0.1"),    // older but a device uses it
            P("oem20.inf", "netwtw10.inf", "Contoso", "1.0.0.0"),   // same INF, other provider: own group
            P("oem30.inf", "rt640x64.inf", "Realtek", "10.50.0.0"),
            P("oem31.inf", "rt640x64.inf", "Realtek", "10.68.0.0"),
        };
        var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OEM14.INF", "oem31.inf" };

        var (old, skipped) = DriverCleanup.SelectOld(pkgs, inUse);

        Assert.Equal(["oem10.inf", "oem11.inf", "oem30.inf"], old.Select(o => o.Package.PublishedName).Order());
        Assert.All(old.Where(o => o.Package.OriginalName.StartsWith("net", StringComparison.OrdinalIgnoreCase)),
            o => Assert.Equal("23.10.0.1", o.Newest.ToString()));
        Assert.Equal(["oem14.inf"], skipped.Select(s => s.Package.PublishedName));
    }

    [Fact]
    public void Driver_delete_refuses_anything_but_an_oem_inf_name()
    {
        Assert.Throws<ArgumentException>(() => DriverCleanup.Delete("netwtw10.inf"));
        Assert.Throws<ArgumentException>(() => DriverCleanup.Delete("oem1.inf /force"));
        Assert.Throws<ArgumentException>(() => DriverCleanup.Delete(@"..\oem1.inf"));
    }

    [Fact]
    public void Old_driver_rule_lists_through_the_context_and_reports_an_empty_list()
    {
        var ctx = FakeContext(listDrivers: () => []);
        Rule r = RuleCatalog.Build().Single(x => x.Id == "old-drivers");
        Scanner.ScanRule(r, ctx, Guard.CreateDefault(), admin: true);
        Assert.Empty(r.Items);
        Assert.Contains(r.Skipped, s => s.What == "driver list");
    }

    // ------------------------------------------------------------------ helpers

    private static ScanContext FakeContext(FakeVolumeCaches? keys = null, Func<List<DriverPackage>>? listDrivers = null) =>
        new(new ProcessSnapshot([]), () => null, new DiskCleanupHandlers(keys ?? new FakeVolumeCaches(), () => { }),
            listDrivers ?? (() => []));

    /// <summary>A fake VolumeCaches key set. The real registry is never touched by the tests.</summary>
    private sealed class FakeVolumeCaches(params string[] categories) : IVolumeCaches
    {
        public Dictionary<string, int> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ValueNames { get; } = [];

        public IReadOnlyList<string> Categories() => categories;

        public bool Exists(string category) => categories.Contains(category, StringComparer.OrdinalIgnoreCase);

        public void SetFlag(string category, string valueName, int value)
        {
            ValueNames.Add(valueName);
            Flags[category] = value;
        }

        public void DeleteFlag(string category, string valueName)
        {
            ValueNames.Add(valueName);
            Flags.Remove(category);
        }
    }
}
