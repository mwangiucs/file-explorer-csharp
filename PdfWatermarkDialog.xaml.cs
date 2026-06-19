using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace FileExplorerCS;

public partial class PdfWatermarkDialog : Window
{
    public bool IsTextWatermark => WatermarkTabControl.SelectedIndex == 0;

    // Text options
    public string WatermarkText => WatermarkTextBox.Text;
    public float WatermarkFontSize => (float)FontSizeSlider.Value;
    public float TextOpacity => (float)TextOpacitySlider.Value;
    public float WatermarkAngle => (float)AngleSlider.Value;

    // Image options
    public string ImagePath => ImagePathTextBox.Text;
    public float ImageScale => (float)ImageScaleSlider.Value;
    public float ImageOpacity => (float)ImageOpacitySlider.Value;

    public PdfWatermarkDialog()
    {
        InitializeComponent();
    }

    private void BrowseImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        var openDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Watermark Image",
            Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All Files (*.*)|*.*"
        };

        if (openDialog.ShowDialog(this) == true)
        {
            ImagePathTextBox.Text = openDialog.FileName;
        }
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!IsTextWatermark && string.IsNullOrWhiteSpace(ImagePath))
        {
            System.Windows.MessageBox.Show("Please select an image for the watermark.", "No Image Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
