using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using WheelSwap.Core;

namespace WheelSwap.App;

public sealed record EntryTag(string Text, Brush Brush);

public sealed class EntryItem : INotifyPropertyChanged
{
    bool _checked;
    public required CarEntry Entry { get; init; }
    public string Label => Entry.Describe + (Entry.Problem is null ? "" : "  (not supported)");
    /// <summary>The chassis option as the template names it ("Release Default Chassis", "FOM Chassis", ...).</summary>
    public string Title => Entry.Title;
    public string FileName => Entry.FileName;
    public IEnumerable<EntryTag> Tags
    {
        get
        {
            if (Entry.Swapped) yield return new("Swapped chassis", MainWindow.Res("Magenta"));
            if (Entry.PreviousSwap is not null) yield return new("Wheel already swapped", MainWindow.Res("Amber"));
            if (Entry.Problem is not null) yield return new("Not supported", MainWindow.Res("Red"));
        }
    }
    public string? Tooltip => Entry.Problem ?? Entry.ZipPath;
    public bool IsEnabled => Entry.Problem is null;
    public bool IsChecked { get => _checked; set { _checked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record LogLine(string Text, Brush Brush);

sealed class Settings
{
    public string? GameDir { get; set; }
    public string? LastTemplateDir { get; set; }
    public string? Donor { get; set; }
    public bool SaveReport { get; set; }

    static string PathOf => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteeringWheelSwapper", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf)) ?? new(); } catch { return new(); }
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!); File.WriteAllText(PathOf, JsonSerializer.Serialize(this)); } catch { }
    }
}

public partial class MainWindow : Window
{
    readonly Settings _settings = Settings.Load();
    readonly ObservableCollection<EntryItem> _entries = [];
    readonly ObservableCollection<LogLine> _log = [];
    SerpsTemplate? _template;
    bool _outEdited;

    /// <summary>The app version (from the project's Version), e.g. "1.3.1".</summary>
    static string AppVersion =>
        (System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?").Split('+')[0];

    internal static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + AppVersion;
        EntryList.ItemsSource = _entries;
        LogList.ItemsSource = _log;
        // donors grouped by season: the 2026 cars, then the 2025 cars
        var donors = new System.Windows.Data.ListCollectionView(GameData.Donors);
        donors.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(Team.Season)));
        DonorBox.ItemsSource = donors;
        DonorBox.SelectedItem = GameData.Donors.FirstOrDefault(t => t.Id == _settings.Donor) ?? GameData.Teams2026[1];
        GameBox.Text = _settings.GameDir ?? GameData.DetectGameDir() ?? "";
        // hover a "steering wheel from" option: a picture of that wheel (rendered from the game files, cached)
        DonorBox.ItemContainerGenerator.StatusChanged += (_, _) => AttachWheelTips();
        Loaded += (_, _) => { AttachWheelTips(); _ = PrewarmWheelPictures(); };
        ReportBox.IsChecked = _settings.SaveReport;
        OutBox.PreviewKeyDown += (_, _) => _outEdited = true;
        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.FirstOrDefault(f => f.EndsWith(".serps", StringComparison.OrdinalIgnoreCase)) is { } f)
                LoadTemplate(f);
        };
        var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => a.EndsWith(".serps", StringComparison.OrdinalIgnoreCase));
        if (arg is not null && File.Exists(arg)) Loaded += (_, _) => LoadTemplate(arg);
        Log("Pick a template and the cars to change, choose whose steering wheel to use, then Build.", Res("LogText"));
    }

    // ---------------------------------------------------------------- wheel pictures on hover

    static readonly string WheelCacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteeringWheelSwapper", "wheels");
    const int ThumbW = 480, ThumbH = 320;
    readonly Dictionary<string, Task<string>> _thumbs = [];
    readonly SemaphoreSlim _thumbGate = new(1, 1);
    string? _thumbGame;

    /// <summary>The picture of a donor's stock wheel (file path), rendered once per game install and cached.</summary>
    Task<string> WheelPicture(Team t)
    {
        var dir = GameBox.Text.Trim();
        if (_thumbGame != dir) { _thumbs.Clear(); _thumbGame = dir; }
        if (_thumbs.TryGetValue(t.Id, out var task) && !task.IsFaulted) return task;
        return _thumbs[t.Id] = Task.Run(async () =>
        {
            await _thumbGate.WaitAsync();
            try { return WheelThumbnail.CachedPath(new GameData(dir), t.Id, ThumbW, ThumbH, WheelCacheDir); }
            finally { _thumbGate.Release(); }
        });
    }

    /// <summary>Render every wheel picture in the background so hovering shows them at once.</summary>
    async Task PrewarmWheelPictures()
    {
        foreach (var t in GameData.Donors)
        {
            try { await WheelPicture(t); }
            catch (Exception) { return; }   // no usable game folder yet: pictures render on hover once it is set
        }
    }

    void AttachWheelTips()
    {
        if (DonorBox.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated) return;
        foreach (var t in GameData.Donors)
        {
            if (DonorBox.ItemContainerGenerator.ContainerFromItem(t) is not System.Windows.Controls.ListBoxItem item || item.ToolTip is System.Windows.Controls.ToolTip) continue;
            var image = new System.Windows.Controls.Image { Width = ThumbW / 2, Height = ThumbH / 2, Stretch = Stretch.Uniform };
            var note = new System.Windows.Controls.TextBlock
            {
                Text = "rendering…", FontFamily = (FontFamily)Application.Current.Resources["Mono"], FontWeight = FontWeights.Bold, FontSize = 10.5,
                Foreground = Res("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
            };
            var title = new System.Windows.Controls.TextBlock
            {
                Text = WheelSuffix(t).ToUpperInvariant(), FontFamily = (FontFamily)Application.Current.Resources["Display"], FontWeight = FontWeights.Bold,
                FontSize = 10.5, Foreground = Res("Ink"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            };
            var panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(image);
            panel.Children.Add(title);
            panel.Children.Add(note);
            var tip = new System.Windows.Controls.ToolTip
            {
                Content = panel, Background = Res("Panel"), BorderBrush = Res("Purple"), BorderThickness = new Thickness(1.5), Padding = new Thickness(12, 10, 12, 8),
                Placement = System.Windows.Controls.Primitives.PlacementMode.Top, HasDropShadow = true,
            };
            System.Windows.Controls.ToolTipService.SetInitialShowDelay(item, 250);
            System.Windows.Controls.ToolTipService.SetShowDuration(item, 60000);
            item.ToolTip = tip;
            item.ToolTipOpening += async (_, _) =>
            {
                if (image.Source is not null) return;
                try
                {
                    var path = await WheelPicture(t);
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(path); bmp.EndInit(); bmp.Freeze();
                    image.Source = bmp;
                    note.Text = "stock wheel · " + t.Season + " car";
                }
                catch (Exception) { note.Text = "set the F1 25 game folder to see this wheel"; }
            };
        }
    }

    void Log(string s, Brush b)
    {
        _log.Add(new LogLine(s, b));
        LogList.ScrollIntoView(_log[^1]);
    }

    void Detect_Click(object sender, RoutedEventArgs e)
    {
        var d = GameData.DetectGameDir();
        if (d is null) Log("Could not find F1 25 in your Steam libraries. Use Browse… to pick the game folder.", Res("LogWarn"));
        else { GameBox.Text = d; Log($"Found F1 25: {d}", Res("LogText")); }
    }

    void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the F1 25 game folder" };
        if (dlg.ShowDialog(this) == true) GameBox.Text = dlg.FolderName;
    }

    void BrowseTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "SERPs template (*.serps)|*.serps", InitialDirectory = _settings.LastTemplateDir ?? "" };
        if (dlg.ShowDialog(this) == true) LoadTemplate(dlg.FileName);
    }

    void LoadTemplate(string path)
    {
        try
        {
            WaitCursor(true);
            _template = new SerpsTemplate(path);
            TemplateBox.Text = path;
            _settings.LastTemplateDir = Path.GetDirectoryName(path);
            _entries.Clear();
            foreach (var c in _template.CarEntries) _entries.Add(new EntryItem { Entry = c });
            EntryHint.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EntryHint.Text = "no car entries with a steering wheel were found in this template.";
            Log($"Template {_template.Name}: {_entries.Count} car entr{(_entries.Count == 1 ? "y" : "ies")} found.", Res("LogText"));
            _outEdited = false;
            SuggestOutput();
        }
        catch (Exception ex) { Log("Could not read the template: " + ex.Message, Res("LogError")); }
        finally { WaitCursor(false); }
    }

    void Donor_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SuggestOutput();

    void SuggestOutput()
    {
        if (_template is null || _outEdited || DonorBox.SelectedItem is not Team t) return;
        OutBox.Text = Path.Combine(Path.GetDirectoryName(_template.SourcePath)!, $"{OutputBaseName(_template.Name)} - {WheelSuffix(t)}.serps");
    }

    /// <summary>"26 Ferrari Wheel", "25 Aston Martin Wheel".</summary>
    internal static string WheelSuffix(Team t) => GameData.WheelSuffix(t.Id);

    /// <summary>The template name without the wheel ending an earlier build gave it.</summary>
    internal static string OutputBaseName(string templateName) => GameData.StripWheelSuffix(templateName);

    void BrowseOut_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "SERPs template (*.serps)|*.serps",
            FileName = Path.GetFileName(OutBox.Text),
            InitialDirectory = Path.GetDirectoryName(OutBox.Text) ?? "",
        };
        if (dlg.ShowDialog(this) == true) { OutBox.Text = dlg.FileName; _outEdited = true; }
    }

    async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_template is null) { Log("Pick a template first.", Res("LogWarn")); return; }
        var selected = _entries.Where(x => x.IsChecked).Select(x => x.Entry).ToList();
        if (selected.Count == 0) { Log("Tick at least one car.", Res("LogWarn")); return; }
        if (DonorBox.SelectedItem is not Team donor) { Log("Pick whose steering wheel to use.", Res("LogWarn")); return; }
        var outPath = OutBox.Text.Trim();
        if (outPath.Length == 0) { Log("Choose where to save the new .serps.", Res("LogWarn")); return; }
        if (File.Exists(outPath) && MessageBox.Show(this, $"{Path.GetFileName(outPath)} already exists. Replace it?", "Steering Wheel Swapper",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        GameData game;
        try { game = new GameData(GameBox.Text.Trim()); }
        catch (Exception ex) { Log(ex.Message, Res("LogError")); return; }

        bool saveReport = ReportBox.IsChecked == true;
        _settings.GameDir = game.GameDir; _settings.Donor = donor.Id; _settings.SaveReport = saveReport; _settings.Save();
        _log.Clear();
        Log($"{donor.Label} steering wheel -> {string.Join(", ", selected.Select(s => s.FileName))}", Res("LogText"));
        var log = new SwapLog();
        log.Line += (s, w) => Dispatcher.Invoke(() => Log((w ? "Warning: " : "") + s, w ? Res("LogWarn") : Res("LogText")));
        SetBusy(true);
        ReportButton.Visibility = Visibility.Collapsed;
        StatusPill.Visibility = Visibility.Collapsed;
        try
        {
            var tpl = _template;
            var res = await Task.Run(() => tpl.Build(game, donor.Id, selected, outPath, log));
            Log($"Done. Saved {Path.GetFileName(outPath)}" + (log.Warnings.Count > 0 ? $" ({log.Warnings.Count} warning(s) above)." : "."), Res("LogOk"));
            Status($"Verified · {res.Verification.Sum(v => v.Checks)} checks", "Teal");
            if (saveReport) try
            {
                _reportPath = await Task.Run(() => BuildReport.Write(game, tpl, res, log, outPath));
                ReportButton.Visibility = Visibility.Visible;
                Log($"Report: {Path.GetFileName(_reportPath)}", Res("LogMuted"));
            }
            catch (Exception ex) { Log("The build is fine, but the report could not be written: " + ex.Message, Res("LogWarn")); }
        }
        catch (Exception ex) { Log("Failed: " + ex.Message, Res("LogError")); Status("Failed · nothing written", "Red"); }
        finally { SetBusy(false); }
    }

    string? _reportPath;

    /// <summary>A trial build (checked, nothing saved) shown as an interactive 3D page in the browser.</summary>
    async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_template is null) { Log("Pick a template first.", Res("LogWarn")); return; }
        var selected = _entries.Where(x => x.IsChecked).Select(x => x.Entry).ToList();
        if (selected.Count == 0) { Log("Tick at least one car.", Res("LogWarn")); return; }
        if (DonorBox.SelectedItem is not Team donor) { Log("Pick whose steering wheel to use.", Res("LogWarn")); return; }
        GameData game;
        try { game = new GameData(GameBox.Text.Trim()); }
        catch (Exception ex) { Log(ex.Message, Res("LogError")); return; }

        _log.Clear();
        Log($"Preview: {donor.Label} steering wheel -> {string.Join(", ", selected.Select(s => s.FileName))} (nothing is saved)", Res("LogText"));
        var log = new SwapLog();
        log.Line += (s, w) => Dispatcher.Invoke(() => Log((w ? "Warning: " : "") + s, w ? Res("LogWarn") : Res("LogText")));
        SetBusy(true);
        StatusPill.Visibility = Visibility.Collapsed;
        try
        {
            var tpl = _template;
            var path = await Task.Run(() =>
            {
                var res = tpl.Build(game, donor.Id, selected, null, log);
                var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SteeringWheelSwapper")).FullName;
                var file = Path.Combine(dir, $"Preview - {OutputBaseName(tpl.Name)} - {WheelSuffix(donor)}.html");
                File.WriteAllText(file, WheelPreview.Page(game, tpl, res), new System.Text.UTF8Encoding(false));
                return file;
            });
            Log("Preview opened in your browser. Build to save it.", Res("LogOk"));
            Status("Preview · nothing saved", "Teal");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log("Preview failed: " + ex.Message, Res("LogError")); }
        finally { SetBusy(false); }
    }

    void Status(string text, string brush)
    {
        StatusText.Text = text;
        StatusPill.Background = Res(brush);
        StatusText.Foreground = brush == "Red" ? Res("Paper") : Res("Ink");
        StatusPill.Visibility = Visibility.Visible;
    }

    void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_reportPath is null || !File.Exists(_reportPath)) { Log("The report file is no longer there.", Res("LogWarn")); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_reportPath) { UseShellExecute = true }); }
        catch (Exception ex) { Log("Could not open the report: " + ex.Message, Res("LogWarn")); }
    }

    void SetBusy(bool b)
    {
        BuildButton.IsEnabled = !b;
        PreviewButton.IsEnabled = !b;
        Busy.Visibility = b ? Visibility.Visible : Visibility.Collapsed;
        EntryList.IsEnabled = !b;
        DonorBox.IsEnabled = !b;
        ReportBox.IsEnabled = !b;
        WaitCursor(b);
    }

    static void WaitCursor(bool wait) => System.Windows.Input.Mouse.OverrideCursor = wait ? System.Windows.Input.Cursors.Wait : null;
}
