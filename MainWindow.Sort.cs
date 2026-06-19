// ── MainWindow.Sort.cs ────────────────────────────────────────────────────────
// Drop alongside MainWindow.xaml.cs. Replaces SortButton_OnClick and
// SmartApplySortButton_OnClick in the original file.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FileExplorerCS;

public partial class MainWindow
{
    // ── Ribbon / toolbar "Sort" button ────────────────────────────────────────
    private void SortButton_OnClick(object sender, RoutedEventArgs e)
        => RunSortWorkflow(getRibbonFiles: true);

    // ── Left-panel "Sort selected files" button ───────────────────────────────
    private void SmartApplySortButton_OnClick(object sender, RoutedEventArgs e)
        => RunSortWorkflow(getRibbonFiles: false);

    // ── Shared sort workflow ──────────────────────────────────────────────────
    private void RunSortWorkflow(bool getRibbonFiles)
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;

        List<ExplorerItem> files = ItemsList.SelectedItems
            .OfType<ExplorerItem>()
            .Where(i => i.Type != "File folder")
            .ToList();

        if (files.Count == 0)
        {
            StatusText.Text = "Select one or more files to sort.";
            return;
        }

        // Open the redesigned dialog — pass file paths so it can show live preview
        var dialog = new SortDialog(_currentPath, files.Select(f => f.FullPath))
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || dialog.Result is null)
            return;

        SortOptions opts = dialog.Result;

        // Run the actual sort
        System.Collections.Generic.List<SortMoveResult> results;
        try
        {
            results = FileSorter.Sort(files.Select(f => f.FullPath), _currentPath, opts);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Sort failed: {ex.Message}";
            System.Windows.MessageBox.Show(ex.Message, "Sort failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        RefreshAfterMutation();

        int ok   = results.Count(r => r.Success);
        int fail = results.Count(r => !r.Success);

        // ── Results summary ───────────────────────────────────────────────────
        if (fail == 0)
        {
            StatusText.Text = $"Sorted {ok} file{(ok == 1 ? "" : "s")} successfully.";
        }
        else
        {
            StatusText.Text = $"Sorted {ok} file{(ok == 1 ? "" : "s")}, {fail} failed.";
            ShowSortResultsDialog(results);
        }
    }

    // ── Compact results dialog shown only when there are failures ─────────────
    private void ShowSortResultsDialog(System.Collections.Generic.List<SortMoveResult> results)
    {
        var win = new Window
        {
            Title                 = "Sort Results",
            Width                 = 540,
            Height                = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            FontFamily            = new System.Windows.Media.FontFamily("Segoe UI"),
            Background            = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF8, 0xF9, 0xFA)),
            ResizeMode            = ResizeMode.CanResizeWithGrip
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lv = new System.Windows.Controls.ListView
        {
            BorderThickness = new Thickness(0),
            Background      = System.Windows.Media.Brushes.White,
            Margin          = new Thickness(0)
        };

        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "File",        Width = 200, DisplayMemberBinding = new System.Windows.Data.Binding("FileName") });
        gv.Columns.Add(new GridViewColumn { Header = "Destination", Width = 220, DisplayMemberBinding = new System.Windows.Data.Binding("DestFolder") });
        gv.Columns.Add(new GridViewColumn { Header = "Status",      Width = 80,  DisplayMemberBinding = new System.Windows.Data.Binding("StatusText") });
        lv.View = gv;

        lv.ItemsSource = results.Select(r => new
        {
            r.FileName,
            DestFolder = r.Success ? TruncatePath(r.DestFolder, 36) : "—",
            StatusText = r.Success ? "✓ OK" : "✗ Failed",
            Error      = r.Error ?? ""
        }).ToList();

        Grid.SetRow(lv, 0);
        grid.Children.Add(lv);

        var footer = new Border
        {
            Background      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF3, 0xF5)),
            BorderBrush     = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDE, 0xE2, 0xE6)),
            BorderThickness = new Thickness(0, 0.5, 0, 0),
            Padding         = new Thickness(12, 8, 12, 8)
        };
        var closeBtn = new System.Windows.Controls.Button
        {
            Content             = "Close",
            Padding             = new Thickness(20, 6, 20, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Background          = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x5F, 0xA5)),
            Foreground          = System.Windows.Media.Brushes.White,
            BorderThickness     = new Thickness(0),
            Cursor              = System.Windows.Input.Cursors.Hand
        };
        closeBtn.Click += (_, _) => win.Close();
        footer.Child    = closeBtn;

        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);

        win.Content = grid;
        win.ShowDialog();
    }

    private static string TruncatePath(string path, int maxLen)
        => path.Length <= maxLen ? path : "…" + path[^(maxLen - 1)..];
}
