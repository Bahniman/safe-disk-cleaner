namespace SafeDiskCleaner.Core;

/// <summary>The cards in the window. Reference tiers: safe, apps, ask, admin.</summary>
public enum RuleGroup
{
    Leftovers,
    Apps,
    YourCall,
    Windows,
}

public static class RuleGroups
{
    public static readonly RuleGroup[] Order = [RuleGroup.Leftovers, RuleGroup.Apps, RuleGroup.YourCall, RuleGroup.Windows];

    public static string Title(RuleGroup g) => g switch
    {
        RuleGroup.Leftovers => "Leftovers",
        RuleGroup.Apps => "App and browser caches",
        RuleGroup.YourCall => "Your call",
        RuleGroup.Windows => "Windows",
        _ => g.ToString(),
    };

    public static string Blurb(RuleGroup g, bool admin) => g switch
    {
        RuleGroup.Leftovers => "Nothing uses these any more",
        RuleGroup.Apps => "Logins and settings stay. An open app is skipped.",
        RuleGroup.YourCall => "Unchecked until you tick them",
        RuleGroup.Windows => admin ? "Windows' own leftovers" : "Restart as admin to scan these",
        _ => "",
    };
}

/// <summary>One thing to delete: a path (checked by the guard) or an action such as emptying the Recycle Bin.</summary>
public sealed class Item
{
    public Item(string path, string? label = null, long? size = null)
    {
        Path = path;
        Label = label ?? path;
        Size = size;
    }

    public Item(string label, Action action, long? size)
    {
        Label = label;
        Action = action;
        Size = size;
    }

    public string? Path { get; }

    public string Label { get; }

    /// <summary>Set for items that are not plain paths (Recycle Bin, Delivery Optimization, DISM).</summary>
    public Action? Action { get; }

    /// <summary>Bytes, or null when unknown (DISM).</summary>
    public long? Size { get; set; }

    /// <summary>What happened when cleaning: "deleted", "refused: ...", "partly in use, ..." and so on.</summary>
    public string? Result { get; set; }
}

/// <summary>Something the rule found but will not delete, with the reason (from the guard or the rule).</summary>
public sealed record Skip(string What, string Why);

public delegate IEnumerable<Item> Finder(ScanContext ctx, Rule rule);

public sealed class Rule
{
    public Rule(string id, RuleGroup group, string title, string why, Finder find, bool? isChecked = null, bool admin = false)
    {
        Id = id;
        Group = group;
        Title = title;
        Why = why;
        Find = find;
        DefaultChecked = isChecked ?? group != RuleGroup.YourCall;
        NeedsAdmin = admin;
    }

    public string Id { get; }

    public RuleGroup Group { get; }

    public string Title { get; }

    public string Why { get; }

    public Finder Find { get; }

    public bool DefaultChecked { get; }

    public bool NeedsAdmin { get; }

    /// <summary>For rules tied to an app: returns "X is open, close it to clear this" while it runs.</summary>
    public Func<ProcessSnapshot, string?>? Busy { get; init; }

    public List<Item> Items { get; } = [];

    public List<Skip> Skipped { get; } = [];

    /// <summary>Why the whole rule cannot be cleaned right now (app open, needs admin, scan error), else null.</summary>
    public string? Blocked { get; set; }

    public long Total => Items.Sum(i => i.Size ?? 0);

    public bool HasUnknownSize => Items.Any(i => i.Size is null);

    public bool CanClean => Blocked is null && Items.Count > 0;

    /// <summary>Worth a row in the window: it found something, or has a reason to show.</summary>
    public bool Visible => Items.Count > 0 || Skipped.Count > 0;

    public override string ToString() => $"{Id} ({Items.Count} items, {SizeFormat.Format(Total)})";
}

/// <summary>What each finder may consult: running processes and, lazily, the installed NVIDIA driver.</summary>
public sealed class ScanContext
{
    private readonly Lazy<VersionKey?> _nvidiaDriver;

    public ScanContext(ProcessSnapshot processes, Func<VersionKey?>? nvidiaDriver = null)
    {
        Processes = processes;
        _nvidiaDriver = new Lazy<VersionKey?>(nvidiaDriver ?? Tools.NvidiaDriverVersion, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ProcessSnapshot Processes { get; }

    public VersionKey? NvidiaDriver => _nvidiaDriver.Value;

    public bool Running(IEnumerable<string>? names = null, IEnumerable<string>? dirs = null) => Processes.Running(names, dirs);
}
