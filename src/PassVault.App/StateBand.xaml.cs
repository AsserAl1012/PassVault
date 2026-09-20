using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PassVault.App;

/// <summary>What the app is doing right now. Drives the band's colour and nothing else.</summary>
public enum Mood
{
    /// <summary>Nothing wrong, nothing happening. Grey.</summary>
    Idle,

    /// <summary>Working - Argon2, writing a stick. Brass.</summary>
    Working,

    /// <summary>Stick connected, vault open, saved. Green.</summary>
    Good,

    /// <summary>Something needs attention. Red.</summary>
    Bad,
}

/// <summary>
/// The state band. The app's whole job is telling you which state you are in - stick
/// connected or not, locked or open, how many tries are left - so that is the largest thing
/// on screen rather than a strip along the top.
/// </summary>
public partial class StateBand : UserControl
{
    public StateBand() => InitializeComponent();

    // ---- what it says ----

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(string), typeof(StateBand),
        new PropertyMetadata("", (d, e) => ((StateBand)d).StateText.Text = (string)e.NewValue));

    public string State
    {
        get => (string)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(string), typeof(StateBand),
        new PropertyMetadata("", (d, e) => ((StateBand)d).DetailText.Text = (string)e.NewValue));

    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(StateBand),
        new PropertyMetadata("", (d, e) => ((StateBand)d).GlyphText.Text = (string)e.NewValue));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public static readonly DependencyProperty TrailingProperty = DependencyProperty.Register(
        nameof(Trailing), typeof(string), typeof(StateBand),
        new PropertyMetadata("", (d, e) => ((StateBand)d).TrailText.Text = (string)e.NewValue));

    /// <summary>The right-hand slot - the auto-lock countdown on the vault window.</summary>
    public string Trailing
    {
        get => (string)GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    public static readonly DependencyProperty TrailingAlertProperty = DependencyProperty.Register(
        nameof(TrailingAlert), typeof(bool), typeof(StateBand),
        new PropertyMetadata(false, (d, e) =>
        {
            var band = (StateBand)d;
            band.Fade(band.TrailInk, (bool)e.NewValue ? "AccentColor" : "TextSecondaryColor");
        }));

    /// <summary>Turns the trailing text brass - the last minute before an auto-lock.</summary>
    public bool TrailingAlert
    {
        get => (bool)GetValue(TrailingAlertProperty);
        set => SetValue(TrailingAlertProperty, value);
    }

    // ---- what colour it is ----

    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(Mood), typeof(StateBand),
        new PropertyMetadata(Mood.Idle, (d, e) => ((StateBand)d).Repaint((Mood)e.NewValue)));

    public Mood Mood
    {
        get => (Mood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    /// <summary>
    /// The band never moves and never resizes - it changes colour in place. That is what
    /// makes a walk-through of several screens feel like one object rather than a stack of
    /// dialogs, and it means a state change can never make the content below it jump.
    /// </summary>
    private void Repaint(Mood mood)
    {
        var (wash, ink, edge) = mood switch
        {
            Mood.Working => ("WorkWashColor", "AccentColor", "AccentColor"),
            Mood.Good => ("GoodWashColor", "SuccessColor", "HairlineColor"),
            Mood.Bad => ("BadWashColor", "DangerColor", "DangerColor"),
            _ => ("IdleWashColor", "TextSecondaryColor", "HairlineColor"),
        };

        Fade(Wash, wash);
        Fade(GlyphInk, ink);
        Fade(Edge, edge);
    }

    private void Fade(SolidColorBrush brush, string colourKey)
    {
        var to = (Color)FindResource(colourKey);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            To = to,
            Duration = (Duration)FindResource("Travel"),
            EasingFunction = (IEasingFunction)FindResource("EaseInOut"),
        });
    }
}
