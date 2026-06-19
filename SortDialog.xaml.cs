using System.IO;
// ── SortDialog.xaml.cs ────────────────────────────────────────────────────────
// Code-behind for SortDialog.xaml  (see companion file).
// Replaces the old SortDialog entirely.

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FileExplorerCS;

public sealed class SortPreviewRow
{
    public string FileName   { get; init; } = "";
    public string DestFolder { get; init; } = "";
    public bool   IsError    { get; init; }
    public string Error      { get; init; } = "";
}

public partial class SortDialog : Window
{
    private readonly string                           _currentFolder;
    private readonly List<string>                     _filePaths;
    public  readonly ObservableCollection<SortPreviewRow> PreviewRows = new();

    public SortOptions? Result { get; private set; }

    public SortDialog(string currentFolder, IEnumerable<string> filePaths)
    {
        bool isDark = false;
        try
        {
            isDark = AppSettingsStore.Load().IsDarkMode;
        }
        catch { }

        var themeDict = new ResourceDictionary
        {
            Source = new Uri(isDark ? "ThemeDark.xaml" : "ThemeLight.xaml", UriKind.RelativeOrAbsolute)
        };
        this.Resources.MergedDictionaries.Add(themeDict);

        _currentFolder = currentFolder;
        _filePaths     = filePaths.ToList();
        InitializeComponent();
        PreviewList.ItemsSource = PreviewRows;
        ArchivePathBox.Text     = currentFolder;
        RefreshPreview();
    }

    // ── Options changed → refresh preview ────────────────────────────────────

    private void AnyOption_Changed(object sender, RoutedEventArgs e) => RefreshPreview();
    private void AnyOption_Changed(object sender, SelectionChangedEventArgs e) => RefreshPreview();

    private void BrowseArchive_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description        = "Choose archive root folder",
            UseDescriptionForTitle = true,
            SelectedPath       = ArchivePathBox.Text
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            ArchivePathBox.Text = dlg.SelectedPath;
            RefreshPreview();
        }
    }

    private void RefreshPreview()
    {
        PreviewRows.Clear();
        var opts = BuildOptions(dryRun: true);

        foreach (string src in _filePaths.Take(50)) // cap preview at 50 rows
        {
            try
            {
                string dest       = FileSorter.PreviewDestPath(src, _currentFolder, opts);
                string destFolder = Path.GetDirectoryName(dest) ?? "";
                // Show path relative to archive root for readability
                string root       = string.IsNullOrWhiteSpace(opts.ArchiveRoot) ? _currentFolder : opts.ArchiveRoot;
                if (destFolder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    destFolder = "." + destFolder[root.Length..];

                PreviewRows.Add(new SortPreviewRow
                {
                    FileName   = Path.GetFileName(src),
                    DestFolder = destFolder,
                    IsError    = false
                });
            }
            catch (Exception ex)
            {
                PreviewRows.Add(new SortPreviewRow
                {
                    FileName   = Path.GetFileName(src),
                    DestFolder = "",
                    IsError    = true,
                    Error      = ex.Message
                });
            }
        }

        if (_filePaths.Count > 50)
            PreviewRows.Add(new SortPreviewRow
            {
                FileName   = $"… and {_filePaths.Count - 50} more files",
                DestFolder = "",
                IsError    = false
            });
    }

    private SortOptions BuildOptions(bool dryRun = false)
    {
        DateFolderMode dateMode = DateFolderMode.None;
        if (DateMonthRadio?.IsChecked == true) dateMode = DateFolderMode.ByMonth;
        else if (DateYearRadio?.IsChecked  == true) dateMode = DateFolderMode.ByYear;
        else if (DateDayRadio?.IsChecked   == true) dateMode = DateFolderMode.ByDay;

        DuplicateAction dupeAction = DuplicateAction.Rename;
        if (DupeSkipRadio?.IsChecked      == true) dupeAction = DuplicateAction.Skip;
        else if (DupeOverwriteRadio?.IsChecked == true) dupeAction = DuplicateAction.Overwrite;

        return new SortOptions
        {
            DateMode      = dateMode,
            TagSubfolders = TagFolderCheck?.IsChecked  == true,
            ExtSubfolders = ExtFolderCheck?.IsChecked  == true,
            ArchiveRoot   = string.IsNullOrWhiteSpace(ArchivePathBox?.Text) ? null : ArchivePathBox.Text.Trim(),
            OnDuplicate   = dupeAction,
            DryRun        = dryRun
        };
    }

    // ── Dialog buttons ────────────────────────────────────────────────────────

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        Result       = BuildOptions(dryRun: false);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
