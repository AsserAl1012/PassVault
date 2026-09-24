using System.Text.Json.Serialization;

namespace PassVault.Core.Model;

/// <summary>
/// One saved credential. Either username or email (or both) will be filled in.
///
/// Id, UpdatedAt and Deleted exist so sync can be added later without changing the file
/// format. Deleting keeps the row with Deleted = true (a "tombstone") - otherwise a
/// deleted entry would come back from the other device the next time they talk.
/// </summary>
public sealed class Entry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    /// <summary>Website or app this belongs to, e.g. "github.com".</summary>
    public string Name { get; set; } = "";

    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? Notes { get; set; }

    /// <summary>Unix time in milliseconds.</summary>
    public long UpdatedAt { get; set; } = Now();

    public bool Deleted { get; set; }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Who this login is for, for the list on screen. Never saved to the file.</summary>
    [JsonIgnore]
    public string Who => Username ?? Email ?? "";

    /// <summary>
    /// What a screen reader says for this row in the list - the same name and login the row
    /// shows. Without it every credential was read out as "PassVault.Core.Model.Entry".
    /// </summary>
    public override string ToString() => Who.Length == 0 ? Name : $"{Name}, {Who}";

    /// <summary>True if this entry matches what the user typed in the search box.</summary>
    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return Contains(Name, search) || Contains(Username, search) || Contains(Email, search) || Contains(Notes, search);
    }

    private static bool Contains(string? haystack, string needle)
        => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
