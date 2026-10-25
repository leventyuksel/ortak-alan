using System.Windows;
using System.Windows.Input;

namespace OrtakAlan.Views;

public partial class SettingsWindow : ChromeWindow
{
    readonly List<SwatchVm> _cards = new();
    string _theme;
    bool _saved;

    public SettingsWindow()
    {
        InitializeComponent();
        UseLogo(LogoImage);
        NameBox.Text = App.Me.Settings.ComputerName;
        StartBox.IsChecked = App.Me.Settings.StartWithWindows;
        _theme = ThemeService.Find(App.Me.Settings.Theme).Name;
        LightThemes.ItemsSource = Cards(ThemeService.Light);
        DarkThemes.ItemsSource = Cards(ThemeService.DarkThemes);
    }

    List<SwatchVm> Cards(IReadOnlyList<ThemeSet> themes)
    {
        var list = themes.Select(theme => SwatchVm.From(theme, theme.Name == _theme)).ToList();
        _cards.AddRange(list);
        return list;
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SwatchVm swatch) return;
        _theme = swatch.Name;
        foreach (var item in _cards)
            item.Selected = item == swatch;
        ThemeService.Apply(_theme);
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

        var theme = ThemeService.Find(_theme);
        App.Me.Settings.ComputerName = name;
        App.Me.Settings.Theme = theme.Name;
        App.Me.Settings.Appearance = theme.Dark ? "Karanlık" : "Aydınlık";
        App.Me.Settings.TextColor = "Otomatik";
        App.Me.Settings.StartWithWindows = StartBox.IsChecked == true;
        App.Me.Store.Save();
        if (App.Me.Settings.StartWithWindows) AutostartService.Enable();
        else AutostartService.Disable();
        ThemeService.Apply(theme.Name);
        App.Me.PublishIdentity();
        _saved = true;
        Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (!_saved)
            ThemeService.Apply(App.Me.Settings.Theme);
        base.OnClosed(e);
    }

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
