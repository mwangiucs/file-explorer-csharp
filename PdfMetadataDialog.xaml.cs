using System.Windows;

namespace FileExplorerCS;

public partial class PdfMetadataDialog : Window
{
    public string PdfTitle => TitleTextBox.Text;
    public string PdfAuthor => AuthorTextBox.Text;
    public string PdfSubject => SubjectTextBox.Text;
    public string PdfKeywords => KeywordsTextBox.Text;

    public PdfMetadataDialog()
    {
        InitializeComponent();
    }

    public void SetMetadata(string title, string author, string subject, string keywords)
    {
        TitleTextBox.Text = title ?? string.Empty;
        AuthorTextBox.Text = author ?? string.Empty;
        SubjectTextBox.Text = subject ?? string.Empty;
        KeywordsTextBox.Text = keywords ?? string.Empty;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
