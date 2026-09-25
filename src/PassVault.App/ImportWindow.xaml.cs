using System.Windows;
using PassVault.Core.Model;

namespace PassVault.App;

/// <summary>
/// The one look before an import goes in: how many were found, how many are already in the
/// vault, and their names. The vault window does the adding once this says yes.
/// </summary>
public partial class ImportWindow : Window
{
    private const int NamesShown = 12;

    private readonly string _fileName;
    private readonly int _found;
    private readonly int _leftOut;
    private readonly List<Entry> _fresh;

    public ImportWindow(string fileName, int found, int leftOut, List<Entry> fresh)
    {
        InitializeComponent();
        ScreenGuard.Protect(this);

        _fileName = fileName;
        _found = found;
        _leftOut = leftOut;
        _fresh = fresh;
    }

    /// <summary>Whether to delete the export file once the import is safely saved.</summary>
    public bool DeleteFileAfter => DeleteAfter.IsChecked == true;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DarkTitleBar.Apply(this);

        int count = _fresh.Count;
        int already = _found - count;

        Band.State = count == 0 ? "Nothing new to import" : $"{count} to import";
        Band.Detail = _fileName;
        Band.Mood = count == 0 ? Mood.Idle : Mood.Good;

        Summary.Text = count == 0
            ? $"Everything in {_fileName} is already in your vault, so there is nothing new to bring in."
            : already == 0
                ? $"Found {Plural(count, "credential")} in {_fileName}."
                : $"Found {Plural(_found, "credential")} in {_fileName}. "
                  + (already == 1 ? "1 is already in your vault, so it is left out."
                                  : $"{already} are already in your vault, so they are left out.");

        var names = _fresh.Select(x => x.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Preview.Text = names.Count == 0 ? "Nothing."
            : string.Join("  ·  ", names.Take(NamesShown))
              + (names.Count > NamesShown ? $"  ·  and {names.Count - NamesShown} more" : "");

        // Said out loud, so nobody closes their old manager thinking their cards came across.
        if (_leftOut > 0)
            Summary.Text += $" {Plural(_leftOut, "other item")} {(_leftOut == 1 ? "stays" : "stay")} behind - "
                            + "cards, identities, folders or deleted items. PassVault holds logins and notes.";

        // Underscores doubled: in a checkbox label a single one marks a keyboard shortcut and
        // disappears, and Bitwarden and Proton both put underscores in their file names - the
        // one moment the name has to be right is when you are being asked to delete it.
        DeleteAfter.Content = $"Delete {_fileName.Replace("_", "__")} afterwards";
        // With nothing to import there is nothing for the box to wait on, so say it instead.
        if (count == 0)
        {
            DeleteAfter.Visibility = DeleteHint.Visibility = Visibility.Collapsed;
            Summary.Text += " Delete that file when you can - it holds every password in plain text.";
        }

        ImportButton.Content = count == 0 ? "Import" : $"Import {count}";
        ImportButton.IsEnabled = count > 0;
    }

    private void OnImport(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string Plural(int n, string one)
        => $"{n} {one}{(n == 1 ? "" : "s")}";
}
