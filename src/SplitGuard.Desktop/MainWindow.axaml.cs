using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SplitGuard.Services;
using SplitGuard.ViewModels;

namespace SplitGuard.Views;

// Desktop shell around the shared MainView: extended-chrome window, .conf drag-drop,
// keyboard shortcuts, window-bounds persistence, and the IDialogs implementation
// (clipboard + WinRT toasts).
public partial class MainWindow : Window, IDialogs
{
    public MainWindow()
    {
        InitializeComponent();
        // The theme engine paints the WINDOW background (the extended-client title bar must
        // share the page color, and empty strip areas keep native caption drag), and hands us
        // the accent-composed icons for the window/tray/toast registration.
        View.ChromeTarget = this;
        View.IconsChanged = icons =>
        {
            Icon = icons.Idle;
            TrayHost.Current?.SetAccentIcons(icons.Idle, icons.Active);
            // Save a branded PNG and register it as the toast-notification app icon/name.
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SplitGuard");
                Directory.CreateDirectory(dir);
                var iconPath = Path.Combine(dir, "notify.png");
                icons.Logo.Save(iconPath);
                NotificationService.Register(iconPath);
            }
            catch { }
        };
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        // "Drop to import" overlay while files hover anywhere over the window.
        AddHandler(DragDrop.DragEnterEvent, (_, e) =>
            View.SetDropOverlay(e.Data.Contains(DataFormats.Files)));
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => View.SetDropOverlay(false));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public void ApplyUiPrefs(SplitGuard.Models.UiPrefs prefs)
    {
        View.ApplyUiPrefs(prefs);
        // Reconcile the run-at-boot registry entry with the (possibly default-on) pref — never
        // from the UI-review harness, which must not point the Run key at a dev binary.
        if (!RuleStore.DemoMode)
            try { StartupService.Set(prefs.StartOnBoot); } catch { }
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        View.SetDropOverlay(false);
        if (DataContext is not MainViewModel vm) return;
        var files = e.Data.GetFiles() ?? Enumerable.Empty<IStorageItem>();
        foreach (var item in files.OfType<IStorageFile>())
        {
            if (!item.Name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) continue;
            await using var stream = await item.OpenReadAsync();
            using var reader = new StreamReader(stream);
            var conf = await reader.ReadToEndAsync();
            // Only import whole tunnels here. A peer-descriptor .conf dropped onto a tunnel card is
            // added there as a peer (TunnelCard.OnCardDrop); the card handler can't mark the bubbling
            // drop handled before its async file read, so gate on content to avoid double-processing.
            if (!conf.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)) continue;
            vm.AddTunnelFromText(conf, Path.GetFileNameWithoutExtension(item.Name));
        }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        var vm = DataContext as MainViewModel;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // Enter in a single-line field commits and drops focus (clearing the highlight).
        // Add-boxes bind Enter to their add command and mark it handled, so they never
        // reach here; multiline (raw-config) fields keep Enter for newlines.
        if (e.Key == Key.Enter && !e.Handled
            && FocusManager?.GetFocusedElement() is TextBox { AcceptsReturn: false })
        {
            View.FocusList();
            e.Handled = true;
            return;
        }
        // Esc peels back one layer at a time: drop field focus, then close an open drawer,
        // then collapse (cancel) the editing card.
        if (e.Key == Key.Escape && !e.Handled)
        {
            if (FocusManager?.GetFocusedElement() is TextBox)
            {
                View.FocusList();
                e.Handled = true;
                return;
            }
            if (View.HasOpenDrawer)
            {
                View.CloseDrawers();
                e.Handled = true;
                return;
            }
            var editing = vm?.Tunnels.FirstOrDefault(t => t.IsEditing);
            if (editing is not null)
            {
                editing.CancelEditCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
        if (ctrl && e.Key == Key.N && vm is not null)
        {
            vm.CreateEmptyTunnel();
            e.Handled = true;
            return;
        }
        // Ctrl+O opens the file picker to import a .conf (mirrors the Add drawer's Import row).
        // Not while typing in a field (same guard as Ctrl+V), and close any open drawer first so
        // it isn't left hanging under the file dialog.
        if (ctrl && e.Key == Key.O && !e.Handled && vm is not null
            && FocusManager?.GetFocusedElement() is not TextBox)
        {
            View.CloseDrawers();
            _ = View.ImportConfAsync();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.E && vm is not null)
        {
            vm.Tunnels.FirstOrDefault(t => t.IsEditing)?.ToggleTextModeCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Ctrl+D: same two-step arming as the Delete button (first arms, second deletes).
        if (ctrl && e.Key == Key.D && vm is not null)
        {
            vm.Tunnels.FirstOrDefault(t => t.IsEditing)?.DeleteCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Ctrl+V outside a text field imports a config from the clipboard.
        if (ctrl && e.Key == Key.V
            && FocusManager?.GetFocusedElement() is not TextBox
            && vm is not null && Clipboard is not null)
        {
            var text = await Clipboard.GetTextAsync();
            if (MainViewModel.LooksLikeConfig(text))
            {
                vm.AddTunnelFromText(text!, null);
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
    }

    // Restore the last window size/position when the saved rect still lands on a visible
    // screen (a monitor may have been unplugged since); otherwise keep the defaults.
    public void RestoreWindowBounds(SplitGuard.Models.UiPrefs p)
    {
        if (p.WindowW < (int)MinWidth || p.WindowH < (int)MinHeight) return; // never saved (or nonsense)
        Width = p.WindowW;
        Height = p.WindowH;
        var pos = new PixelPoint(p.WindowX, p.WindowY);
        foreach (var s in Screens.All)
        {
            // The title bar's midpoint must be reachable so the window can always be dragged.
            if (!s.WorkingArea.Contains(new PixelPoint(pos.X + p.WindowW / 2, pos.Y + 20))) continue;
            Position = pos;
            return;
        }
    }

    // Capture the current bounds for persistence — only a Normal window's geometry is worth
    // keeping (a maximized/minimized rect would restore wrong).
    public void SaveWindowBounds(SplitGuard.Models.UiPrefs p)
    {
        if (WindowState != WindowState.Normal) return;
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        p.WindowW = (int)Bounds.Width;
        p.WindowH = (int)Bounds.Height;
        p.WindowX = Position.X;
        p.WindowY = Position.Y;
    }

    public async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is not null)
            await Clipboard.SetTextAsync(text);
    }

    public void Notify(string title, string message, bool isError) =>
        NotificationService.Show(title, message, isError);

    public async Task ExportTextAsync(string suggestedName, string text)
    {
        // DefaultExtension appends ".conf"; strip it from the suggested name so a
        // ".conf" filename doesn't become "name.conf.conf".
        var baseName = suggestedName.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)
            ? suggestedName[..^5] : suggestedName;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export WireGuard configuration",
            SuggestedFileName = baseName,
            DefaultExtension = "conf",
            FileTypeChoices = new[] { new FilePickerFileType("WireGuard config") { Patterns = new[] { "*.conf" } } },
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0); // truncate when overwriting a longer existing file
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text);
    }
}
