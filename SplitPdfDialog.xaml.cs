using System.Windows;

namespace FileExplorerCS;

public partial class SplitPdfDialog : Window
{
    public bool IsExtractMode => ExtractRangeRadio.IsChecked == true;
    public int PagesPerSplit { get; private set; } = 1;
    public string PageRange => PageRangeTextBox.Text.Trim();

    public SplitPdfDialog()
    {
        InitializeComponent();
    }

    private void OnSplitModeChanged(object sender, RoutedEventArgs e)
    {
        if (SplitEvenlyPanel == null || ExtractRangePanel == null) return;

        bool even = SplitEvenlyRadio.IsChecked == true;
        SplitEvenlyPanel.IsEnabled = even;
        SplitEvenlyPanel.Opacity = even ? 1.0 : 0.5;

        ExtractRangePanel.IsEnabled = !even;
        ExtractRangePanel.Opacity = !even ? 1.0 : 0.5;
    }

    private void OKButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!IsExtractMode)
        {
            if (int.TryParse(PagesPerSplitTextBox.Text, out int pages) && pages > 0)
            {
                PagesPerSplit = pages;
                DialogResult = true;
            }
            else
            {
                System.Windows.MessageBox.Show("Please enter a valid positive number for pages per split.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(PageRange))
            {
                System.Windows.MessageBox.Show("Please enter a valid page range (e.g. 1-3, 5).", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                DialogResult = true;
            }
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
