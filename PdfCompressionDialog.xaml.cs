using System.Windows;

namespace FileExplorerCS;

public partial class PdfCompressionDialog : Window
{
    public int MaxEdgePx => HighRadio.IsChecked == true ? 800 : (MediumRadio.IsChecked == true ? 1200 : 2000);
    public int JpegQuality => HighRadio.IsChecked == true ? 35 : (MediumRadio.IsChecked == true ? 60 : 85);
    public bool UseGhostscript => GhostscriptCheck.IsChecked == true;

    public PdfCompressionDialog()
    {
        InitializeComponent();
    }

    private void CompressButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
