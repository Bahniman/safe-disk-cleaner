using SafeDiskCleaner.Core;

namespace SafeDiskCleaner.Core.Tests;

/// <summary>The recursive delete: links are removed, never followed; placeholders are never deleted.</summary>
public class DeleteTests
{
    [Fact]
    public void Junction_inside_a_deleted_folder_is_removed_and_its_target_survives()
    {
        using var t = new TempDir();
        string sentinel = t.Dir("sentinel");
        string keep = t.File("must survive", "sentinel", "keep.txt");
        string victim = t.Dir("victim");
        t.File("cache", "victim", "data_0");
        t.File("deep", "victim", "sub", "deep.txt");
        Links.Junction(Path.Combine(victim, "link"), sentinel);

        Assert.Null(t.Guard().Check(victim));
        DeleteReport rep = SafeDelete.Delete(victim);

        Assert.False(Directory.Exists(victim));
        Assert.True(File.Exists(keep), "the junction's target was followed");
        Assert.Equal("must survive", File.ReadAllText(keep));
        Assert.Equal(1, rep.LinksRemoved);
        Assert.Equal(0, rep.Failed);
    }

    [Fact]
    public void Deleting_a_junction_itself_removes_only_the_link()
    {
        using var t = new TempDir();
        string sentinel = t.Dir("sentinel");
        string keep = t.File("x", "sentinel", "keep.txt");
        string link = Path.Combine(t.Root, "link");
        Links.Junction(link, sentinel);

        SafeDelete.Delete(link);

        Assert.False(PathUtil.Exists(link));
        Assert.True(File.Exists(keep));
    }

    [SkippableFact]
    public void Directory_and_file_symlinks_are_removed_not_followed()
    {
        using var t = new TempDir();
        string sentinel = t.Dir("sentinel");
        string keep = t.File("x", "sentinel", "keep.txt");
        string victim = t.Dir("victim");
        Links.SymlinkOrSkip(() =>
        {
            Directory.CreateSymbolicLink(Path.Combine(victim, "dirlink"), sentinel);
            File.CreateSymbolicLink(Path.Combine(victim, "filelink.txt"), keep);
        });

        DeleteReport rep = SafeDelete.Delete(victim);

        Assert.False(Directory.Exists(victim));
        Assert.True(File.Exists(keep));
        Assert.Equal(2, rep.LinksRemoved);
    }

    [Fact]
    public void Read_only_files_and_folders_are_deleted()
    {
        using var t = new TempDir();
        string victim = t.Dir("victim");
        string f = t.File("x", "victim", "sub", "locked.txt");
        File.SetAttributes(f, FileAttributes.ReadOnly);
        new DirectoryInfo(Path.Combine(victim, "sub")).Attributes |= FileAttributes.ReadOnly;

        DeleteReport rep = SafeDelete.Delete(victim);

        Assert.False(Directory.Exists(victim));
        Assert.Equal(0, rep.Failed);
    }

    [SkippableFact]
    public void Cloud_placeholder_files_are_kept_and_reported()
    {
        using var t = new TempDir();
        string folder = t.Dir("synced");
        string normal = t.File("x", "synced", "normal.txt");
        string placeholder = t.File("y", "synced", "online-only.docx");
        File.SetAttributes(placeholder, FileAttributes.Offline);
        Xunit.Skip.IfNot((File.GetAttributes(placeholder) & FileAttributes.Offline) != 0, "This file system does not keep the Offline attribute.");

        string result = Cleaner.DeletePath(folder, t.Guard());

        Assert.False(File.Exists(normal));
        Assert.True(File.Exists(placeholder));
        Assert.True(Directory.Exists(folder));
        Assert.StartsWith("partly kept: 1 cloud files", result);
    }

    [Fact]
    public void DeletePath_rechecks_the_guard_before_deleting()
    {
        using var t = new TempDir();
        string models = t.Dir("Models");
        string f = t.File("weights", "Models", "model.bin");
        Guard g = t.Guard(protectedPaths: [models]);

        string result = Cleaner.DeletePath(models, g);

        Assert.StartsWith("refused: protected", result);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void DeletePath_reports_deleted_for_a_plain_cache()
    {
        using var t = new TempDir();
        string cache = t.Dir("App", "Cache");
        t.File("x", "App", "Cache", "Cache_Data", "f_000001");

        Assert.Equal("deleted", Cleaner.DeletePath(cache, t.Guard()));
        Assert.False(Directory.Exists(cache));
        Assert.True(Directory.Exists(Path.Combine(t.Root, "App")));
    }

    [Fact]
    public void A_file_held_open_is_reported_as_partly_in_use()
    {
        using var t = new TempDir();
        string cache = t.Dir("App", "Cache");
        string busy = t.File(new string('x', 4096), "App", "Cache", "busy.bin");
        t.File("y", "App", "Cache", "free.bin");
        string result;
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = Cleaner.DeletePath(cache, t.Guard());
        }
        Assert.StartsWith("partly in use", result);
        Assert.False(File.Exists(Path.Combine(cache, "free.bin")));
    }
}
