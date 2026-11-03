namespace SafeDiskCleaner.Core;

/// <summary>What is deliberately left alone, shown so people can see it (KEPT in the reference).</summary>
public static class Kept
{
    public sealed record Entry(string What, string Why);

    public static readonly Entry[] List =
    [
        new("Claude desktop VM (vm_bundles)", "Claude's local VM runs on it"),
        new("Ollama, LM Studio and Hugging Face models", "Re-downloading costs hours"),
        new("WinGet tools, Programs, PATH folders", "Installed tools such as ffmpeg and uv"),
        new("NVIDIA and AMD shader caches", "Games stutter while they rebuild. The DirectX one is under Your call"),
        new("Chrome on-device AI model", "Chrome features use it"),
        new("Windows minidumps", "Needed to diagnose blue screens"),
        new("Your Downloads folder", "Windows Disk Cleanup offers to empty it. This app never will"),
        new("File History versions", "Your backups. Disk Cleanup lists them; this app leaves them"),
        new("OneDrive, CrossDevice, .ssh", "Synced or personal files"),
        new("Browser logins, cookies and history", "Personal data; clearing them signs you out"),
        new("Pictures, Music, Videos, game saves", "Your files"),
        new("Folders listed in protect.txt", "You asked for these to be left alone"),
    ];
}
