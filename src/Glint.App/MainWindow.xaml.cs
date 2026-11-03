using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Glint.App.Services;
using Glint.Core;
using Forms = System.Windows.Forms;

namespace Glint.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glint"));
    private readonly ApplicationCatalog catalog = new();
    private readonly IconService icons = new();
    private UserSettings settings = new();
    private SearchEngine engine = new([]);
    private AppEntry[] indexedApps = [];
    private int aliasesVersion;
    private CatalogMonitor? catalogMonitor;
    private bool resultMenuOpen;
    private HotkeyService? hotkey;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private bool refreshing;
    private bool refreshPending;
    private bool composing;
    private bool interaction;
    private bool dialogOpen;
    private bool exiting;
    private int appCount;
    private IntPtr handle;

    public MainWindow()
    {
        InitializeComponent();
        TextCompositionManager.AddPreviewTextInputStartHandler(SearchBox, (_, _) => composing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(SearchBox, (_, _) => composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(SearchBox, (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => composing = false));
        Closing += OnClosing;
        Closed += (_, _) => { catalogMonitor?.Dispose(); hotkey?.Dispose(); tray?.Dispose(); trayIcon?.Dispose(); };
    }

    public void InitializeLauncher(bool background)
    {
        try { settings = store.Load(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            try
            {
                if (File.Exists(store.FilePath)) File.Copy(store.FilePath, store.FilePath + ".backup-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                ShowNotice("设置无法读取，已保留原文件并使用默认设置。");
            }
            catch (Exception backupException) when (backupException is IOException or UnauthorizedAccessException)
            { ShowNotice("设置无法读取或备份，请检查设置目录权限。"); }
        }
        handle = new WindowInteropHelper(this).EnsureHandle();
        hotkey = new HotkeyService(handle);
        hotkey.Pressed += () => { if (IsVisible && IsActive) Hide(); else ShowLauncher(); };
        var registered = hotkey.TryRegister(settings.Hotkey);
        if (!registered) ShowNotice($"{settings.Hotkey} 已被占用。请在设置中切换快捷键，或从托盘打开轻启。");
        HotkeyLabel.Text = registered ? settings.Hotkey + " 唤出" : "快捷键被占用";
        CreateTray();
        catalogMonitor = new CatalogMonitor();
        catalogMonitor.Changed += () =>
        {
            try
            {
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(() => { if (!exiting) _ = RefreshIndexAsync(); });
            }
            catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted) { }
        };
        if (!background || !registered) ShowLauncher();
        _ = RefreshIndexAsync();
    }

    private void CreateTray()
    {
        using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/glint.ico"))!.Stream;
        using var sourceIcon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize);
        trayIcon = (System.Drawing.Icon)sourceIcon.Clone();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开轻启", null, (_, _) => Dispatcher.Invoke(ShowLauncher));
        menu.Items.Add("刷新应用列表", null, (_, _) => Dispatcher.Invoke(() => { _ = RefreshIndexAsync(); }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("作者 GitHub · VieVibe", null, (_, _) => Dispatcher.Invoke(() => OpenGitHub("https://github.com/VieVibe")));
        menu.Items.Add("项目主页与反馈", null, (_, _) => Dispatcher.Invoke(() => OpenGitHub("https://github.com/VieVibe/Glint")));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出轻启", null, (_, _) => Dispatcher.Invoke(ExitLauncher));
        tray = new Forms.NotifyIcon
        {
            Text = "轻启 Glint · " + settings.Hotkey,
            Icon = trayIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ShowLauncher); };
    }

    public void ShowLauncher()
    {
        if (exiting || dialogOpen) return;
        composing = false;
        SearchBox.Clear();
        UpdateResults();
        Show();
        PositionOnActiveScreen();
        Activate();
        SetForegroundWindow(handle);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { SearchBox.Focus(); Keyboard.Focus(SearchBox); });
    }

    private void PositionOnActiveScreen()
    {
        var area = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        SetWindowPos(handle, IntPtr.Zero, area.Left + 20, area.Top + 20, 0, 0, 0x0015);
        var bounds = LauncherPlacement.Fit(new(area.Left, area.Top, area.Width, area.Height), GetDpiForWindow(handle) / 96.0);
        SetWindowPos(handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0014);
    }

    private async Task RefreshIndexAsync()
    {
        if (exiting) return;
        if (refreshing) { refreshPending = true; return; }
        refreshing = true;
        CountLabel.Text = "正在更新应用…";
        UpdateResults(preserveSelection: true);
        try
        {
            var result = await catalog.ScanAsync(settings.PortableApps);
            // Snapshot preferences on the UI thread; the background build must not race an alias edit.
            var aliases = settings.Aliases.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var version = aliasesVersion;
            var nextEngine = await Task.Run(() => new SearchEngine(result.Apps, aliases));
            if (exiting) return;
            indexedApps = result.Apps;
            engine = version == aliasesVersion ? nextEngine : new SearchEngine(indexedApps, settings.Aliases);
            icons.Clear();
            appCount = result.Apps.Length;
            if (result.Warnings.Length > 0) ShowNotice(string.Join("；", result.Warnings));
        }
        catch (Exception exception)
        {
            ShowNotice("应用列表更新失败：" + exception.Message);
        }
        finally
        {
            refreshing = false;
            if (!exiting) UpdateResults(preserveSelection: true);
            if (refreshPending && !exiting) { refreshPending = false; _ = RefreshIndexAsync(); }
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (ResultsList is not null) UpdateResults();
    }

    private void UpdateResults(bool preserveSelection = false)
    {
        var selectedId = preserveSelection ? (ResultsList.SelectedItem as ResultRow)?.Entry.Id : null;
        var results = engine.Search(SearchBox.Text, settings.Usage, pinned: settings.PinnedApps);
        var rows = results.Select((app, index) => new ResultRow(app, index + 1,
            results.Count(other => other.Name.Equals(app.Name, StringComparison.CurrentCultureIgnoreCase)) > 1,
            settings.PinnedApps.Contains(app.Id), settings.Aliases.GetValueOrDefault(app.Id) ?? [])).ToArray();
        ResultsList.ItemsSource = rows;
        var selectedIndex = Array.FindIndex(rows, row => row.Entry.Id == selectedId);
        ResultsList.SelectedIndex = selectedIndex >= 0 ? selectedIndex : rows.Length > 0 ? 0 : -1;
        if (ResultsList.SelectedItem is not null) ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        SectionLabel.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? settings.PinnedApps.Count > 0 ? "固定与常用" : "常用应用" : "搜索结果";
        CountLabel.Text = refreshing ? "正在更新应用…" : $"已收录 {appCount} 个应用";
        EmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = refreshing ? "正在收集你的应用" : "没有找到应用";
        EmptyHint.Text = refreshing ? "首次打开需要一点时间，之后搜索会即时响应" : "换个名称，或在设置中添加便携应用";
        foreach (var row in rows) _ = LoadIconAsync(row);
    }

    private async Task LoadIconAsync(ResultRow row)
    {
        try { row.Icon = await icons.GetAsync(row.Entry.IconPath); }
        catch (Exception exception) when (exception is ArgumentException or ExternalException or IOException) { }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (composing || e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (SettingsMenu.IsOpen || resultMenuOpen) return;
        if (key == Key.Escape) { Hide(); e.Handled = true; return; }
        if (key == Key.F5) { _ = RefreshIndexAsync(); e.Handled = true; return; }
        if (key == Key.F2) { OnSettingsClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (!SearchBox.IsKeyboardFocusWithin && !ResultsList.IsKeyboardFocusWithin) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.D)
        { TogglePin(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.E)
        { EditAliases(); e.Handled = true; return; }
        if (key is Key.Down or Key.Up)
        {
            var count = ResultsList.Items.Count;
            if (count > 0)
            {
                var delta = key == Key.Down ? 1 : -1;
                ResultsList.SelectedIndex = (ResultsList.SelectedIndex + delta + count) % count;
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            }
            e.Handled = true;
        }
        else if (key == Key.Enter)
        {
            if (ResultsList.SelectedItem is ResultRow row) Launch(row.Entry);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && key >= Key.D1 && key <= Key.D8)
        {
            var index = key - Key.D1;
            if (index < ResultsList.Items.Count) Launch(((ResultRow)ResultsList.Items[index]).Entry);
            e.Handled = true;
        }
    }

    private void Launch(AppEntry entry)
    {
        try
        {
            if (!entry.LaunchPath.StartsWith("shell:", StringComparison.Ordinal) && !File.Exists(entry.LaunchPath))
            { ShowNotice("此应用路径已失效，请刷新列表或重新添加。"); return; }
            var info = new ProcessStartInfo(entry.LaunchPath) { UseShellExecute = true };
            if (File.Exists(entry.LaunchPath)) info.WorkingDirectory = Path.GetDirectoryName(entry.LaunchPath)!;
            using var process = Process.Start(info);
            settings.Usage.TryGetValue(entry.Id, out var usage);
            settings.Usage[entry.Id] = new(Math.Min((usage?.Count ?? 0) + 1, 1000000), DateTimeOffset.UtcNow);
            SaveSettings();
            Hide();
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        { ShowNotice("无法打开应用：" + exception.Message); }
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(ResultsList, element) is ListBoxItem item
            && item.DataContext is ResultRow row) Launch(row.Entry);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!interaction && !composing && !dialogOpen && !resultMenuOpen) Hide();
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBlock or Border or Grid && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnClearClick(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private void OnResultMenuOpening(object sender, ContextMenuEventArgs args)
    {
        if (sender is not ListBoxItem { DataContext: ResultRow row } item) { args.Handled = true; return; }
        ResultsList.SelectedItem = row;
        resultMenuOpen = true;
        ((MenuItem)item.ContextMenu.Items[0]).Header = settings.PinnedApps.Contains(row.Entry.Id) ? "取消固定" : "固定到常用";
    }

    private void OnResultMenuClosed(object sender, RoutedEventArgs args)
    {
        resultMenuOpen = false;
        if (IsVisible && !dialogOpen) SearchBox.Focus();
    }

    private void OnPinClick(object sender, RoutedEventArgs args) => TogglePin();
    private void OnEditAliasesClick(object sender, RoutedEventArgs args) => EditAliases();
    private void TogglePin()
    {
        if (ResultsList.SelectedItem is not ResultRow row) return;
        if (!settings.PinnedApps.Remove(row.Entry.Id)) settings.PinnedApps.Add(row.Entry.Id);
        SaveSettings();
        UpdateResults(preserveSelection: true);
    }

    private void EditAliases()
    {
        if (ResultsList.SelectedItem is not ResultRow row) return;
        dialogOpen = true;
        try
        {
            var editor = new AliasEditorWindow(row.Entry, settings.Aliases.GetValueOrDefault(row.Entry.Id) ?? []) { Owner = this, Icon = Icon };
            if (editor.ShowDialog() != true) return;
            if (editor.Aliases.Length == 0) settings.Aliases.Remove(row.Entry.Id);
            else settings.Aliases[row.Entry.Id] = editor.Aliases;
            aliasesVersion++;
            SaveSettings();
            engine = new SearchEngine(indexedApps, settings.Aliases);
            UpdateResults(preserveSelection: true);
        }
        finally { dialogOpen = false; if (IsVisible) SearchBox.Focus(); }
    }
    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();
    private void OnAuthorGitHubClick(object sender, RoutedEventArgs e) => OpenGitHub("https://github.com/VieVibe");
    private void OnProjectGitHubClick(object sender, RoutedEventArgs e) => OpenGitHub("https://github.com/VieVibe/Glint");

    private void OpenGitHub(string url)
    {
        try { using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            ShowLauncher();
            ShowNotice("无法打开浏览器，请手动访问：" + url);
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshIndexAsync();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        interaction = true;
        AltSpaceMenu.IsChecked = settings.Hotkey == "Alt+Space";
        CtrlAltSpaceMenu.IsChecked = settings.Hotkey == "Ctrl+Alt+Space";
        try { StartupMenu.IsChecked = StartupService.IsEnabled(); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        { ShowNotice("无法读取开机启动设置。"); }
        PortableMenu.Items.Clear();
        foreach (var path in settings.PortableApps)
        {
            var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(path), ToolTip = path };
            item.Click += (_, _) => { settings.PortableApps.Remove(path); SaveSettings(); _ = RefreshIndexAsync(); };
            PortableMenu.Items.Add(item);
        }
        PortableMenu.IsEnabled = PortableMenu.Items.Count > 0;
        SettingsMenu.PlacementTarget = SettingsButton;
        SettingsMenu.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (SettingsMenu.IsOpen) Keyboard.Focus((MenuItem)SettingsMenu.Items[0]);
        });
    }

    private void OnSettingsClosed(object sender, RoutedEventArgs e)
    {
        interaction = false;
        if (IsVisible && !dialogOpen) SearchBox.Focus();
    }

    private async void OnAddPortableClick(object sender, RoutedEventArgs e)
    {
        dialogOpen = true;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "添加便携应用", Filter = "应用程序或快捷方式|*.exe;*.lnk;*.appref-ms", Multiselect = true,
                CheckFileExists = true, DereferenceLinks = false
            };
            if (dialog.ShowDialog(this) == true)
            {
                foreach (var file in dialog.FileNames)
                    if (!settings.PortableApps.Contains(file, StringComparer.OrdinalIgnoreCase)) settings.PortableApps.Add(file);
                SaveSettings();
                await RefreshIndexAsync();
            }
        }
        finally { dialogOpen = false; ShowLauncher(); }
    }

    private void ChangeHotkey(string value)
    {
        if (!hotkey!.TryRegister(value))
        { ShowNotice(value + " 已被其他程序占用，原快捷键保持不变。"); return; }
        settings.Hotkey = value;
        HotkeyLabel.Text = value + " 唤出";
        if (tray is not null) tray.Text = "轻启 Glint · " + value;
        ShowNotice("唤出快捷键已设为 " + value);
        SaveSettings();
    }

    private void OnAltSpaceClick(object sender, RoutedEventArgs e) => ChangeHotkey("Alt+Space");
    private void OnCtrlAltSpaceClick(object sender, RoutedEventArgs e) => ChangeHotkey("Ctrl+Alt+Space");
    private void OnStartupClick(object sender, RoutedEventArgs e)
    {
        try { StartupService.SetEnabled(StartupMenu.IsChecked); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { StartupMenu.IsChecked = !StartupMenu.IsChecked; ShowNotice("无法更新开机启动：" + exception.Message); }
    }

    private bool SaveSettings()
    {
        try { store.Save(settings); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { ShowNotice("设置暂时无法保存：" + exception.Message); return false; }
    }

    private void ShowNotice(string message) { Notice.Text = message; Notice.Visibility = Visibility.Visible; }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; Hide(); } }
    private void OnExitClick(object sender, RoutedEventArgs e) => ExitLauncher();
    public void RequestExit() => ExitLauncher();
    private void ExitLauncher() { exiting = true; Close(); Application.Current.Shutdown(); }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

    private sealed class ResultRow(AppEntry entry, int index, bool duplicateName, bool pinned, string[] aliases) : INotifyPropertyChanged
    {
        public AppEntry Entry { get; } = entry;
        public string Name => Entry.Name;
        public string PinIndicator => pinned ? "★" : "";
        public string Source => aliases.Length > 0 ? "别名：" + string.Join("、", aliases)
            : duplicateName && Entry.Keywords.Length > 0 ? Entry.Source + " · " + Entry.Keywords.Split('|')[0] : Entry.Source;
        public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "✦";
        public string Shortcut => "Alt+" + index;
        private ImageSource? icon;
        public ImageSource? Icon
        {
            get => icon;
            set { icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
