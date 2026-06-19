// ── SortDialog.Helpers.cs ─────────────────────────────────────────────────────
// Partial class additions: card-click helpers, title-bar drag, footer summary.
// Merge into SortDialog.xaml.cs (or keep as a separate partial file).

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FileExplorerCS;

public partial class SortDialog
{
    // ── Title-bar drag ────────────────────────────────────────────────────────
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    // ── Date-mode card clicks (clicking the whole card selects its radio) ─────
    private void DateNoneCard_Click(object  sender, MouseButtonEventArgs e) { DateNoneRadio.IsChecked  = true; HighlightCards(); }
    private void DateYearCard_Click(object  sender, MouseButtonEventArgs e) { DateYearRadio.IsChecked  = true; HighlightCards(); }
    private void DateMonthCard_Click(object sender, MouseButtonEventArgs e) { DateMonthRadio.IsChecked = true; HighlightCards(); }
    private void DateDayCard_Click(object   sender, MouseButtonEventArgs e) { DateDayRadio.IsChecked   = true; HighlightCards(); }

    private void HighlightCards()
    {
        var blue   = (SolidColorBrush)FindResource("Blue600Brush");
        var gray   = (SolidColorBrush)FindResource("Gray300Brush");
        var blueText  = (SolidColorBrush)FindResource("Blue600Brush");
        var grayText  = (SolidColorBrush)FindResource("Gray800Brush");

        SetCard(DateNoneCard,  DateNoneRadio.IsChecked  == true, blue, gray, blueText, grayText);
        SetCard(DateYearCard,  DateYearRadio.IsChecked  == true, blue, gray, blueText, grayText);
        SetCard(DateMonthCard, DateMonthRadio.IsChecked == true, blue, gray, blueText, grayText);
        SetCard(DateDayCard,   DateDayRadio.IsChecked   == true, blue, gray, blueText, grayText);

        UpdateFooter();
        RefreshPreview();
    }

    private static void SetCard(System.Windows.Controls.Border card, bool selected,
                                SolidColorBrush activeBorder, SolidColorBrush inactiveBorder,
                                SolidColorBrush activeText,   SolidColorBrush inactiveText)
    {
        card.BorderBrush = selected ? activeBorder : inactiveBorder;
        // Update the title TextBlock foreground (second child of the inner StackPanel)
        if (card.Child is System.Windows.Controls.StackPanel sp && sp.Children.Count >= 2
            && sp.Children[1] is System.Windows.Controls.TextBlock tb)
        {
            tb.Foreground = selected ? activeText : inactiveText;
        }
    }

    // ── Footer summary ────────────────────────────────────────────────────────
    private void UpdateFooter()
    {
        int n = _filePaths.Count;
        FooterSummary.Text = $"{n} file{(n == 1 ? "" : "s")} will be moved";
        PreviewCountText.Text = $"— {n} file{(n == 1 ? "" : "s")}";
    }

    // Called once when dialog loads
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        HighlightCards();
        UpdateFooter();
    }
}
