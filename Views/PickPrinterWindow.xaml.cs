using System.Drawing.Printing;
using System.Windows;

namespace OrtakAlan.Views;

public partial class PickPrinterWindow : ChromeWindow
{
    public string SelectedPrinter { get; private set; } = "";

    public PickPrinterWindow()
    {
        InitializeComponent();
        foreach (string printer in PrinterSettings.InstalledPrinters)
            PrinterList.Items.Add(printer);
        if (PrinterList.Items.Count == 0) EmptyText.Visibility = Visibility.Visible;
        else PrinterList.SelectedIndex = 0;
    }

    void Share_Click(object sender, RoutedEventArgs e)
    {
        if (PrinterList.SelectedItem is not string name || name.Length == 0) return;
        SelectedPrinter = name;
        DialogResult = true;
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
