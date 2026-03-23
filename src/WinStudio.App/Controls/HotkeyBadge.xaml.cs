using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
namespace WinStudio.App.Controls;

public sealed partial class HotkeyBadge : UserControl
{
    private static readonly Brush BadgeBackgroundBrush = new SolidColorBrush(Color.FromArgb(255, 31, 31, 31));
    private static readonly Brush BadgeBorderBrush = new SolidColorBrush(Color.FromArgb(255, 42, 42, 42));
    private static readonly Brush BadgeTextBrush = new SolidColorBrush(Color.FromArgb(255, 240, 240, 240));
    private static readonly Brush SeparatorBrush = new SolidColorBrush(Color.FromArgb(255, 85, 85, 85));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(HotkeyBadge),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HotkeyTextProperty =
        DependencyProperty.Register(
            nameof(HotkeyText),
            typeof(string),
            typeof(HotkeyBadge),
            new PropertyMetadata(string.Empty, OnHotkeyTextChanged));

    public HotkeyBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => RenderKeys();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string HotkeyText
    {
        get => (string)GetValue(HotkeyTextProperty);
        set => SetValue(HotkeyTextProperty, value);
    }

    private static void OnHotkeyTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyBadge badge)
        {
            badge.RenderKeys();
        }
    }

    private void RenderKeys()
    {
        KeysPanel.Children.Clear();

        var segments = (HotkeyText ?? string.Empty)
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                KeysPanel.Children.Add(new TextBlock
                {
                    Text = "+",
                    Foreground = SeparatorBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11
                });
            }

            var keyBorder = new Border
            {
                Background = BadgeBackgroundBrush,
                BorderBrush = BadgeBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9999),
                Padding = new Thickness(8, 2, 8, 2),
                Child = new TextBlock
                {
                    Text = segments[i],
                    FontSize = 11,
                    FontFamily = new FontFamily("Cascadia Mono"),
                    Foreground = BadgeTextBrush
                }
            };

            KeysPanel.Children.Add(keyBorder);
        }
    }
}
