using System.Windows;

namespace OrtakAlan.Views;

public partial class ApprovalWindow : ChromeWindow
{
    public bool? Decision { get; private set; }

    public ApprovalWindow(string peerName, string localName)
    {
        InitializeComponent();
        UseLogo(LogoImage);
        PeerNameText.Text = peerName;
        LocalText.Text = localName + " olarak cevaplıyorsunuz.";
        Title = "Ortak Alan — " + localName;
    }

    void Allow_Click(object sender, RoutedEventArgs e)
    {
        Decision = true;
        DialogResult = true;
        Close();
    }

    void Deny_Click(object sender, RoutedEventArgs e)
    {
        Decision = false;
        DialogResult = false;
        Close();
    }
}
