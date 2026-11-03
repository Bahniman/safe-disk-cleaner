using SafeDiskCleaner.Core;

namespace SafeDiskCleaner.Core.Tests;

/// <summary>The guard cases from docs/SPEC.md, built from temp folders the tests create.</summary>
public class GuardTests
{
    [Fact]
    public void Allows_a_plain_cache_folder()
    {
        using var t = new TempDir();
        t.File("x", "App", "Cache", "data_0");
        Assert.Null(t.Guard().Check(Path.Combine(t.Root, "App", "Cache")));
    }

    [Fact]
    public void Refuses_a_missing_path()
    {
        using var t = new TempDir();
        Assert.Equal(Guard.GoneAlready, t.Guard().Check(Path.Combine(t.Root, "nope")));
    }

    [Fact]
    public void Refuses_top_level_folders()
    {
        using var t = new TempDir();
        Assert.Equal(Guard.TopLevel, t.Guard().Check(t.Root));
        Assert.Equal(Guard.TopLevel, t.Guard().Check(t.Root + "\\"));
    }

    [Fact]
    public void Default_guard_refuses_real_top_level_folders()
    {
        // Read-only checks against the real profile; nothing is created or deleted.
        Guard g = Guard.CreateDefault();
        Assert.Equal(Guard.TopLevel, g.Check(KnownPaths.Home));
        Assert.Equal(Guard.TopLevel, g.Check(KnownPaths.Local));
        Assert.Equal(Guard.TopLevel, g.Check(KnownPaths.Roaming));
        Assert.Equal(Guard.TopLevel, g.Check(KnownPaths.Windows));
        Assert.Equal(Guard.TopLevel, g.Check(KnownPaths.SystemDrive));
    }

    [Fact]
    public void Refuses_anything_outside_the_allowed_roots()
    {
        using var t = new TempDir();
        using var other = new TempDir();
        string outside = other.Dir("Cache");
        Assert.Equal(Guard.Outside, t.Guard().Check(outside));
        Assert.Equal(Guard.Outside, Guard.CreateDefault().Check(Path.Combine(KnownPaths.Windows, "System32")));
    }

    [Fact]
    public void Refuses_anything_inside_or_containing_a_protected_path()
    {
        using var t = new TempDir();
        string models = t.Dir("Models");
        t.File("weights", "Models", "llama", "model.bin");
        t.Dir("Outer", "Keep");
        Guard g = t.Guard(protectedPaths: [models, Path.Combine(t.Root, "Outer", "Keep")]);

        Assert.StartsWith("protected", g.Check(Path.Combine(models, "llama")));
        Assert.StartsWith("protected", g.Check(models));
        Assert.StartsWith("protected", g.Check(Path.Combine(t.Root, "Outer")));
    }

    [Fact]
    public void Refuses_a_path_reached_through_a_junction()
    {
        using var t = new TempDir();
        string real = t.Dir("Real");
        t.File("x", "Real", "Cache", "index");
        string link = Path.Combine(t.Root, "Link");
        Links.Junction(link, real);
        Guard g = t.Guard();

        Assert.Equal(Guard.ThroughLink, g.Check(Path.Combine(link, "Cache")));
        Assert.Equal(Guard.ThroughLink, g.Check(link));
        Assert.Null(g.Check(Path.Combine(real, "Cache")));
    }

    [Fact]
    public void Refuses_a_folder_that_holds_a_PATH_entry()
    {
        using var t = new TempDir();
        string bin = t.Dir("Tools", "ffmpeg", "bin");
        t.File("exe", "Tools", "ffmpeg", "bin", "ffmpeg.exe");
        t.Dir("Other");
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", oldPath + ";" + bin);
            Guard g = t.Guard(inUse: GuardSources.PathDirs());

            Assert.StartsWith("holds an installed program or PATH entry", g.Check(Path.Combine(t.Root, "Tools")));
            Assert.StartsWith("holds an installed program or PATH entry", g.Check(Path.Combine(t.Root, "Tools", "ffmpeg")));
            Assert.StartsWith("holds an installed program or PATH entry", g.Check(bin));
            Assert.Null(g.Check(Path.Combine(t.Root, "Other")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    [Fact]
    public void Refuses_a_folder_that_holds_a_registered_install_location()
    {
        // Same check as PATH; an install location from the registry lands in the same list.
        using var t = new TempDir();
        string app = t.Dir("Packages", "Vendor.Tool_abc", "app");
        Guard g = t.Guard(inUse: [app]);
        Assert.StartsWith("holds an installed program", g.Check(Path.Combine(t.Root, "Packages")));
        Assert.StartsWith("holds an installed program", g.Check(app));
    }

    [SkippableFact]
    public void Refuses_a_folder_that_holds_a_WinGet_link_target()
    {
        using var t = new TempDir();
        string exe = t.File("exe", "Packages", "Tool_1.0", "tool.exe");
        string links = t.Dir("Links");
        Links.SymlinkOrSkip(() => File.CreateSymbolicLink(Path.Combine(links, "tool.exe"), exe));

        List<string> targets = GuardSources.LinkTargets(links);
        Assert.Contains(targets, p => PathUtil.SamePath(p, Path.GetDirectoryName(exe)!));
        Guard g = t.Guard(inUse: targets);
        Assert.StartsWith("holds an installed program", g.Check(Path.Combine(t.Root, "Packages")));
    }

    [Fact]
    public void Default_guard_protects_WinGet_and_Programs()
    {
        Guard g = Guard.CreateDefault();
        Assert.Contains(g.ProtectedPaths, p => PathUtil.SamePath(p, Path.Combine(KnownPaths.Local, "Microsoft", "WinGet")));
        Assert.Contains(g.ProtectedPaths, p => PathUtil.SamePath(p, Path.Combine(KnownPaths.Local, "Programs")));
        Assert.Contains(g.ProtectedPaths, p => PathUtil.SamePath(p, Path.Combine(KnownPaths.Windows, "Installer")));
    }

    [Fact]
    public void Default_sources_read_without_throwing()
    {
        Assert.NotNull(GuardSources.InstallLocations());
        Assert.NotEmpty(GuardSources.PathDirs());
        Assert.NotNull(GuardSources.WinGetLinkTargets());
    }

    [Fact]
    public void User_protected_folders_from_protect_txt_are_honoured()
    {
        using var t = new TempDir();
        string keep = t.Dir("Mods", "Cache");
        string file = Path.Combine(t.Root, "protect.txt");
        UserProtection.EnsureFile(file);
        UserProtection.Add(keep, file);
        UserProtection.Add(keep, file); // second add is a no-op

        List<string> listed = UserProtection.Read(file);
        Assert.Single(listed);
        Guard g = t.Guard(protectedPaths: listed);
        Assert.StartsWith("protected", g.Check(keep));
        Assert.StartsWith("protected", g.Check(Path.Combine(t.Root, "Mods")));
    }

    [SkippableFact]
    public void Short_8dot3_names_compare_equal_to_long_names()
    {
        string temp = Path.GetTempPath();
        Xunit.Skip.IfNot(temp.Contains('~'), "This machine's %TEMP% has no 8.3 name to test with.");
        Assert.DoesNotContain("~", PathUtil.Norm(temp));
    }
}
