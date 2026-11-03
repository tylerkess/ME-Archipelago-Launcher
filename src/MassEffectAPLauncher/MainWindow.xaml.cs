// The launcher window: a campaign rail (ME1/ME2/ME3) and four pages for the selected game.
//   Deploy:  status, the game's newest GitHub release, Download/Install/Update, Launch, Uninstall, a log.
//   Folders: the Legendary Edition, Archipelago and Mod Manager folders (detected, remembered), and background music:
//            any audio file on the player's PC, looped quietly (the launcher ships no music of its own).
//   Options: the player's options from the world's schema, saved as a YAML into Archipelago's Players folder.
//   Intel:   the game's save briefing and the release notes.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace APSetup;

public partial class MainWindow : Window
{
    sealed class Campaign
    {
        public GameProfile Profile;
        public string Numeral, Tagline;
        public Color Accent;
        public ReleaseInfo Latest;
        public string LatestError;
        public bool Checked;
        public OptionsModel Options;
        public string Preset = "Default";
        public TextBlock CardStatus;
        public Ellipse CardDot;
        public RadioButton Card;
    }

    readonly Campaign[] _campaigns =
    [
        new() { Profile = GameProfile.ME1, Numeral = "I", Accent = Color.FromRgb(0x4F, 0xC3, 0xF7),
                Tagline = "Galaxy-map coordinates, squadmates and gear from every manufacturer scattered across the multiworld." },
        new() { Profile = GameProfile.ME2, Numeral = "II", Accent = Color.FromRgb(0xFF, 0x9F, 0x1C),
                Tagline = "Dossiers, ship upgrades, Reaper IFF intel and loyalty missions shuffled into everyone's games." },
        new() { Profile = GameProfile.ME3, Numeral = "III", Accent = Color.FromRgb(0xFF, 0x5A, 0x4E),
                Tagline = "Crucible intel, war assets and story arcs, gathered from across the multiworld for the final assault." },
    ];

    readonly TextBox _gameDir = new(), _apDir = new(), _modManager = new(), _slot = new() { MaxLength = 16, Width = 180 };
    readonly TextBox _log = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0),
        Background = Brushes.Transparent, FontSize = 12, Padding = new Thickness(0),
    };
    readonly CheckBox _launchClient = new() { IsChecked = true };
    readonly TextBox _musicFile = new() { IsReadOnly = true };
    readonly MediaPlayer _music = new();
    double _musicVolume = 0.35;
    bool _musicOn = true;
    readonly List<CheckBox> _movies = [];
    Campaign _game;
    bool _busy;
    Rectangle _progressFill;
    TextBlock _progressText;
    Grid _progressTrack;
    string _settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MassEffectAPSetup", "launcher.json");

    public MainWindow()
    {
        InitializeComponent();
        _log.FontFamily = (FontFamily)FindResource("Mono");
        _log.Foreground = (Brush)FindResource("TextDim");
        VersionText.Text = "LAUNCHER v" + Program.Version;
        Hex.Fill = Fx.HexGrid(Color.FromRgb(0x9F, 0xC8, 0xFF));
        Scan.Fill = Fx.Scanlines();
        Hex.CacheMode = new BitmapCache();    // static layers: rasterized once, not every star frame
        Scan.CacheMode = new BitmapCache();
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();
        MusicButton.Click += (_, _) => { _musicOn = !_musicOn; PlayMusic(); SaveSettings(); };
        _music.MediaEnded += (_, _) => { _music.Position = TimeSpan.Zero; _music.Play(); };
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        foreach (var tab in new[] { TabDeploy, TabFolders, TabOptions, TabIntel }) tab.Checked += (_, _) => ShowPage();

        LoadSettings(out var lastGame);
        BuildRail();
        BuildLinks();
        Loaded += (_, _) =>
        {
            var sweep = new DoubleAnimation(-200, Math.Max(800, ActualHeight) + 200, TimeSpan.FromSeconds(7)) { RepeatBehavior = RepeatBehavior.Forever };
            SweepMove.BeginAnimation(TranslateTransform.YProperty, sweep);
            _ = CheckReleases();
            PlayMusic();
        };
        Closing += (_, _) => SaveSettings();
        Select(_campaigns.FirstOrDefault(c => c.Profile.Id == lastGame) ?? _campaigns[2]);
        Log($"{Program.AppName} {Program.Version} online.");
    }

    // ------------------------------------------------------------------ settings and folders
    sealed class Settings
    {
        public string GameDir { get; set; }
        public string ApDir { get; set; }
        public string ModManager { get; set; }
        public string Game { get; set; }
        public string Slot { get; set; }
        public string MusicFile { get; set; }
        public double MusicVolume { get; set; } = 0.35;
        public bool MusicOn { get; set; } = true;
    }

    void LoadSettings(out string lastGame)
    {
        lastGame = null;
        try
        {
            if (File.Exists(_settingsFile))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsFile));
                _gameDir.Text = s.GameDir ?? ""; _apDir.Text = s.ApDir ?? ""; _modManager.Text = s.ModManager ?? ""; _slot.Text = s.Slot ?? "";
                lastGame = s.Game;
                _musicFile.Text = s.MusicFile ?? ""; _musicVolume = s.MusicVolume; _musicOn = s.MusicOn;
            }
        }
        catch (Exception) { }   // a damaged settings file: detect again
        DetectPaths();
        if (_slot.Text.Length == 0) _slot.Text = "Shepard";
    }

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile));
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(new Settings
                { GameDir = _gameDir.Text, ApDir = _apDir.Text, ModManager = _modManager.Text, Game = _game?.Profile.Id, Slot = _slot.Text,
                  MusicFile = _musicFile.Text, MusicVolume = _musicVolume, MusicOn = _musicOn }));
        }
        catch (Exception) { }
    }

    void DetectPaths()
    {
        if (!File.Exists(Path.Combine(_gameDir.Text, "Game", "ME1", "Binaries", "Win64", "MassEffect1.exe")) &&
            !GameProfile.All.Any(p => File.Exists(Path.Combine(_gameDir.Text, "Game", p.GameFolder, "Binaries", "Win64", p.Exe))))
        {
            var libraries = new List<string>();
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam)
            {
                libraries.Add(steam);
                var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (var line in File.ReadAllLines(vdf))
                        if (line.Trim().StartsWith("\"path\""))
                            libraries.Add(line.Split('"')[3].Replace(@"\\", @"\"));
            }
            libraries.AddRange([@"C:\Program Files (x86)\Steam", @"C:\Steam", @"D:\Steam", @"D:\SteamLibrary", @"E:\SteamLibrary"]);
            var candidates = libraries.Select(l => Path.GetFullPath(Path.Combine(l, "steamapps", "common", "Mass Effect Legendary Edition")))
                .Append(@"C:\Program Files\EA Games\Mass Effect Legendary Edition");
            _gameDir.Text = candidates.FirstOrDefault(c => GameProfile.All.Any(p => File.Exists(Path.Combine(c, "Game", p.GameFolder, "Binaries", "Win64", p.Exe)))) ?? _gameDir.Text;
        }
        if (!File.Exists(Path.Combine(_apDir.Text, "ArchipelagoLauncher.exe")))
            _apDir.Text = new[] { @"C:\ProgramData\Archipelago", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Archipelago") }
                .FirstOrDefault(p => File.Exists(Path.Combine(p, "ArchipelagoLauncher.exe"))) ?? _apDir.Text;
        if (!File.Exists(_modManager.Text))
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
                if (FindFile(root, "ME3TweaksModManager.exe", 4) is { } found) { _modManager.Text = found; break; }
    }

    static string FindFile(string root, string name, int depth)
    {
        try
        {
            var direct = Path.Combine(root, name);
            if (File.Exists(direct)) return direct;
            if (depth == 0) return null;
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (FindFile(dir, name, depth - 1) is { } hit) return hit;
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return null;
    }

    string Payload => Program.Payload(_game.Profile);
    Installer Make() => new(_game.Profile, _gameDir.Text, _apDir.Text, Payload ?? Path.Combine(Releases.CacheRoot, "none"), Installer.DefaultStateDir(_game.Profile));

    // ------------------------------------------------------------------ music
    // Loops the player's chosen file, fading in; the title-bar button mutes it.
    void PlayMusic()
    {
        var on = _musicOn && File.Exists(_musicFile.Text);
        MusicButton.Content = _musicOn ? "" : "";   // speaker / muted speaker
        MusicButton.Opacity = File.Exists(_musicFile.Text) ? 1 : 0.5;
        if (!on) { _music.Stop(); return; }
        var uri = new Uri(Path.GetFullPath(_musicFile.Text));
        if (_music.Source != uri) _music.Open(uri);
        _music.Volume = 0;
        _music.Play();
        var fade = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        fade.Tick += (_, _) =>
        {
            _music.Volume = Math.Min(_musicVolume, _music.Volume + _musicVolume / 30);
            if (_music.Volume >= _musicVolume) fade.Stop();
        };
        fade.Start();
    }

    // ------------------------------------------------------------------ rail
    void BuildRail()
    {
        foreach (var c in _campaigns)
        {
            var accent = new SolidColorBrush(c.Accent);
            c.CardStatus = new TextBlock { FontSize = 12, Foreground = (Brush)FindResource("TextDim"), Text = "Scanning..." };
            c.CardDot = new Ellipse { Width = 7, Height = 7, Fill = (Brush)FindResource("TextDim"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            var numeral = new TextBlock
            {
                Text = c.Numeral, FontFamily = (FontFamily)FindResource("Display"), FontWeight = FontWeights.Light, FontSize = 44, Width = 62,
                Foreground = accent, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = c.Accent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.8 },
            };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            text.Children.Add(new TextBlock { Text = "MASS EFFECT", FontFamily = (FontFamily)FindResource("Display"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = accent });
            text.Children.Add(new TextBlock { Text = c.Profile.Title.Replace("Mass Effect ", "Legendary Edition "), FontFamily = (FontFamily)FindResource("Display"),
                                              FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Text") });
            var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            status.Children.Add(c.CardDot);
            status.Children.Add(c.CardStatus);
            text.Children.Add(status);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(numeral);
            row.Children.Add(text);

            var face = new Chamfer { Cut = 14, Padding = new Thickness(10, 14, 14, 14), Child = row, Fill = (Brush)FindResource("Panel"), Stroke = (Brush)FindResource("Edge") };
            var card = new RadioButton { GroupName = "Game", Margin = new Thickness(0, 0, 0, 10), Cursor = System.Windows.Input.Cursors.Hand };
            card.Template = CardTemplate();
            card.Content = face;
            card.Checked += (_, _) => Select(c);
            card.MouseEnter += (_, _) => { if (card.IsChecked != true) face.Stroke = accent; };
            card.MouseLeave += (_, _) => { if (card.IsChecked != true) face.Stroke = (Brush)FindResource("Edge"); };
            card.Checked += (_, _) =>
            {
                face.Stroke = accent; face.Ticks = true;
                face.Fill = new LinearGradientBrush(Color.FromArgb(0x55, c.Accent.R, c.Accent.G, c.Accent.B), Color.FromArgb(0x10, c.Accent.R, c.Accent.G, c.Accent.B), 0);
                face.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = c.Accent, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55 };
            };
            card.Unchecked += (_, _) => { face.Stroke = (Brush)FindResource("Edge"); face.Ticks = false; face.Fill = (Brush)FindResource("Panel"); face.Effect = null; };
            c.Card = card;
            GameCards.Children.Add(card);
        }
    }

    static ControlTemplate CardTemplate()
    {
        var template = new ControlTemplate(typeof(RadioButton));
        template.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        return template;
    }

    void BuildLinks()
    {
        Links.Children.Clear();
        void Link(string text, Func<string> url)
        {
            var b = new TextBlock { Text = "›  " + text, FontSize = 13, Margin = new Thickness(0, 0, 0, 6), Cursor = System.Windows.Input.Cursors.Hand,
                                    Foreground = (Brush)FindResource("TextDim") };
            b.MouseEnter += (_, _) => b.Foreground = (Brush)FindResource("Accent");
            b.MouseLeave += (_, _) => b.Foreground = (Brush)FindResource("TextDim");
            b.MouseLeftButtonUp += (_, _) => Open(url());
            Links.Children.Add(b);
        }
        Link("Archipelago", () => "https://archipelago.gg");
        Link("This game's project page", () => Releases.ProjectUrl(Releases.Projects[_game.Profile.Id]));
        Link("Report a problem", () => Releases.ProjectUrl(Releases.Projects[_game.Profile.Id]) + "/issues");
        Link("Launcher project page", () => Releases.ProjectUrl(Releases.LauncherProject));
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception) { }
    }

    // ------------------------------------------------------------------ selection
    public void SelectGame(string id) => Select(_campaigns.First(c => c.Profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));

    public void SelectTab(string name) =>
        (name.ToLowerInvariant() switch { "folders" => TabFolders, "options" => TabOptions, "intel" => TabIntel, _ => TabDeploy }).IsChecked = true;

    void Select(Campaign c)
    {
        if (_game == c) return;
        _game = c;
        if (c.Card.IsChecked != true) c.Card.IsChecked = true;
        var from = ((SolidColorBrush)FindResource("Accent")).Color;
        var step = 0;
        _accentTween?.Stop();
        _accentTween = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _accentTween.Tick += (_, _) =>
        {
            var t = Math.Min(1, ++step / 16.0);
            SetAccent(Color.FromRgb((byte)(from.R + (c.Accent.R - from.R) * t), (byte)(from.G + (c.Accent.G - from.G) * t), (byte)(from.B + (c.Accent.B - from.B) * t)));
            if (t >= 1) _accentTween.Stop();
        };
        _accentTween.Start();
        NebulaStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0x60, c.Accent.R, c.Accent.G, c.Accent.B), TimeSpan.FromMilliseconds(500)));
        Stars.Accent = c.Accent;
        GameKicker.Text = $"CAMPAIGN {c.Numeral}  //  LEGENDARY EDITION";
        Fx.Decrypt(GameTitle, c.Profile.Title.ToUpperInvariant());
        GameTagline.Text = c.Tagline;
        c.Options ??= OptionsModel.For(c.Profile, Payload);
        ShowPage();
    }

    System.Windows.Threading.DispatcherTimer _accentTween;

    // The accent resources (Theme.xaml): replaced, not animated, because brushes used by styles are frozen.
    static void SetAccent(Color color)
    {
        var resources = Application.Current.Resources;
        T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
        resources["Accent"] = Frozen(new SolidColorBrush(color));
        resources["AccentSoft"] = Frozen(new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B)));
        resources["Glow"] = Frozen(new System.Windows.Media.Effects.DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 16, Opacity = 0.85 });
        resources["GlowSoft"] = Frozen(new System.Windows.Media.Effects.DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 10, Opacity = 0.5 });
    }

    void ShowPage()
    {
        if (_game == null || GameTitle == null) return;
        Page.Content = TabFolders.IsChecked == true ? FoldersPage() : TabOptions.IsChecked == true ? OptionsPage() : TabIntel.IsChecked == true ? IntelPage() : DeployPage();
        var move = (TranslateTransform)Page.RenderTransform;
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase() });
        Page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        RefreshCards();
    }

    // ------------------------------------------------------------------ building blocks
    Chamfer Panel(string title, UIElement content, double cut = 14)
    {
        var stack = new DockPanel();
        if (title != null)
        {
            var head = new TextBlock { Text = title, Style = (Style)FindResource("SectionTitle") };
            DockPanel.SetDock(head, Dock.Top);
            stack.Children.Add(head);
        }
        stack.Children.Add(content);
        return new Chamfer { Cut = cut, Fill = (Brush)FindResource("Panel"), Stroke = (Brush)FindResource("Edge"), Ticks = true, Padding = new Thickness(18, 14, 18, 16), Child = stack };
    }

    Button Btn(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, Style = (Style)FindResource(primary ? "Primary" : "Holo") };
        b.Click += (_, _) => click();
        return b;
    }

    TextBlock Dim(string text, double size = 13) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("TextDim") };

    // ------------------------------------------------------------------ deploy
    UIElement DeployPage()
    {
        var installer = Make();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var status = StatusPanel(installer);
        grid.Children.Add(status);

        var release = ReleasePanel(installer);
        Grid.SetColumn(release, 2);
        grid.Children.Add(release);

        (_log.Parent as Panel)?.Children.Remove(_log);
        var console = Panel("COMMS LOG", _log);
        Grid.SetRow(console, 2);
        Grid.SetColumnSpan(console, 3);
        grid.Children.Add(console);
        return grid;
    }

    UIElement StatusPanel(Installer i)
    {
        var rows = new Grid();
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var payload = Payload != null;
        void Row(string label, string value, string level)
        {
            int r = rows.RowDefinitions.Count;
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var color = (Brush)FindResource(level);
            var dot = new Ellipse { Width = 8, Height = 8, Fill = color, VerticalAlignment = VerticalAlignment.Center,
                                    Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = ((SolidColorBrush)color).Color, BlurRadius = 8, ShadowDepth = 0 } };
            if (level != "Ok")
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            var l = new TextBlock { Text = label, FontSize = 13, Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(0, 4, 8, 4) };
            var v = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(dot, r); Grid.SetRow(l, r); Grid.SetRow(v, r);
            Grid.SetColumn(l, 1); Grid.SetColumn(v, 2);
            rows.Children.Add(dot); rows.Children.Add(l); rows.Children.Add(v);
        }
        string Level(string s) => s.StartsWith("NOT") || s.StartsWith("MISSING") ? "Bad"
            : s.StartsWith("not installed") || s.Contains("different") || s.Contains("download:") ? "Warn" : "Ok";
        void Line(string label, string value) => Row(label, value, Level(value));

        Line($"{_game.Profile.Title} LE", i.GameFound ? "found" : "NOT FOUND (set it under Folders)");
        Line("Archipelago", i.ArchipelagoFound ? "found" : "NOT FOUND (set it under Folders)");
        if (i.GameFound) Line("ASI loader", payload || _game.Profile.Loader == LoaderMode.RequireExisting ? i.LoaderState() : File.Exists(i.BinkOriginal) ? "installed" : "not installed");
        foreach (var (_, name) in _game.Profile.RequiredAsis)
            if (i.GameFound) Line(name, i.MissingRequirements().Contains(name) ? "MISSING (Mod Manager > ASI Manager)" : "installed");
        if (i.ArchipelagoFound) Line("Archipelago world", payload ? i.State(Path.Combine(i.Payload, _game.Profile.Apworld), i.WorldTarget) : File.Exists(i.WorldTarget) ? "installed" : "not installed");
        if (i.GameFound)
        {
            foreach (var asi in _game.Profile.AsiFiles.Where(a => a.EndsWith(".asi")))
                Line("Game bridge", payload ? i.State(Path.Combine(i.Payload, "ASI", asi), Path.Combine(i.AsiDir, asi)) : File.Exists(Path.Combine(i.AsiDir, asi)) ? "installed" : "not installed");
            var mod = i.ModVersionInstalled();
            Line("Mod", mod == null ? "not installed" : mod == "installed" ? "installed" : "installed, version " + mod + (payload && i.ModVersionPayload() is { } v && v != mod ? $" (download: {v})" : ""));
        }
        Row("Uninstall record", i.Installed ? "yes: Uninstall restores your original files" : "none", i.Installed ? "Ok" : "Warn");

        var stack = new StackPanel();
        stack.Children.Add(rows);
        _movies.Clear();
        if (_game.Profile.OptionalMovies.Length > 0)
        {
            stack.Children.Add(new TextBlock { Text = "INSTALL CHOICES", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 14, 0, 6) });
            foreach (var (file, label) in _game.Profile.OptionalMovies)
            {
                var box = new CheckBox { Content = label, Tag = file, IsChecked = true, Margin = new Thickness(0, 3, 0, 3), ToolTip = file };
                _movies.Add(box);
                stack.Children.Add(box);
            }
        }
        return Panel("SYSTEM STATUS", stack);
    }

    UIElement ReleasePanel(Installer installer)
    {
        var stack = new StackPanel();
        void Pair(string label, string value, Brush color = null)
        {
            stack.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
            stack.Children.Add(new TextBlock { Text = value, FontSize = 15, Margin = new Thickness(0, 1, 0, 10), TextWrapping = TextWrapping.Wrap,
                                               Foreground = color ?? (Brush)FindResource("Text"), FontFamily = (FontFamily)FindResource("Display") });
        }
        var latest = _game.Latest;
        Pair("LATEST ON GITHUB", !_game.Checked ? "Contacting GitHub..." : latest != null ? $"{latest.Tag}  ·  {latest.Released:d MMM yyyy}"
                                 : _game.LatestError ?? "No release published yet");
        var payloadTag = PayloadTag();
        Pair("ON THIS PC", payloadTag ?? "Not downloaded");
        var update = UpdateAvailable();
        if (update) Pair("STATUS", $"Update available: {latest.Tag}", (Brush)FindResource("Warn"));

        var action = Payload == null && latest == null ? "NO RELEASE YET" : Payload == null || update ? (installer.Installed || installer.ModVersionInstalled() != null ? "UPDATE" : "DOWNLOAD + INSTALL") : "INSTALL / REPAIR";
        // the step the player needs next is the bright button: Install/Update until the game is ready, then Launch
        var installed = installer.ModVersionInstalled() != null;
        var needsInstall = !installed || update || (Payload != null && installer.GameFound && installer.ArchipelagoFound && installer.LaunchProblems().Count > 0);
        var install = Btn(action, () => _ = InstallAsync(), primary: needsInstall);
        install.IsEnabled = !_busy && (Payload != null || latest != null);
        install.HorizontalAlignment = HorizontalAlignment.Stretch;
        install.Margin = new Thickness(0, 4, 0, 10);
        stack.Children.Add(install);

        var launch = Btn($"LAUNCH {_game.Profile.Title.ToUpperInvariant()}", Launch, primary: !needsInstall);
        launch.IsEnabled = !_busy && (Payload != null || installer.ModVersionInstalled() != null);
        launch.HorizontalAlignment = HorizontalAlignment.Stretch;
        launch.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(launch);
        _launchClient.Content = $"Also start the {_game.Profile.Client}";
        _launchClient.Margin = new Thickness(0, 2, 0, 12);
        (_launchClient.Parent as Panel)?.Children.Remove(_launchClient);
        stack.Children.Add(_launchClient);

        // progress bar: an accent fill with a shimmer running over it
        _progressTrack = new Grid { Height = 6, Margin = new Thickness(0, 0, 0, 4), Visibility = _busy ? Visibility.Visible : Visibility.Collapsed };
        _progressTrack.Children.Add(new Rectangle { Fill = (Brush)FindResource("Edge") });
        _progressFill = new Rectangle { Fill = (Brush)FindResource("Accent"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _progressFill.SetResourceReference(EffectProperty, "GlowSoft");
        _progressTrack.Children.Add(_progressFill);
        var shimmer = new Rectangle { Width = 80, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = new TranslateTransform(),
            Fill = new LinearGradientBrush(new GradientStopCollection { new(Color.FromArgb(0, 255, 255, 255), 0), new(Color.FromArgb(150, 255, 255, 255), 0.5), new(Color.FromArgb(0, 255, 255, 255), 1) }, 0) };
        shimmer.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-80, 420, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
        _progressTrack.Children.Add(shimmer);
        _progressTrack.ClipToBounds = true;
        stack.Children.Add(_progressTrack);
        _progressText = new TextBlock { FontSize = 12, Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(0, 0, 0, 8), FontFamily = (FontFamily)FindResource("Mono") };
        stack.Children.Add(_progressText);

        var tools = new WrapPanel();
        var uninstall = Btn("Uninstall", Uninstall);
        uninstall.IsEnabled = !_busy && installer.Installed;
        tools.Children.Add(uninstall);
        tools.Children.Add(Btn("Game folder", () => { if (installer.GameFound) Open(installer.Root); }));
        tools.Children.Add(Btn("AP Players", () => { if (installer.ArchipelagoFound) { Directory.CreateDirectory(Path.Combine(_apDir.Text, "Players")); Open(Path.Combine(_apDir.Text, "Players")); } }));
        stack.Children.Add(tools);
        return Panel("RELEASE CHANNEL", stack);
    }

    string PayloadTag()
    {
        var p = Payload;
        if (p == null) return null;
        var tagFile = Path.Combine(p, ".release");
        return File.Exists(tagFile) ? File.ReadAllText(tagFile).Trim() : "bundled files (next to the launcher)";
    }

    bool UpdateAvailable()
    {
        if (_game.Latest == null) return false;
        var p = Payload;
        if (p == null) return true;
        var tagFile = Path.Combine(p, ".release");
        return File.Exists(tagFile) && Releases.CompareVersions(_game.Latest.Tag, File.ReadAllText(tagFile).Trim()) > 0;
    }

    void RefreshCards()
    {
        foreach (var c in _campaigns)
        {
            var p = Program.Payload(c.Profile);
            var installer = new Installer(c.Profile, _gameDir.Text, _apDir.Text, p ?? Path.Combine(Releases.CacheRoot, "none"), Installer.DefaultStateDir(c.Profile));
            string text; string level;
            var mod = installer.GameFound ? installer.ModVersionInstalled() : null;
            var tag = p != null && File.Exists(Path.Combine(p, ".release")) ? File.ReadAllText(Path.Combine(p, ".release")).Trim() : null;
            bool update = c.Latest != null && (p == null || tag != null && Releases.CompareVersions(c.Latest.Tag, tag) > 0);
            if (!installer.GameFound) { text = "Game not found"; level = "Bad"; }
            else if (mod == null) { text = c.Latest != null ? $"Ready to install {c.Latest.Tag}" : c.Checked ? "No release yet" : "Not installed"; level = "Warn"; }
            else if (update && mod != null) { text = $"Update: {c.Latest.Tag}"; level = "Warn"; }
            else { text = mod == "installed" ? "Installed" : $"Installed · mod {mod}"; level = "Ok"; }
            c.CardStatus.Text = text;
            c.CardDot.Fill = (Brush)FindResource(level);
        }
        Footer.Text = $"LE: {(_gameDir.Text.Length > 0 ? _gameDir.Text : "not set")}   ·   AP: {(_apDir.Text.Length > 0 ? _apDir.Text : "not set")}";
    }

    void Log(string line)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
        _log.ScrollToEnd();
    }

    void Progress(double fraction, string text)
    {
        if (_progressTrack == null) return;
        _progressTrack.Visibility = Visibility.Visible;
        _progressFill.Width = Math.Max(0, _progressTrack.ActualWidth * fraction);
        _progressText.Text = $"{text}  {fraction:P0}";
    }

    async Task CheckReleases()
    {
        await Releases.ResolveProjects();
        await Task.WhenAll(_campaigns.Select(async c =>
        {
            try { c.Latest = await Releases.Latest(Releases.Projects[c.Profile.Id]); }
            catch (Exception ex) { c.LatestError = "GitHub unreachable: " + ex.Message; }
            c.Checked = true;
            Log(c.Latest != null ? $"{c.Profile.Title}: latest release {c.Latest.Tag}." : $"{c.Profile.Title}: {c.LatestError ?? "no release published yet"}.");
        }));
        try
        {
            var launcher = await Releases.Latest(Releases.LauncherProject);
            if (launcher != null && Releases.CompareVersions(launcher.Tag, Program.Version) > 0)
            {
                LauncherUpdate.Content = $"LAUNCHER {launcher.Tag} AVAILABLE";
                LauncherUpdate.Visibility = Visibility.Visible;
                LauncherUpdate.Click += (_, _) => Open(launcher.WebUrl);
                Log($"A newer launcher is available: {launcher.Tag}.");
            }
        }
        catch (Exception) { }
        if (!_busy) ShowPage();
    }

    // ------------------------------------------------------------------ actions
    bool GameRunning()
    {
        if (Process.GetProcessesByName(_game.Profile.ProcessName).Length == 0) return false;
        Warn($"Close {_game.Profile.Title} first.");
        return true;
    }

    void Warn(string text)
    {
        Log(text.Replace("\r\n", " "));
        MessageBox.Show(this, text, Program.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    async Task InstallAsync()
    {
        if (_busy || GameRunning()) return;
        var game = _game;
        _busy = true;
        ShowPage();
        try
        {
            var payload = Payload;
            if ((payload == null || UpdateAvailable()) && game.Latest != null)
            {
                Log($"Downloading {game.Profile.Title} {game.Latest.Tag} from GitHub...");
                var progress = new Progress<(double, string)>(p => { if (_game == game) Progress(p.Item1, p.Item2); });
                payload = await Releases.Fetch(game.Profile, game.Latest, progress, line => Dispatcher.Invoke(() => Log(line)));
                game.Options = OptionsModel.For(game.Profile, payload);
            }
            if (payload == null) throw new InvalidOperationException("There is nothing to install yet: the game has no release on GitHub.");
            var options = new Installer.Options();
            foreach (var box in _movies)
                if (box.IsChecked != true) options.KeepMovies.Add((string)box.Tag);
            var installer = new Installer(game.Profile, _gameDir.Text, _apDir.Text, payload, Installer.DefaultStateDir(game.Profile));
            Log($"Installing {game.Profile.Title}...");
            if (_game == game) Progress(1, "Installing");
            await Task.Run(() => installer.Install(options));
            installer.Log.ForEach(Log);
            Log("Install complete.");
        }
        catch (Exception ex) { Warn(ex.Message); }
        finally
        {
            _busy = false;
            ShowPage();
        }
    }

    void Uninstall()
    {
        if (_busy || GameRunning()) return;
        if (MessageBox.Show(this, $"Remove the {_game.Profile.Title} world, bridge and mod and put back every file they replaced?", Program.AppName,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var installer = Make();
            installer.Uninstall();
            installer.Log.ForEach(Log);
        }
        catch (Exception ex) { Warn(ex.Message); }
        ShowPage();
    }

    void Launch()
    {
        try { LaunchGame(); }
        catch (Exception ex) { Warn(ex.Message); }
    }

    void LaunchGame()
    {
        if (_busy || GameRunning()) return;
        var installer = Make();
        var problems = Payload != null ? installer.LaunchProblems() : InstalledProblems(installer);
        if (problems.Count > 0) { Warn("Not ready to launch:\r\n\r\n" + string.Join("\r\n", problems)); return; }
        // Started directly; the Legendary Edition launcher may still open: pick the game there. Steam must be running.
        Process.Start(new ProcessStartInfo(installer.GameExe) { WorkingDirectory = installer.Win64, UseShellExecute = true });
        Log($"Launching {_game.Profile.Title}.");
        if (_launchClient.IsChecked == true)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(_apDir.Text, "ArchipelagoLauncher.exe"), $"\"{_game.Profile.Client}\"")
                { WorkingDirectory = _apDir.Text, UseShellExecute = false });
            Log($"Starting the {_game.Profile.Client}.");
        }
    }

    // Without a payload to compare against (installed earlier, nothing downloaded): only check that the parts are there.
    List<string> InstalledProblems(Installer i)
    {
        var problems = new List<string>();
        if (!i.GameFound) problems.Add("Mass Effect Legendary Edition not found (set its folder under Folders).");
        if (!i.ArchipelagoFound) problems.Add("Archipelago not found (set its folder under Folders).");
        if (problems.Count > 0) return problems;
        if (!File.Exists(i.WorldTarget)) problems.Add("The Archipelago world is not installed.");
        foreach (var asi in _game.Profile.AsiFiles)
            if (!File.Exists(Path.Combine(i.AsiDir, asi))) problems.Add($"{asi} is not installed.");
        if (i.ModVersionInstalled() == null) problems.Add("The mod is not installed.");
        if (!File.Exists(i.BinkOriginal)) problems.Add("The ASI loader is not installed.");
        foreach (var missing in i.MissingRequirements()) problems.Add($"{missing} is missing: {i.LoaderState()}.");
        return problems;
    }

    // ------------------------------------------------------------------ folders
    UIElement FoldersPage()
    {
        var stack = new StackPanel();
        stack.Children.Add(Dim("Detected automatically; Browse to change. These are shared by all three games and remembered.\n" +
            (_game.Profile.Loader == LoaderMode.RequireExisting
                ? "ME3Tweaks Mod Manager is needed once for Mass Effect 1, for its ASI Manager (the ASI loader and LE1 Autoload Enabler). The launcher installs the mod itself."
                : $"ME3Tweaks Mod Manager is optional for {_game.Profile.Title}: the launcher installs the mod and the ASI loader itself.")));
        void Row(string label, string hint, TextBox box, bool folder, Func<string, bool> ok)
        {
            stack.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 18, 0, 2) });
            stack.Children.Add(Dim(hint, 12));
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var light = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            void Update() => light.Fill = (Brush)FindResource(ok(box.Text) ? "Ok" : "Bad");
            Update();
            box.TextChanged += (_, _) => Update();
            var browse = Btn("Browse", () =>
            {
                if (folder)
                {
                    var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(box.Text) ? box.Text : null };
                    if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
                }
                else
                {
                    var dialog = new OpenFileDialog { Filter = "ME3TweaksModManager.exe|ME3TweaksModManager.exe" };
                    if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName;
                }
                SaveSettings();
                RefreshCards();
            });
            browse.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(light, Dock.Left);
            DockPanel.SetDock(browse, Dock.Right);
            (box.Parent as Panel)?.Children.Remove(box);
            row.Children.Add(light);
            row.Children.Add(browse);
            row.Children.Add(box);
            stack.Children.Add(row);
        }
        Row("MASS EFFECT LEGENDARY EDITION", "The folder that contains Game\\ME1, Game\\ME2 and Game\\ME3.", _gameDir, true,
            d => GameProfile.All.Any(p => File.Exists(Path.Combine(d, "Game", p.GameFolder, "Binaries", "Win64", p.Exe))));
        Row("ARCHIPELAGO", "The folder with ArchipelagoLauncher.exe (0.6.8 or newer).", _apDir, true, d => File.Exists(Path.Combine(d, "ArchipelagoLauncher.exe")));
        Row("ME3TWEAKS MOD MANAGER", "ME3TweaksModManager.exe (only needed for Mass Effect 1's one-time ASI step).", _modManager, false, File.Exists);
        stack.Children.Add(new TextBlock { Text = "BACKGROUND MUSIC", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 18, 0, 2) });
        stack.Children.Add(Dim("Any audio file on this PC (mp3, wma, wav), looped quietly while the launcher is open. The title-bar speaker mutes it.", 12));
        var musicRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var pick = Btn("Browse", () =>
        {
            var dialog = new OpenFileDialog { Filter = "Audio|*.mp3;*.wma;*.wav;*.m4a;*.aac|All files|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            _musicFile.Text = dialog.FileName;
            _musicOn = true;
            PlayMusic();
            SaveSettings();
        });
        var clear = Btn("Clear", () => { _musicFile.Text = ""; PlayMusic(); SaveSettings(); });
        pick.Margin = new Thickness(10, 0, 0, 0);
        clear.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(clear, Dock.Right);
        DockPanel.SetDock(pick, Dock.Right);
        (_musicFile.Parent as Panel)?.Children.Remove(_musicFile);
        musicRow.Children.Add(clear);
        musicRow.Children.Add(pick);
        musicRow.Children.Add(_musicFile);
        stack.Children.Add(musicRow);
        var volume = new Slider { Minimum = 0, Maximum = 100, Value = _musicVolume * 100, IsSnapToTickEnabled = false, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        volume.ValueChanged += (_, _) => { _musicVolume = volume.Value / 100; _music.Volume = _musicVolume; };
        volume.PreviewMouseUp += (_, _) => SaveSettings();
        var volumeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        volumeRow.Children.Add(new TextBlock { Text = "VOLUME", Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        volumeRow.Children.Add(volume);
        stack.Children.Add(volumeRow);
        var again = Btn("Detect again", () => { _gameDir.Text = _apDir.Text = _modManager.Text = ""; DetectPaths(); SaveSettings(); RefreshCards(); });
        again.Margin = new Thickness(0, 22, 0, 0);
        again.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(again);
        return Panel("FOLDERS", new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    // ------------------------------------------------------------------ options
    UIElement OptionsPage()
    {
        var model = _game.Options;
        var codexTitle = new TextBlock { FontFamily = (FontFamily)FindResource("Display"), FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var codexKey = new TextBlock { FontFamily = (FontFamily)FindResource("Mono"), FontSize = 11, Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(0, 2, 0, 10) };
        var codexBody = Dim(_game.Profile.SaveNote);
        codexTitle.Text = "Briefing";
        void Codex(OptionDef def) { codexTitle.Text = def.Display; codexKey.Text = def.Key; codexBody.Text = def.Doc; }

        // top bar
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        bar.Children.Add(new TextBlock { Text = "SLOT", Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        (_slot.Parent as Panel)?.Children.Remove(_slot);
        _slot.Margin = new Thickness(0, 0, 18, 0);
        bar.Children.Add(_slot);
        bar.Children.Add(new TextBlock { Text = "PRESET", Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var preset = new ComboBox { ItemsSource = model.Presets, SelectedItem = _game.Preset, Margin = new Thickness(0, 0, 18, 0) };
        bar.Children.Add(preset);
        bar.Children.Add(Btn("Save to Players", () => SaveYaml(null)));
        bar.Children.Add(Btn("Save as...", () =>
        {
            var dialog = new SaveFileDialog { Filter = "YAML|*.yaml", FileName = OptionsModel.SafeName(_slot.Text) + ".yaml" };
            if (dialog.ShowDialog(this) == true) SaveYaml(dialog.FileName);
        }));
        bar.Children.Add(Btn("Save + generate seed", Generate));

        // groups | options | codex
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        var groups = new StackPanel();
        var rows = new StackPanel { Margin = new Thickness(16, 0, 8, 0) };
        var scroller = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroller, 1);

        void ShowGroup(string group)
        {
            rows.Children.Clear();
            foreach (var def in model.Options.Where(o => o.Group == group)) rows.Children.Add(OptionRow(model, def, Codex));
            scroller.ScrollToTop();
        }
        string first = null;
        foreach (var group in model.Groups)
        {
            first ??= group;
            var tab = new RadioButton { Content = group, Style = (Style)FindResource("SideTab"), GroupName = "OptGroup", IsChecked = group == first };
            tab.Checked += (_, _) => ShowGroup(group);
            groups.Children.Add(tab);
        }
        ShowGroup(first);
        preset.SelectionChanged += (_, _) =>
        {
            _game.Preset = (string)preset.SelectedItem;
            model.ApplyPreset(_game.Preset);
            ShowGroup(groups.Children.OfType<RadioButton>().First(t => t.IsChecked == true).Content as string);
            Log($"Preset {_game.Preset} applied.");
        };

        var codex = new StackPanel();
        codex.Children.Add(codexTitle);
        codex.Children.Add(codexKey);
        codex.Children.Add(codexBody);
        var codexPanel = Panel("CODEX", new ScrollViewer { Content = codex, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(codexPanel, 3);
        body.Children.Add(groups);
        body.Children.Add(scroller);
        body.Children.Add(codexPanel);

        var page = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        page.Children.Add(bar);
        var worldNote = Dim($"{model.Game}, world {model.WorldVersion}. Click an option to read about it.", 12);
        worldNote.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(worldNote, Dock.Top);
        page.Children.Add(worldNote);
        page.Children.Add(body);
        return page;
    }

    UIElement OptionRow(OptionsModel model, OptionDef def, Action<OptionDef> codex)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2), Background = Brushes.Transparent };
        var label = new TextBlock { Text = def.Display, Width = 190, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 12, 8) };
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        UIElement input;
        switch (def.Type)
        {
            case "toggle":
            {
                var box = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = (bool)model.Values[def.Key], VerticalAlignment = VerticalAlignment.Center };
                box.Click += (_, _) => { model.Values[def.Key] = box.IsChecked == true; codex(def); };
                input = box;
                break;
            }
            case "choice":
            {
                var combo = new ComboBox { ItemsSource = def.Choices, SelectedItem = model.Values[def.Key], HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, MinWidth = 140, MaxWidth = 280 };
                combo.SelectionChanged += (_, _) => { model.Values[def.Key] = combo.SelectedItem; codex(def); };
                combo.DropDownOpened += (_, _) => codex(def);
                input = combo;
                break;
            }
            case "range":
            {
                var value = new TextBlock { Width = 44, FontFamily = (FontFamily)FindResource("Mono"), FontSize = 14, Foreground = (Brush)FindResource("Accent"), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
                var slider = new Slider { Minimum = def.Min, Maximum = def.Max, Value = (int)model.Values[def.Key], VerticalAlignment = VerticalAlignment.Center, MaxWidth = 340 };
                value.Text = ((int)slider.Value).ToString();
                slider.ValueChanged += (_, _) => { model.Values[def.Key] = (int)slider.Value; value.Text = ((int)slider.Value).ToString(); codex(def); };
                var panel = new DockPanel();
                DockPanel.SetDock(value, Dock.Right);
                panel.Children.Add(value);
                panel.Children.Add(slider);
                input = panel;
                break;
            }
            default:
            {
                var set = (HashSet<string>)model.Values[def.Key];
                var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 2) };
                foreach (var choice in def.Choices)
                {
                    var chip = new CheckBox { Content = choice, Style = (Style)FindResource("Chip"), IsChecked = set.Contains(choice) };
                    chip.Click += (_, _) => { if (chip.IsChecked == true) set.Add(choice); else set.Remove(choice); codex(def); };
                    wrap.Children.Add(chip);
                }
                input = wrap;
                break;
            }
        }
        row.Children.Add(input);
        row.MouseEnter += (_, _) => { row.Background = (Brush)FindResource("AccentSoft"); codex(def); };
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    bool SaveYaml(string path)
    {
        if (path == null)
        {
            if (!File.Exists(Path.Combine(_apDir.Text, "ArchipelagoLauncher.exe"))) { Warn("Set the Archipelago folder under Folders, or use Save as."); return false; }
            var players = Path.Combine(_apDir.Text, "Players");
            Directory.CreateDirectory(players);
            path = Path.Combine(players, OptionsModel.SafeName(_slot.Text) + ".yaml");
            if (File.Exists(path) && MessageBox.Show(this, $"{path} exists. Replace it?", Program.AppName, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return false;
        }
        File.WriteAllText(path, _game.Options.BuildYaml(_slot.Text, _game.Preset, Program.AppName, Program.Version), new UTF8Encoding(false));
        Log($"Saved {path}");
        SaveSettings();
        return true;
    }

    void Generate()
    {
        try
        {
            var generator = Path.Combine(_apDir.Text, "ArchipelagoGenerate.exe");
            if (!File.Exists(generator)) throw new InvalidOperationException("ArchipelagoGenerate.exe not found: set the Archipelago folder under Folders.");
            if (!SaveYaml(null)) return;
            var players = Path.Combine(_apDir.Text, "Players");
            var yamls = Directory.GetFiles(players, "*.yaml").Select(Path.GetFileName).ToArray();
            if (MessageBox.Show(this, $"Generate a seed from every YAML in {players}?\r\n\r\n{string.Join("\r\n", yamls)}\r\n\r\n" +
                    "Then upload the AP_*.zip from the output folder at archipelago.gg/uploads to host it.", Program.AppName, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            using var run = Process.Start(new ProcessStartInfo(generator) { WorkingDirectory = _apDir.Text, UseShellExecute = true });
            run.WaitForExit();
            Open(Path.Combine(_apDir.Text, "output"));
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    // ------------------------------------------------------------------ intel
    UIElement IntelPage()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });

        var briefing = new StackPanel();
        briefing.Children.Add(Dim(_game.Profile.SaveNote, 14));
        briefing.Children.Add(new TextBlock { Text = "HOW TO PLAY", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 18, 0, 6) });
        briefing.Children.Add(Dim(
            $"1. Deploy: download and install {_game.Profile.Title}.\n" +
            "2. Options: pick a preset or set options, then Save to Players.\n" +
            "3. Give the YAML to whoever generates the seed (or Save + generate seed and host it on archipelago.gg).\n" +
            $"4. Launch the game with the {_game.Profile.Client} and connect to the room.", 14));
        grid.Children.Add(Panel("BRIEFING", new ScrollViewer { Content = briefing, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));

        var notes = new StackPanel();
        var latest = _game.Latest;
        notes.Children.Add(new TextBlock { Text = latest != null ? (latest.Name ?? latest.Tag) : "No release notes", FontFamily = (FontFamily)FindResource("Display"),
                                           FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        notes.Children.Add(Dim(latest != null ? Releases.PlainNotes(latest.Notes) : _game.LatestError ?? "This game has no release on GitHub yet.", 13));
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (latest != null) buttons.Children.Add(Btn("Open release page", () => Open(latest.WebUrl)));
        buttons.Children.Add(Btn("All releases", () => Open(Releases.ProjectUrl(Releases.Projects[_game.Profile.Id]) + "/releases")));
        notes.Children.Add(buttons);
        var notesPanel = Panel("RELEASE NOTES", new ScrollViewer { Content = notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(notesPanel, 2);
        grid.Children.Add(notesPanel);
        return grid;
    }
}
