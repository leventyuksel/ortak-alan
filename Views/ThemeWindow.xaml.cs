using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OrtakAlan.Views;

public partial class ThemeWindow : ChromeWindow
{
    readonly List<SwatchVm> _cards = new();
    string _chosen;

    public ThemeWindow()
    {
        InitializeComponent();
        UseLogo(LogoImage);
        _chosen = ThemeService.Find(App.Me.Settings.Theme).Name;
        LightThemes.ItemsSource = Cards(ThemeService.Light);
        DarkThemes.ItemsSource = Cards(ThemeService.DarkThemes);
    }

    List<SwatchVm> Cards(IReadOnlyList<ThemeSet> themes)
    {
        var list = themes.Select(theme => SwatchVm.From(theme, theme.Name == _chosen)).ToList();
        _cards.AddRange(list);
        return list;
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SwatchVm swatch) return;
        _chosen = swatch.Name;
        foreach (var item in _cards)
            item.Selected = item == swatch;
        ThemeService.Apply(_chosen);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var theme = ThemeService.Find(_chosen);
        App.Me.Settings.Theme = theme.Name;
        App.Me.Settings.Appearance = theme.Dark ? "Karanlık" : "Aydınlık";
        App.Me.Settings.TextColor = "Otomatik";
        App.Me.Store.Save();
        Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        ThemeService.Apply(App.Me.Settings.Theme);
        base.OnClosed(e);
    }

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}

public sealed class SwatchVm : Observable
{
    public string Name { get; init; } = "";
    public Brush Paper { get; init; } = Brushes.White;
    public Brush Face { get; init; } = Brushes.Black;
    public Brush Accent { get; init; } = Brushes.Teal;
    public Brush Line { get; init; } = Brushes.LightGray;
    public Brush EdgeBrush => Selected ? Accent : Line;
    bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                Raise(nameof(EdgeBrush));
        }
    }

    public static SwatchVm From(ThemeSet theme, bool selected) => new()
    {
        Name = theme.Name,
        Paper = Paint(theme.Paper),
        Face = Paint(theme.Ink),
        Accent = Paint(theme.Accent),
        Line = Paint(theme.Line),
        Selected = selected
    };

    static SolidColorBrush Paint(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
