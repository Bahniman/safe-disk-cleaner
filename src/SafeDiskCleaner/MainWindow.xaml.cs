using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SafeDiskCleaner.Core;

namespace SafeDiskCleaner;

/// <summary>One window, one scan, one Clean button.</summary>
public sealed partial class MainWindow : Window
{
    private const double WideLayoutMinWidth = 1100;
    private const int MaxDetailLines = 300;

    private readonly bool _admin = Tools.IsAdmin();
    private readonly Dictionary<string, bool> _checked = new(StringComparer.Ordinal);
    private readonly Dictionary<Rule, RowView> _rows = [];
    private readonly Dictionary<RuleGroup, CheckBox> _groupBoxes = [];
    private ScanResult? _scan;
    private string? _selectedId;
    private bool _busy;
    private bool? _wide;

    private sealed record RowView(Border Container, CheckBox Box);

    public MainWindow()
    {
        InitializeComponent();

        // Windows 11 look: Mica (the stronger BaseAlt tint, so the cards stand out), content drawn
        // into the title bar, system caption buttons. Every layer above it is transparent.
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(ico))
        {
            AppWindow.SetIcon(ico);
        }
        string png = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
        if (File.Exists(png))
        {
            TitleBarIcon.Source = new BitmapImage(new Uri(png));
        }

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowSizing.SetMinimumSize(hwnd, 900, 600);
        WindowSizing.ResizeAndCenter(AppWindow, hwnd, 1280, 800);

        Activated += (_, e) => TitleBarText.Opacity = e.WindowActivationState == WindowActivationState.Deactivated ? 0.5 : 1.0;
        RootGrid.SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width, e.NewSize.Height);
        RootGrid.ActualThemeChanged += (_, _) => Render();
        RootGrid.Loaded += async (_, _) =>
        {
            ApplyLayout(RootGrid.ActualWidth, RootGrid.ActualHeight);
            await ScanAsync();
        };

        SetupAdminBar();
        UpdateDriveHeader();
        UpdateLastCleaned();
        ShowDetails(null);
    }

    // ------------------------------------------------------------------ scan and clean

    private async Task ScanAsync()
    {
        if (_busy)
        {
            return;
        }
        GroupsPanel.Visibility = Visibility.Collapsed;
        ScanningPanel.Visibility = Visibility.Visible;
        SetBusy(true, "Scanning…");
        ScanBar.Value = 0;
        ScanningText.Text = "Scanning…";
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanBar.Maximum = Math.Max(1, p.Total);
            ScanBar.Value = p.Done;
            ScanningText.Text = $"Scanning… {p.Current}";
        });
        try
        {
            bool admin = _admin;
            _scan = await Task.Run(() => Scanner.Scan(admin, progress));
            foreach (Rule r in _scan.Rules)
            {
                _checked.TryAdd(r.Id, r.DefaultChecked);
            }
            StatusText.Text = $"Found {Fmt(_scan.Total)} in {_scan.Elapsed.TotalSeconds:0.0} s.";
        }
        catch (Exception ex)
        {
            RunLog.WriteError(ex.ToString());
            ShowError("The scan failed", ex.Message);
            StatusText.Text = "";
        }
        finally
        {
            ScanningPanel.Visibility = Visibility.Collapsed;
            GroupsPanel.Visibility = Visibility.Visible;
            SetBusy(false);
            Render();
            UpdateDriveHeader();
        }
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is null || _busy)
        {
            return;
        }
        List<Rule> pick = SelectedRules();
        if (pick.Count == 0)
        {
            return;
        }
        int count = pick.Sum(r => r.Items.Count);
        long bytes = pick.Sum(r => r.Total);
        string text = $"Delete {count} item{(count == 1 ? "" : "s")}, about {Fmt(bytes)}? This can't be undone.";
        if (pick.Any(r => r.Items.Any(i => i.Handler is not null)))
        {
            text += "\n\nWindows' own Disk Cleanup runs too and can take 10+ minutes.";
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            Title = "Clean selected?",
            Content = text,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true, "Cleaning…");
        ResultInfoBar.IsOpen = false;
        var progress = new Progress<CleanProgress>(p =>
        {
            WorkBar.Maximum = Math.Max(1, p.Total);
            WorkBar.Value = p.Done;
            StatusText.Text = p.Current.Length > 0 ? $"Cleaning… {p.Current.Trim()}" : "Measuring freed space…";
        });
        CleanResult result;
        try
        {
            result = await Task.Run(() => Cleaner.Clean(pick, progress));
        }
        catch (Exception ex)
        {
            RunLog.WriteError(ex.ToString());
            ShowError("Cleaning stopped", ex.Message);
            SetBusy(false);
            return;
        }
        SetBusy(false);

        ResultInfoBar.Severity = result.Problems > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        ResultInfoBar.Title = result.Freed > 0
            ? $"Freed {Fmt(result.Freed)} (measured on the drive)"
            : "Done. The drive's free space did not grow measurably.";
        ResultInfoBar.Message = result.Problems > 0
            ? $"{result.Problems} of {result.Items} items were skipped, refused or in use. The log lists each one."
            : $"{result.Items} item{(result.Items == 1 ? "" : "s")} cleaned.";
        ResultInfoBar.IsOpen = true;
        UpdateLastCleaned();
        await ScanAsync();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private List<Rule> SelectedRules() => _scan?.Rules
        .Where(r => r.CanClean && _checked.GetValueOrDefault(r.Id))
        .ToList() ?? [];

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        RescanButton.IsEnabled = !busy;
        ProtectButton.IsEnabled = !busy;
        RestartAdminButton.IsEnabled = !busy;
        ListScroller.IsEnabled = !busy;
        WorkBar.Visibility = busy && ScanningPanel.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        WorkBar.Value = 0;
        if (status is not null)
        {
            StatusText.Text = status;
        }
        UpdateCleanButton();
    }

    // ------------------------------------------------------------------ list

    private void Render()
    {
        GroupsPanel.Children.Clear();
        _rows.Clear();
        _groupBoxes.Clear();
        if (_scan is null)
        {
            return;
        }
        foreach (RuleGroup g in RuleGroups.Order)
        {
            List<Rule> rules = _scan.Rules
                .Where(r => r.Group == g && r.Visible)
                .OrderByDescending(r => r.CanClean)
                .ThenByDescending(r => r.Total)
                .ToList();
            if (rules.Count > 0)
            {
                GroupsPanel.Children.Add(BuildGroup(g, rules));
            }
        }
        GroupsPanel.Children.Add(BuildKept());
        ShowDetails(_scan.Rules.FirstOrDefault(r => r.Id == _selectedId));
        UpdateCleanButton();
    }

    private UIElement BuildGroup(RuleGroup g, List<Rule> rules)
    {
        long total = rules.Sum(r => r.Total);
        string title = RuleGroups.Title(g);

        var box = new CheckBox { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, $"Select everything in {title}");
        box.Click += (_, _) => OnGroupBoxClick(g);
        _groupBoxes[g] = box;

        var header = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 10, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new FontIcon { Glyph = GroupGlyph(g), FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = Res<Style>("BodyStrongTextBlockStyle") });
        text.Children.Add(new TextBlock { Text = RuleGroups.Blurb(g, _admin), Style = Res<Style>("MutedCaptionStyle"), TextWrapping = TextWrapping.Wrap });
        var size = new TextBlock
        {
            Text = total > 0 ? Fmt(total) : "",
            Style = Res<Style>("BodyStrongTextBlockStyle"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Place(header, box, 0);
        Place(header, glyph, 1);
        Place(header, text, 2);
        Place(header, size, 3);

        var rows = new StackPanel { Spacing = 2 };
        foreach (Rule r in rules)
        {
            rows.Children.Add(BuildRow(r));
        }

        var expander = new Expander
        {
            Header = header,
            Content = rows,
            IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(expander, total > 0 ? $"{title}, {Fmt(total)}" : title);
        RefreshGroupBox(g);
        return expander;
    }

    private UIElement BuildRow(Rule r)
    {
        bool can = r.CanClean;
        string sizeText = SizeText(r);
        string description = Describe(r);

        var box = new CheckBox
        {
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = can,
            IsChecked = can && _checked.GetValueOrDefault(r.Id),
        };
        AutomationProperties.SetName(box, $"{r.Title}, {sizeText}");
        AutomationProperties.SetHelpText(box, description);
        box.Click += (_, _) =>
        {
            _checked[r.Id] = box.IsChecked == true;
            RefreshGroupBox(r.Group);
            UpdateCleanButton();
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = r.Title,
            Style = Res<Style>(can ? "BodyTextBlockStyle" : "DisabledBodyStyle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            Style = Res<Style>(can ? "MutedCaptionStyle" : r.Blocked is not null && r.Blocked != Scanner.NeedsAdmin ? "CautionCaptionStyle" : "DisabledCaptionStyle"),
            TextWrapping = TextWrapping.Wrap,
        });
        var size = new TextBlock
        {
            Text = sizeText,
            Style = Res<Style>(can ? "BodyStrongTextBlockStyle" : "DisabledBodyStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            MinWidth = 72,
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(grid, box, 0);
        Place(grid, text, 1);
        Place(grid, size, 2);

        var container = new Border { Child = grid, Style = Res<Style>(r.Id == _selectedId ? "SelectedRowStyle" : "RowStyle") };
        container.Tapped += (_, _) => Select(r);

        // A row whose box is disabled still needs a keyboard stop so its details can be read.
        var host = new ContentControl
        {
            Content = container,
            IsTabStop = !can,
            UseSystemFocusVisuals = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(host, $"{r.Title}, {description}");
        host.GotFocus += (_, _) => Select(r);

        _rows[r] = new RowView(container, box);
        return host;
    }

    private UIElement BuildKept()
    {
        var header = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 10, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Kept on purpose", Style = Res<Style>("BodyStrongTextBlockStyle") });
        int checkedFolders = _scan?.Guard.InUsePaths.Count ?? 0;
        text.Children.Add(new TextBlock
        {
            Text = $"Never cleaned. Every delete is also checked against {checkedFolders} install and PATH folders on this PC.",
            Style = Res<Style>("MutedCaptionStyle"),
            TextWrapping = TextWrapping.Wrap,
        });
        Place(header, new FontIcon { Glyph = "\uEA18", FontSize = 20, VerticalAlignment = VerticalAlignment.Center }, 0);
        Place(header, text, 1);

        var rows = new StackPanel { Spacing = 10, Padding = new Thickness(12, 4, 12, 4) };
        foreach (Kept.Entry k in Kept.List)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lines = new StackPanel();
            lines.Children.Add(new TextBlock { Text = k.What, Style = Res<Style>("BodyTextBlockStyle"), TextWrapping = TextWrapping.Wrap });
            lines.Children.Add(new TextBlock { Text = k.Why, Style = Res<Style>("MutedCaptionStyle"), TextWrapping = TextWrapping.Wrap });
            Place(row, new FontIcon { Glyph = "\uE72E", FontSize = 14, VerticalAlignment = VerticalAlignment.Center }, 0);
            Place(row, lines, 1);
            rows.Children.Add(row);
        }

        var expander = new Expander
        {
            Header = header,
            Content = rows,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(expander, "Kept on purpose");
        return expander;
    }

    private void OnGroupBoxClick(RuleGroup g)
    {
        List<Rule> rules = CleanableIn(g);
        bool allOn = rules.Count > 0 && rules.All(r => _checked.GetValueOrDefault(r.Id));
        foreach (Rule r in rules)
        {
            _checked[r.Id] = !allOn;
            if (_rows.TryGetValue(r, out RowView? v))
            {
                v.Box.IsChecked = !allOn;
            }
        }
        RefreshGroupBox(g);
        UpdateCleanButton();
    }

    private void RefreshGroupBox(RuleGroup g)
    {
        if (!_groupBoxes.TryGetValue(g, out CheckBox? box))
        {
            return;
        }
        List<Rule> rules = CleanableIn(g);
        int on = rules.Count(r => _checked.GetValueOrDefault(r.Id));
        box.IsEnabled = rules.Count > 0;
        box.IsChecked = rules.Count == 0 || on == 0 ? false : on == rules.Count ? true : null;
    }

    private List<Rule> CleanableIn(RuleGroup g) => _scan?.Rules.Where(r => r.Group == g && r.CanClean).ToList() ?? [];

    private void UpdateCleanButton()
    {
        List<Rule> pick = SelectedRules();
        long total = pick.Sum(r => r.Total);
        bool unknown = pick.Any(r => r.HasUnknownSize);
        string label = pick.Count == 0
            ? "Clean selected"
            : total > 0 ? $"Clean selected · {Fmt(total)}{(unknown ? " +" : "")}" : "Clean selected";
        CleanButton.Content = label;
        CleanButton.IsEnabled = pick.Count > 0 && !_busy;
        AutomationProperties.SetName(CleanButton, label.Replace("·", ",", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ details pane

    private void Select(Rule r)
    {
        if (_selectedId == r.Id)
        {
            return;
        }
        _selectedId = r.Id;
        foreach ((Rule rule, RowView v) in _rows)
        {
            v.Container.Style = Res<Style>(rule.Id == r.Id ? "SelectedRowStyle" : "RowStyle");
        }
        ShowDetails(r);
    }

    private void ShowDetails(Rule? r)
    {
        DetailsList.Children.Clear();
        if (r is null)
        {
            DetailsTitle.Text = "Details";
            DetailsWhy.Text = "Select a row to see exactly which folders it covers, and anything the safety check refused.";
            AddDetailNote("Every run is logged to runs.csv, so you can always see what was removed.");
            return;
        }
        DetailsTitle.Text = r.Title;
        DetailsWhy.Text = r.Blocked switch
        {
            null => r.Why,
            Scanner.NeedsAdmin => r.Why + " Needs admin: use Restart as admin at the top.",
            string b => $"{r.Why} {b}.",
        };

        int shown = 0;
        foreach (Item it in r.Items.OrderByDescending(i => i.Size ?? -1))
        {
            if (shown++ == MaxDetailLines)
            {
                AddDetailNote($"…and {r.Items.Count - MaxDetailLines} more.");
                break;
            }
            string label = it.Label.Trim();
            string? path = it.Path is not null && !string.Equals(it.Path, it.Label, StringComparison.Ordinal) ? it.Path : null;
            AddDetailLine(label, path, it.Size is null ? "after the run" : Fmt(it.Size));
        }
        if (r.Skipped.Count > 0)
        {
            DetailsList.Children.Add(new TextBlock
            {
                Text = "Left alone",
                Style = Res<Style>("BodyStrongTextBlockStyle"),
                Margin = new Thickness(0, shown > 0 ? 12 : 0, 0, 0),
            });
            foreach (Skip s in r.Skipped)
            {
                AddDetailLine(s.What, s.Why, "", muted: true);
            }
        }
        if (r.Items.Count == 0 && r.Skipped.Count == 0)
        {
            AddDetailNote("Nothing found.");
        }
    }

    private void AddDetailLine(string title, string? sub, string size, bool muted = false)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lines = new StackPanel();
        lines.Children.Add(new TextBlock
        {
            Text = title,
            Style = Res<Style>(muted ? "MutedBodyStyle" : "BodyTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        if (!string.IsNullOrEmpty(sub))
        {
            lines.Children.Add(new TextBlock
            {
                Text = sub,
                Style = Res<Style>("MutedCaptionStyle"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
        }
        Place(grid, lines, 0);
        Place(grid, new TextBlock { Text = size, Style = Res<Style>("BodyTextBlockStyle"), VerticalAlignment = VerticalAlignment.Top }, 1);
        DetailsList.Children.Add(grid);
    }

    private void AddDetailNote(string text) => DetailsList.Children.Add(new TextBlock
    {
        Text = text,
        Style = Res<Style>("MutedCaptionStyle"),
        TextWrapping = TextWrapping.Wrap,
    });

    // ------------------------------------------------------------------ header and bars

    private void SetupAdminBar()
    {
        AdminInfoBar.IsOpen = true;
        if (_admin)
        {
            AdminInfoBar.Severity = InfoBarSeverity.Success;
            AdminInfoBar.Title = "Running as administrator";
            AdminInfoBar.Message = "The Windows group (system temp, old update downloads, error reports) is included.";
            AdminInfoBar.ActionButton = null;
        }
        else
        {
            AdminInfoBar.Severity = InfoBarSeverity.Informational;
            AdminInfoBar.Title = "Running without admin";
            AdminInfoBar.Message = "Everything works except the Windows group, which needs admin to read Windows' own folders.";
        }
    }

    private void UpdateDriveHeader()
    {
        string label = KnownPaths.SystemDrive.TrimEnd('\\');
        DriveLabel.Text = label;
        if (Tools.SystemDriveUsage() is (long total, long free) && total > 0)
        {
            DriveText.Text = $"{SizeFormat.FormatGb(free)} free of {SizeFormat.FormatGb(total)}";
            double used = 100.0 * (total - free) / total;
            DriveBar.Value = used;
            AutomationProperties.SetName(DriveBar, $"Drive {label}: {used:0} percent used, {SizeFormat.FormatGb(free)} free");
        }
    }

    private void UpdateLastCleaned()
    {
        if (RunLog.ReadLast() is (DateTime when, long freed))
        {
            int days = (int)(DateTime.Now.Date - when.Date).TotalDays;
            string ago = days <= 0 ? "today" : days == 1 ? "yesterday" : $"{days} days ago";
            LastCleanedText.Text = freed > 0 ? $"Last cleaned {ago}, freed {Fmt(freed)}" : $"Last cleaned {ago}";
        }
        else
        {
            LastCleanedText.Text = "Not cleaned with this app yet";
        }
    }

    public void ShowError(string title, string message)
    {
        ErrorInfoBar.Title = title;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }

    private void ShowNotice(string title, string message)
    {
        NoticeInfoBar.Title = title;
        NoticeInfoBar.Message = message;
        NoticeInfoBar.IsOpen = true;
    }

    private void ApplyLayout(double width, double height)
    {
        bool wide = width >= WideLayoutMinWidth;
        if (!wide)
        {
            // Below the list: tall enough to read, never crowding the list on short windows.
            DetailsRow.Height = new GridLength(Math.Clamp(height * 0.28, 140, 260));
        }
        if (_wide == wide)
        {
            return;
        }
        _wide = wide;
        if (wide)
        {
            DetailsColumn.Width = new GridLength(380);
            DetailsRow.Height = new GridLength(0);
            BodyGrid.ColumnSpacing = 16;
            BodyGrid.RowSpacing = 0;
            Grid.SetRow(DetailsPane, 0);
            Grid.SetColumn(DetailsPane, 1);
        }
        else
        {
            DetailsColumn.Width = new GridLength(0);
            BodyGrid.ColumnSpacing = 0;
            BodyGrid.RowSpacing = 12;
            Grid.SetRow(DetailsPane, 1);
            Grid.SetColumn(DetailsPane, 0);
        }
    }

    // ------------------------------------------------------------------ commands

    private async void Protect_Click(object sender, RoutedEventArgs e)
    {
        string? folder;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
            var picked = await picker.PickSingleFolderAsync();
            if (picked is null)
            {
                return;
            }
            folder = picked.Path;
        }
        catch (Exception ex)
        {
            // Fall back to typing or pasting the path if the picker is unavailable.
            RunLog.WriteError("Folder picker failed: " + ex);
            folder = await AskForFolderAsync();
        }
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }
        if (!Directory.Exists(folder))
        {
            ShowError("Folder not found", folder);
            return;
        }
        try
        {
            UserProtection.Add(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError("Could not update protect.txt", ex.Message);
            return;
        }
        ShowNotice("Folder protected", $"{folder} and everything in it will be left alone. The list is in protect.txt.");
        await ScanAsync();
    }

    private async Task<string?> AskForFolderAsync()
    {
        var box = new TextBox { PlaceholderText = @"For example D:\Games\Mods or %USERPROFILE%\Mods", AcceptsReturn = false };
        AutomationProperties.SetName(box, "Folder to protect");
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            Title = "Protect a folder",
            Content = box,
            PrimaryButtonText = "Protect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? Environment.ExpandEnvironmentVariables(box.Text.Trim().Trim('"'))
            : null;
    }

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Environment.ProcessPath is not string exe)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
            Application.Current.Exit();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user said no to the UAC prompt; carry on as before.
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowError("Could not restart as admin", ex.Message);
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(RunLog.FilePath))
        {
            ShowNotice("No log yet", $"The log is created the first time you clean: {RunLog.FilePath}");
            return;
        }
        OpenFile(RunLog.FilePath);
    }

    private void OpenProtectFile_Click(object sender, RoutedEventArgs e) => OpenFile(UserProtection.EnsureFile());

    private void StorageSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowError("Could not open Windows storage settings", ex.Message);
        }
    }

    private void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No app is associated with the file type; Notepad always is there.
            try
            {
                Process.Start(new ProcessStartInfo(Tools.System32("notepad.exe"), $"\"{path}\"") { UseShellExecute = false });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                ShowError("Could not open the file", path);
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string Fmt(long? bytes) => SizeFormat.Format(bytes);

    private static string SizeText(Rule r)
    {
        if (r.Items.Count == 0)
        {
            return "";
        }
        if (r.Total == 0)
        {
            return r.HasUnknownSize ? "Size shows after the run" : "";
        }
        return Fmt(r.Total) + (r.HasUnknownSize ? " +" : "");
    }

    private static string Describe(Rule r)
    {
        if (r.Blocked == Scanner.NeedsAdmin)
        {
            return "Needs admin. Use Restart as admin above.";
        }
        if (r.Blocked is not null)
        {
            return r.Blocked;
        }
        string d = r.Group == RuleGroup.Apps
            ? $"{r.Items.Count} cache folder{(r.Items.Count == 1 ? "" : "s")}"
            : r.Why;
        if (r.Skipped.Count > 0)
        {
            d += $"  ({r.Skipped.Count} left alone)";
        }
        return d;
    }

    private static string GroupGlyph(RuleGroup g) => g switch
    {
        RuleGroup.Leftovers => "\uE7B8", // Package
        RuleGroup.Apps => "\uE774",      // Globe
        RuleGroup.YourCall => "\uE74D",  // Delete
        RuleGroup.Windows => "\uE770",   // System
        _ => "\uE8B7",
    };

    private static T Res<T>(string key) where T : class => (T)Application.Current.Resources[key];

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
