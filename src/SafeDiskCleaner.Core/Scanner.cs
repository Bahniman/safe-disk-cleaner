namespace SafeDiskCleaner.Core;

public sealed record ScanProgress(int Done, int Total, string Current);

public sealed class ScanResult
{
    public required List<Rule> Rules { get; init; }

    public required Guard Guard { get; init; }

    public required bool Admin { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public long Total => Rules.Sum(r => r.Total);
}

/// <summary>scan() from the reference. Read-only: it finds, checks and sizes, it never deletes.</summary>
public static class Scanner
{
    public const string NeedsAdmin = "needs admin";

    /// <param name="createProtectFile">Create protect.txt with its header if missing (the app does; tests do not).</param>
    public static ScanResult Scan(bool? admin = null, IProgress<ScanProgress>? progress = null,
        bool createProtectFile = true, CancellationToken ct = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool isAdmin = admin ?? Tools.IsAdmin();
        if (createProtectFile)
        {
            UserProtection.EnsureFile();
        }
        var ctx = new ScanContext(ProcessSnapshot.Take());
        Guard guard = Guard.CreateDefault();
        List<Rule> rules = RuleCatalog.Build();

        int done = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            CancellationToken = ct,
        };
        Parallel.ForEach(rules, options, r =>
        {
            ScanRule(r, ctx, guard, isAdmin);
            progress?.Report(new ScanProgress(Interlocked.Increment(ref done), rules.Count, r.Title));
        });

        RemoveDuplicates(rules);
        return new ScanResult { Rules = rules, Guard = guard, Admin = isAdmin, Elapsed = watch.Elapsed };
    }

    /// <summary>Finds a rule's items, runs each path through the guard, and sizes what is left.</summary>
    public static void ScanRule(Rule r, ScanContext ctx, Guard guard, bool admin)
    {
        if (r.NeedsAdmin && !admin)
        {
            r.Blocked = NeedsAdmin;
            r.Skipped.Add(new Skip(r.Title, NeedsAdmin));
            return;
        }
        r.Blocked = r.Busy?.Invoke(ctx.Processes);

        List<Item> found;
        try
        {
            found = r.Find(ctx, r).ToList();
        }
        catch (Exception ex)
        {
            r.Blocked = "scan error";
            r.Skipped.Add(new Skip(r.Title, $"scan error: {ex.Message}"));
            return;
        }

        foreach (Item it in found)
        {
            if (it.Path is not null && guard.Check(it.Path) is string why)
            {
                r.Skipped.Add(new Skip(it.Path, why));
                continue;
            }
            r.Items.Add(it);
        }

        Parallel.ForEach(r.Items.Where(i => i.Path is not null && i.Size is null),
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            it => it.Size = PathUtil.TreeSize(it.Path!));
        // path-less items (actions, Windows' handlers) stay even when their size is unknown
        r.Items.RemoveAll(it => it.Path is not null && (it.Size ?? 0) == 0);
    }

    /// <summary>A folder found by two rules is kept by the first one only, so totals are not counted twice.</summary>
    private static void RemoveDuplicates(List<Rule> rules)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Rule r in rules)
        {
            r.Items.RemoveAll(it => it.Path is not null && PathUtil.TryNorm(it.Path) is string p && !seen.Add(p));
        }
    }
}
