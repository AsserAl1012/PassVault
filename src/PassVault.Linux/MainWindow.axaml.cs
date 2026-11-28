using Avalonia.Controls;
using Avalonia.Interactivity;
using PassVault.Core.Vault;

namespace PassVault.Linux;

/// <summary>One window: the unlock screen until the vault opens, then the vault, then back on lock.</summary>
public partial class MainWindow : Window
{
    private VaultView? _vault;
    private bool _closingForReal;

    public MainWindow()
    {
        InitializeComponent();
        FontSize = Program.Settings.FontSize;
        ShowUnlock(null);
        Closing += OnClosing;

        // What the clipboard needs to know: see GuiClipboard.Touched.
        Activated += (_, _) => GuiClipboard.Touched = false;
        AddHandler(PointerPressedEvent, (_, _) => Touched(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => Touched(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void Touched()
    {
        GuiClipboard.Touched = true;
        _ = GuiClipboard.WipeIfDue();
    }

    private void ShowUnlock(string? why)
    {
        _vault = null;
        var unlock = new UnlockView();
        unlock.Opened += ShowVault;
        Host.Content = unlock;
        if (why is not null) unlock.SayFirst(why, bad: false);
    }

    private void ShowVault(OpenVault vault, string? notice)
    {
        _vault = new VaultView(vault, notice);
        _vault.Locked += ShowUnlock;
        Host.Content = _vault;
    }

    /// <summary>
    /// Nothing typed is thrown away because the window was closed, and a copied password does not
    /// outlive PassVault on the clipboard. The close waits for both, then happens. An edit that
    /// cannot be saved stops the first close, with the reason: it may be a password just made and
    /// already set on a website. A second close throws it away.
    /// </summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingForReal) return;
        e.Cancel = true;

        if (_vault?.SaveIfEdited() is { } refused && !_vault.CloseWarned)
        {
            _vault.CloseWarned = true;
            _vault.Say($"{refused} Close again to throw this edit away.");
            return;
        }
        _vault?.Close();
        await GuiClipboard.Wipe();

        _closingForReal = true;
        Close();
    }
}
