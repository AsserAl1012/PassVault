using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PassVault.App;

/// <summary>
/// The unlock second, made visible.
///
/// Unlocking runs Argon2id over 64 MiB, three passes - about a second of real work, on
/// purpose, every time the app opens. Rather than spend that second on the word "Opening..."
/// this shows a four-pin lock where each pin seats on an event that genuinely happened.
///
/// While Argon2 runs, one pin breathes two pixels. Because that is a loop with no start and
/// no finish it looks identically correct at 400ms and at 3 seconds - it is structurally
/// incapable of lying about progress. A timed bar racing a job that offers no callback would
/// be a fiction, and you would know it was a fiction every single time you opened your own app.
/// </summary>
public partial class LockMechanism : UserControl
{
    private static readonly double[] Resting = [-10, -6, -13, -8];

    private readonly DispatcherTimer _patience = new() { Interval = TimeSpan.FromSeconds(3) };
    private Storyboard? _breathing;

    public LockMechanism()
    {
        InitializeComponent();
        _patience.Tick += (_, _) => { _patience.Stop(); Fade(Patience, 1); };
    }

    private TranslateTransform Lift(int pin) => (TranslateTransform)FindName($"T{pin}")!;
    private SolidColorBrush PinInk(int pin) => (SolidColorBrush)FindName($"Ink{pin}")!;
    private SolidColorBrush LineInk(int pin) => (SolidColorBrush)FindName($"Line{pin}")!;

    /// <summary>Back to an unseated lock, ready for another go.</summary>
    public void Reset(string caption = "")
    {
        StopBreathing();
        _patience.Stop();
        Patience.Opacity = 0;
        Caption.Text = caption;

        for (int pin = 1; pin <= 4; pin++)
        {
            Lift(pin).BeginAnimation(TranslateTransform.YProperty, null);
            Lift(pin).Y = Resting[pin - 1];
            PinInk(pin).BeginAnimation(SolidColorBrush.ColorProperty, null);
            PinInk(pin).Color = Colour("HairStrongColor");
            LineInk(pin).BeginAnimation(SolidColorBrush.ColorProperty, null);
            LineInk(pin).Color = Colour("HairlineColor");
        }
    }

    /// <summary>A pin drops to the shear line and its segment lights. Something real happened.</summary>
    public void Seat(int pin, string caption)
    {
        if (pin < 1 || pin > 4) return;

        StopBreathing();
        Caption.Text = caption;

        Lift(pin).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = (IEasingFunction)FindResource("EaseOut"),
        });

        Recolour(PinInk(pin), "AccentColor", 140);
        Recolour(LineInk(pin), "AccentColor", 90);
    }

    /// <summary>
    /// The long stretch. One pin, two pixels, nothing else on the screen moving.
    /// </summary>
    public void Breathe(int pin, string caption)
    {
        StopBreathing();
        Caption.Text = caption;

        var rest = Resting[pin - 1];
        var breath = new DoubleAnimation
        {
            From = rest,
            To = rest + 2,
            Duration = TimeSpan.FromMilliseconds(1100),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Storyboard.SetTarget(breath, Lift(pin));
        Storyboard.SetTargetProperty(breath, new PropertyPath(TranslateTransform.YProperty));

        _breathing = new Storyboard();
        _breathing.Children.Add(breath);
        _breathing.Begin();

        _patience.Stop();
        Patience.Opacity = 0;
        _patience.Start();
    }

    private void StopBreathing()
    {
        _breathing?.Stop();
        _breathing = null;
    }

    private void Recolour(SolidColorBrush brush, string key, int ms)
        => brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            To = Colour(key),
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = (IEasingFunction)FindResource("EaseOut"),
        });

    private Color Colour(string key) => (Color)FindResource(key);

    private static void Fade(UIElement element, double to)
        => element.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(240),
        });
}
