using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using WgSplitDns.ViewModels;

namespace WgSplitDns.Views;

public partial class MainWindow : Window, IDialogs
{
    public MainWindow()
    {
        InitializeComponent();
        // The header lives inside the extended title bar area — make it draggable.
        HeaderBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
                BeginMoveDrag(e);
        };
    }

    void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        var app = Avalonia.Application.Current!;
        var (next, tip) = app.RequestedThemeVariant switch
        {
            var v when v == ThemeVariant.Light => (ThemeVariant.Dark, "Theme: dark"),
            var v when v == ThemeVariant.Dark => (ThemeVariant.Default, "Theme: auto"),
            _ => (ThemeVariant.Light, "Theme: light"),
        };
        app.RequestedThemeVariant = next;
        ToolTip.SetTip(ThemeButton, tip);
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var yes = new Button { Content = "Delete", Classes = { "primary" } };
        var no = new Button { Content = "Cancel" };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { no, yes },
                },
            },
        };
        return await dialog.ShowDialog<bool?>(this) == true;
    }

    public async Task<string?> PickConfFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import WireGuard config",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("WireGuard config") { Patterns = new[] { "*.conf" } },
            },
        });
        var file = files.FirstOrDefault();
        if (file is null) return null;
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        var name = Path.GetFileNameWithoutExtension(file.Name);
        return $"{text}\0{name}";
    }

    public async Task<string?> PasteConfigAsync()
    {
        var dialog = new Window
        {
            Title = "Paste WireGuard config",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var box = new TextBox
        {
            AcceptsReturn = true,
            Height = 260,
            FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            FontSize = 12,
            Watermark = "[Interface]\nPrivateKey = …\nAddress = …\n\n[Peer]\nPublicKey = …\nEndpoint = …\nAllowedIPs = …",
        };
        var add = new Button { Content = "Add tunnel", Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel" };
        add.Click += (_, _) => dialog.Close(box.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            Children =
            {
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, add },
                },
            },
        };
        var result = await dialog.ShowDialog<string?>(this);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is not null)
            await Clipboard.SetTextAsync(text);
    }
}
