using System.Windows;
using System.Windows.Input;

namespace OrtakAlan.Views;

public partial class SetupWindow : ChromeWindow
{
    public string ChosenName { get; private set; } = "";

    public SetupWindow()
    {
        InitializeComponent();
        UseLogo(LogoImage);
        Loaded += (_, _) => NameBox.Focus();
    }

    void Continue_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("Bir ad yazın.");
            return;
        }
        if (name.Length > 32)
        {
            ShowError("Ad en fazla 32 karakter olabilir.");
            return;
        }
        if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            ShowError("Bu karakterler kullanılamaz.");
            return;
        }

        ChosenName = name;
        DialogResult = true;
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ErrorText.Visibility = Visibility.Collapsed;

    void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
        NameBox.Focus();
    }

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
