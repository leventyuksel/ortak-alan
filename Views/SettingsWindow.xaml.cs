using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OrtakAlan.Views;

public partial class SettingsWindow : ChromeWindow
{
    readonly List<SwatchVm> _themes = new();
    readonly List<SwatchVm> _texts = new();
    string _appearance;
    string _theme;
    string _text;
    bool _saved;

    public SettingsWindow()
    {
        InitializeComponent();
        UseLogo(LogoImage);
        NameBox.Text = App.Me.Settings.ComputerName;
        StartBox.IsChecked = App.Me.Settings.StartWithWindows;
        _appearance = App.Me.Settings.Appearance;
        _theme = App.Me.Settings.Theme;
        _text = string.IsNullOrWhiteSpace(App.Me.Settings.TextColor) ? "Otomatik" : App.Me.Settings.TextColor;

        foreach (var theme in ThemeService.All)
        {
            _themes.Add(new SwatchVm
            {
                Name = theme.Name,
                Color = new SolidColorBrush(theme.Color),
                Selected = theme.Name == _theme
            });
        }
        ThemeSwatches.ItemsSource = _themes;

        foreach (var color in ThemeService.TextColors)
        {
            var paint = color.Color ?? (ThemeService.Dark ? Color.FromRgb(0xF3, 0xF4, 0xF6) : Color.FromRgb(0x11, 0x18, 0x27));
            _texts.Add(new SwatchVm
            {
                Name = color.Name,
                Color = new SolidColorBrush(paint),
                Selected = color.Name == _text
            });
        }
        TextSwatches.ItemsSource = _texts;
        MarkAppearance();
        ShowSample();
    }

    void Appearance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string name) return;
        _appearance = name;
        Preview();
        MarkAppearance();
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SwatchVm swatch) return;
        _theme = swatch.Name;
        foreach (var item in _themes)
            item.Selected = item == swatch;
        Preview();
    }

    void Text_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SwatchVm swatch) return;
        _text = swatch.Name;
        foreach (var item in _texts)
            item.Selected = item == swatch;
        Preview();
        ShowSample();
    }

    void Preview() => ThemeService.ApplySaved(_theme, _appearance, _text);

    void ShowSample()
    {
        if (FindResource("Ink") is Brush ink)
            SampleText.Foreground = ink;
    }

    void MarkAppearance()
    {
        var dark = _appearance == "Karanlık";
        var soft = (Brush)FindResource("AccentSoft");
        LightButton.Background = dark ? Brushes.Transparent : soft;
        DarkButton.Background = dark ? soft : Brushes.Transparent;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            NameError.Text = "Bir ad yazın.";
            NameError.Visibility = Visibility.Visible;
            return;
        }

        App.Me.Settings.ComputerName = name;
        App.Me.Settings.Appearance = _appearance;
        App.Me.Settings.Theme = _theme;
        App.Me.Settings.TextColor = _text;
        App.Me.Settings.StartWithWindows = StartBox.IsChecked == true;
        App.Me.Store.Save();
        if (App.Me.Settings.StartWithWindows) AutostartService.Enable();
        else AutostartService.Disable();
        ThemeService.ApplySaved(_theme, _appearance, _text);
        App.Me.PublishIdentity();
        _saved = true;
        Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (!_saved)
            ThemeService.ApplySaved(App.Me.Settings.Theme, App.Me.Settings.Appearance, App.Me.Settings.TextColor);
        base.OnClosed(e);
    }

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
