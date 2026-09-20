using System.Windows;
using System.Windows.Media;
using PassVault.Core.Licensing;
using PassVault.Core.Machine;

namespace PassVault.App;

/// <summary>
/// Where you see whether this copy is yours, and where you paste a licence.
///
/// It is reachable from a quiet button and never appears on its own. The free version is
/// complete and safe, so there is nothing here to nag about - only something to explain.
/// </summary>
public partial class LicenceWindow : Window
{
    private readonly string _folder = MachineStore.DefaultFolder;

    public Licence? Licence { get; private set; }

    public LicenceWindow() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DarkTitleBar.Apply(this);
        Licence = LicenceStore.Load(_folder);
        Paint();
    }

    private void Paint()
    {
        var owned = Licence is not null;

        OwnedPanel.Visibility = owned ? Visibility.Visible : Visibility.Collapsed;
        FreePanel.Visibility = owned ? Visibility.Collapsed : Visibility.Visible;

        if (owned)
        {
            Band.State = $"Owned by {Licence!.Name}";
            Band.Detail = "Yours. No expiry, no account, nothing to renew.";
            Band.Mood = Mood.Good;
            OwnerId.Text = Licence.Id;
            OwnerIssued.Text = $"Issued {Licence.Issued:d MMMM yyyy}";
        }
        else
        {
            Band.State = "Free version";
            Band.Detail = "Complete, and safe. Nothing about your security costs money.";
            Band.Mood = Mood.Idle;
        }
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var saved = LicenceStore.Save(_folder, PasteBox.Text);

        if (saved is null)
        {
            Say("That does not look like a PassVault licence. Paste the whole block, including the "
                + "BEGIN and END lines.");
            return;
        }

        Licence = saved;
        PasteBox.Clear();
        Say("");
        Paint();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        LicenceStore.Remove(_folder);
        Licence = null;
        Say("Removed from this PC. Your licence still works - paste it back any time.", error: false);
        Paint();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Say(string text, bool error = true)
    {
        Message.Text = text;
        Message.Foreground = (Brush)FindResource(error ? "Danger" : "Muted");
    }
}
