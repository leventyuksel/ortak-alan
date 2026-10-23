using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OrtakAlan.Views;

public partial class ThemeWindow : ChromeWindow
{
    readonly List<SwatchVm> _swatches = new();
    string _chosen;

    public ThemeWindow()
    {
        InitializeComponent();
        UseLogo(LogoImage);
        _chosen = App.Me.Settings.Theme;
        foreach (var theme in ThemeService.All)
        {
            _swatches.Add(new SwatchVm
            {
                Name = theme.Name,
                Color = new SolidColorBrush(theme.Color),
                Mark = new SolidColorBrush(ThemeService.On(theme.Color)),
                Selected = theme.Name == _chosen
            });
        }
        Swatches.ItemsSource = _swatches;
    }

    void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SwatchVm swatch) return;
        _chosen = swatch.Name;
        foreach (var item in _swatches)
            item.Selected = item == swatch;
        ThemeService.Apply(_chosen);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        App.Me.Settings.Theme = _chosen;
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
    Brush _color = Brushes.Navy;
    public Brush Color { get => _color; set => Set(ref _color, value); }
    public Brush Mark { get; init; } = Brushes.White;
    bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}
