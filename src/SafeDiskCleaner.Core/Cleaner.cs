namespace SafeDiskCleaner.Core;

public sealed record CleanProgress(int Done, int Total, string Current);

public sealed class CleanResult
{
    /// <summary>Change in free space on the drives involved, measured, never summed from estimates.</summary>
    public long Freed { get; init; }

    public int Items { get; init; }

    /// <summary>Items refused by the guard, skipped (app open), failed, or only partly removed.</summary>
    public int Problems { get; init; }

    public bool Logged { get; init; }
}

/// <summary>clean() from the reference.</summary>
public static class Cleaner
{
    /// <summary>
    /// Deletes the items of the given rules. Every path is checked by the guard again right before
    /// it is deleted, with a guard rebuilt now (so a program installed since the scan still counts),
    /// and a rule whose app has been opened since the scan is skipped.
    /// </summary>
    public static CleanResult Clean(IReadOnlyList<Rule> rules, IProgress<CleanProgress>? progress = null,
        bool dryRun = false, Guard? guard = null, string? logFile = null)
    {
        guard ??= Guard.CreateDefault();
        ProcessSnapshot processes = ProcessSnapshot.Take();
        var work = rules.Where(r => r.CanClean).SelectMany(r => r.Items.Select(it => (Rule: r, Item: it))).ToList();
        var busy = rules.ToDictionary(r => r, r => r.Busy?.Invoke(processes));

        List<string> drives = DrivesToMeasure(work.Select(w => w.Item));
        Dictionary<string, long?> before = drives.ToDictionary(d => d, Tools.FreeBytes);

        var rows = new List<LogRow>();
        int problems = 0;
        for (int n = 0; n < work.Count; n++)
        {
            (Rule r, Item it) = work[n];
            progress?.Report(new CleanProgress(n, work.Count, it.Label));
            if (dryRun)
            {
                it.Result = "dry run";
            }
            else if (busy[r] is string why)
            {
                it.Result = $"skipped: {why}";
            }
            else if (it.Action is not null)
            {
                try
                {
                    it.Action();
                    it.Result = "done";
                }
                catch (Exception ex)
                {
                    it.Result = $"failed: {ex.Message}";
                }
            }
            else
            {
                it.Result = DeletePath(it.Path!, guard);
            }
            if (it.Result is not ("deleted" or "done" or "dry run"))
            {
                problems++;
            }
            rows.Add(new LogRow(DateTime.Now, r.Id, it.Path ?? it.Label, it.Size ?? 0, it.Result));
        }
        progress?.Report(new CleanProgress(work.Count, work.Count, ""));

        long freed = 0;
        foreach (string d in drives)
        {
            if (before[d] is long b && Tools.FreeBytes(d) is long a)
            {
                freed += a - b;
            }
        }

        bool logged = false;
        if (!dryRun && rows.Count > 0)
        {
            try
            {
                RunLog.Append(rows, freed, DateTime.Now, logFile);
                logged = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RunLog.WriteError("Could not write runs.csv: " + ex.Message);
            }
        }
        return new CleanResult { Freed = freed, Items = work.Count, Problems = problems, Logged = logged };
    }

    /// <summary>Re-checks with the guard, deletes, and describes what happened.</summary>
    public static string DeletePath(string path, Guard guard)
    {
        if (guard.Check(path) is string why)
        {
            return $"refused: {why}";
        }
        DeleteReport rep;
        try
        {
            rep = SafeDelete.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"failed: {ex.Message}";
        }
        if (!PathUtil.Exists(path))
        {
            return "deleted";
        }
        if (rep.PlaceholdersKept > 0)
        {
            return $"partly kept: {rep.PlaceholdersKept} cloud files left in place";
        }
        if (rep.SpecialKept > 0)
        {
            return $"partly kept: {rep.SpecialKept} special files left in place";
        }
        long left = PathUtil.TreeSize(path);
        return left > 0 ? $"partly in use, {SizeFormat.Format(left)} left" : "deleted";
    }

    private static List<string> DrivesToMeasure(IEnumerable<Item> items)
    {
        var drives = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { KnownPaths.SystemDrive };
        bool anyAction = false;
        foreach (Item it in items)
        {
            if (it.Path is not null && Path.GetPathRoot(it.Path) is { Length: > 0 } root)
            {
                drives.Add(root);
            }
            anyAction |= it.Action is not null;
        }
        if (anyAction)
        {
            // the Recycle Bin spans every drive
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady && d.DriveType == DriveType.Fixed)
                    {
                        drives.Add(d.RootDirectory.FullName);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
        return drives.ToList();
    }
}
