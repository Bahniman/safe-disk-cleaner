namespace SafeDiskCleaner.Core;

/// <summary>
/// Chromium/Electron cache folders, recognised by what is inside them (_is_chromium_cache and
/// _cache_dirs in the reference), plus the APPS and LAUNCHERS tables.
/// </summary>
public static class AppCaches
{
    private static readonly HashSet<string> GpuCacheNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpucache", "dawncache", "dawngraphitecache", "dawnwebgpucache", "grshadercache", "shadercache",
    };

    /// <summary>An app whose web and GPU caches are cleared (label, user-data roots, process names, install dirs).</summary>
    public sealed record App(string Label, string[] Roots, string[] Names, string[] Dirs);

    /// <summary>A launcher whose whole cache folder is cleared (label, cache folders, process names).</summary>
    public sealed record Launcher(string Label, string[] Paths, string[] Names);

    public static readonly App[] Apps =
    [
        new("Claude app", [@"{L}\Claude-Data\*", @"{R}\Claude"], ["claude.exe"], []),
        new("Chrome", [@"{L}\Google\Chrome\User Data"], ["chrome.exe"], []),
        new("Chrome Beta", [@"{L}\Google\Chrome Beta\User Data"], ["chrome.exe"], []),
        new("Edge", [@"{L}\Microsoft\Edge\User Data"], ["msedge.exe"], []),
        new("Gemini app", [@"{R}\gemini"], ["gemini.exe", "geminiapplauncher.exe"], []),
        new("Notion", [@"{R}\Notion"], ["notion.exe"], [@"{L}\Programs\Notion"]),
        new("Discord", [@"{R}\discord"], ["discord.exe"], [@"{L}\Discord"]),
        new("Antigravity", [@"{R}\Antigravity"], ["antigravity.exe"], [@"{L}\Programs\antigravity"]),
        new("VS Code", [@"{R}\Code"], ["code.exe"], [@"{L}\Programs\Microsoft VS Code"]),
        new("Obsidian", [@"{R}\obsidian"], ["obsidian.exe"], [@"{L}\Programs\Obsidian"]),
        new("Wand", [@"{R}\WeMod", @"{R}\Wand"], ["wand.exe", "wemod.exe"], [@"{L}\Wand"]),
        new("Codex app", [@"{L}\Packages\OpenAI.Codex_*\LocalCache\Roaming\Codex", @"{R}\Codex"], ["codex.exe"], []),
        new("Comfy Desktop", [@"{R}\Comfy Desktop"], [], [@"{L}\Programs\Comfy Desktop"]),
        new("Open Design", [@"{R}\Open Design"], [], [@"{L}\Programs\Open Design"]),
        new("Power BI", [@"{L}\Microsoft\Power BI Desktop\WebView2"], ["pbidesktop.exe"], []),

        // Added in the C# port (spec: "if cheap and clearly safe"). Same signature check, so only
        // folders that look like a Chromium HTTP/code/GPU cache are ever picked, never settings,
        // logins or downloads. Chromium recreates these on the next start:
        // https://www.chromium.org/developers/design-documents/network-stack/disk-cache/
        new("Brave", [@"{L}\BraveSoftware\Brave-Browser\User Data"], ["brave.exe"], []),
        new("Opera", [@"{R}\Opera Software\Opera Stable", @"{L}\Opera Software\Opera Stable",
                      @"{R}\Opera Software\Opera GX Stable", @"{L}\Opera Software\Opera GX Stable"], ["opera.exe"], []),
        new("Slack", [@"{R}\Slack", @"{L}\Packages\91750D7E.Slack_*\LocalCache\Roaming\Slack"], ["slack.exe"], [@"{L}\slack"]),
        // New Teams keeps its WebView2 data here; Microsoft documents clearing it:
        // https://learn.microsoft.com/troubleshoot/microsoftteams/teams-administration/clear-teams-cache
        new("Teams", [@"{L}\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams"], ["ms-teams.exe"], []),
        // Only the embedded browser caches. Spotify's song cache and offline downloads live in
        // other folders that never match the signature.
        new("Spotify", [@"{R}\Spotify", @"{L}\Spotify", @"{L}\Packages\SpotifyAB.SpotifyMusic_*\LocalCache\Spotify"], ["spotify.exe"], []),
        new("Cursor", [@"{R}\Cursor"], ["cursor.exe"], [@"{L}\Programs\cursor"]),
        new("Windsurf", [@"{R}\Windsurf"], ["windsurf.exe"], [@"{L}\Programs\Windsurf"]),
        // Firefox keeps its disk cache (cache2) in the Local profile folder, apart from personal
        // data in Roaming, and rebuilds it on demand:
        // https://support.mozilla.org/kb/profiles-where-firefox-stores-user-data
        // https://support.mozilla.org/kb/how-clear-firefox-cache
        new("Firefox", [@"{L}\Mozilla\Firefox\Profiles", @"{L}\Packages\Mozilla.Firefox_*\LocalCache\Local\Mozilla\Firefox\Profiles"], ["firefox.exe"], []),
    ];

    public static readonly Launcher[] Launchers =
    [
        new("Steam web cache", [@"{L}\Steam\htmlcache"], ["steam.exe", "steamwebhelper.exe"]),
        new("Ubisoft Connect cache", [@"{L}\Ubisoft Game Launcher\cache"], ["upc.exe", "ubisoftconnect.exe", "uplaywebcore.exe"]),
        new("Epic launcher web cache", [@"{L}\EpicGamesLauncher\Saved\webcache*"], ["epicgameslauncher.exe"]),
        new("Zoom installer downloads", [@"{R}\Zoom\ZoomDownload"], ["zoom.exe"]),
    ];

    /// <summary>Is this folder a browser cache, judged by its name and what is directly inside it?</summary>
    public static bool IsChromiumCache(string dir)
    {
        string n = Path.GetFileName(dir).ToLowerInvariant();
        HashSet<string> kids;
        try
        {
            kids = new HashSet<string>(
                Directory.EnumerateFileSystemEntries(dir, "*", PathUtil.AllOptions).Select(Path.GetFileName)!,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return n switch
        {
            "cache" => kids.Contains("cache_data") || kids.Contains("index"),
            "code cache" => kids.Contains("js") || kids.Contains("wasm") || kids.Contains("webui_js"),
            // Firefox's disk cache (added in the C# port)
            "cache2" => kids.Contains("entries"),
            _ when GpuCacheNames.Contains(n) => kids.Contains("index") || kids.Contains("data_0"),
            _ => false,
        };
    }

    /// <summary>Cache folders under an app's data root, up to <paramref name="depth"/> levels down. Never follows links.</summary>
    public static List<string> CacheDirs(string root, int depth = 5)
    {
        var found = new List<string>();
        var stack = new Stack<(string Dir, int Level)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            (string d, int lvl) = stack.Pop();
            List<DirectoryInfo> entries;
            try
            {
                entries = new DirectoryInfo(d).EnumerateDirectories("*", PathUtil.DirOptions).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (DirectoryInfo e in entries)
            {
                string low = e.Name.ToLowerInvariant();
                if (low == "optguideondevicemodel")
                {
                    continue;
                }
                if (IsChromiumCache(e.FullName))
                {
                    found.Add(e.FullName);
                }
                else if (low == "service worker")
                {
                    foreach (string sub in new[] { "CacheStorage", "ScriptCache" })
                    {
                        string p = Path.Combine(e.FullName, sub);
                        if (Directory.Exists(p))
                        {
                            found.Add(p);
                        }
                    }
                }
                else if (lvl < depth)
                {
                    stack.Push((e.FullName, lvl + 1));
                }
            }
        }
        return found;
    }

    internal static Rule AppRule(App app)
    {
        string[] dirs = app.Dirs.Select(KnownPaths.Expand).ToArray();
        string busy = $"{app.Label} is open, close it to clear this";
        return new Rule("cache-" + app.Label.ToLowerInvariant().Replace(' ', '-'), RuleGroup.Apps, app.Label,
            "Web and GPU cache folders", (ctx, r) => FindAppCaches(app))
        {
            Busy = procs => procs.Running(app.Names, dirs) ? busy : null,
        };
    }

    private static IEnumerable<Item> FindAppCaches(App app)
    {
        foreach (string pat in app.Roots)
        {
            foreach (string root in PathUtil.Glob(KnownPaths.Expand(pat)))
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }
                foreach (string d in CacheDirs(root))
                {
                    yield return new Item(d);
                }
            }
        }
    }

    internal static Rule LauncherRule(Launcher l)
    {
        string first = l.Label.Split(' ')[0];
        return new Rule("launcher-" + first.ToLowerInvariant(), RuleGroup.Apps, l.Label,
            "Launcher cache, rebuilt on next start", (ctx, r) => FindLauncherCaches(l))
        {
            Busy = procs => procs.Running(l.Names) ? $"{first} is open, close it to clear this" : null,
        };
    }

    private static IEnumerable<Item> FindLauncherCaches(Launcher l)
    {
        foreach (string pat in l.Paths)
        {
            foreach (string p in PathUtil.Glob(KnownPaths.Expand(pat)))
            {
                if (Directory.Exists(p))
                {
                    yield return new Item(p);
                }
            }
        }
    }
}
