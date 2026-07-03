using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using SplitGuard.ViewModels;

namespace SplitGuard.Views;

public partial class MainWindow : Window, IDialogs
{
    static readonly (string Name, ThemeVariant Variant, string? Background)[] ThemeSteps =
    {
        ("auto", ThemeVariant.Default, null),
        ("light", ThemeVariant.Light, null),
        ("slate", ThemeVariant.Dark, "#3C4148"),
        ("dark", ThemeVariant.Dark, null),
    };
    int _themeIndex;

    public MainWindow()
    {
        InitializeComponent();
        // The header lives inside the extended title bar area — make it draggable.
        HeaderBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
                BeginMoveDrag(e);
        };
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var files = e.Data.GetFiles() ?? Enumerable.Empty<IStorageItem>();
        foreach (var item in files.OfType<IStorageFile>())
        {
            if (!item.Name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) continue;
            await using var stream = await item.OpenReadAsync();
            using var reader = new StreamReader(stream);
            vm.AddTunnelFromText(await reader.ReadToEndAsync(), Path.GetFileNameWithoutExtension(item.Name));
        }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        // Ctrl+V outside a text field imports a config from the clipboard.
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && FocusManager?.GetFocusedElement() is not TextBox
            && DataContext is MainViewModel vm && Clipboard is not null)
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

    async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add tunnel",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("WireGuard config") { Patterns = new[] { "*.conf" } },
            },
        });
        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            vm.AddTunnelFromText(await reader.ReadToEndAsync(), Path.GetFileNameWithoutExtension(file.Name));
        }
    }

    void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        _themeIndex = (_themeIndex + 1) % ThemeSteps.Length;
        var (name, variant, background) = ThemeSteps[_themeIndex];
        Avalonia.Application.Current!.RequestedThemeVariant = variant;
        if (background is null) ClearValue(BackgroundProperty);
        else Background = new SolidColorBrush(Color.Parse(background));
        ToolTip.SetTip(ThemeButton, $"Theme: {name}");
    }

    public async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is not null)
            await Clipboard.SetTextAsync(text);
    }
}
