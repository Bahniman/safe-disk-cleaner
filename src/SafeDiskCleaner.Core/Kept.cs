namespace SafeDiskCleaner.Core;

/// <summary>What is deliberately left alone, shown so people can see it (KEPT in the reference).</summary>
public static class Kept
{
    public sealed record Entry(string What, string Why, bool OpensStorageSettings = false);

    public static readonly Entry[] List =
    [
        new("Claude desktop VM (vm_bundles)", "Claude's local VM runs on it"),
        new("Ollama, LM Studio and Hugging Face models", "Re-downloading costs hours"),
        new("WinGet tools, Programs, PATH folders", "Installed tools such as ffmpeg and uv"),
        new("Game shader caches (NVIDIA, AMD, D3D)", "Games stutter while they rebuild"),
        new("Chrome on-device AI model", "Chrome features use it"),
        new("Windows crash dumps", "Needed to diagnose blue screens"),
        new("OneDrive, CrossDevice, .ssh", "Synced or personal files"),
        new("Browser logins, cookies and history", "Personal data; clearing them signs you out"),
        new("Pictures, Music, Videos, game saves", "Your files"),
        new("Folders listed in protect.txt", "You asked for these to be left alone"),
        new("Windows.old and previous Windows updates", "Windows removes these better itself", OpensStorageSettings: true),
    ];
}
