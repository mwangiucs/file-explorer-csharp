using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;

namespace FileExplorerCS;

public class PdfPageItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;

    public string SourceFilePath { get; set; } = string.Empty;
    public string SourceFileName => Path.GetFileName(SourceFilePath);
    public int PageNumber { get; set; } // 1-based index
    
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail != value)
            {
                _thumbnail = value;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }
    }

    public string PageLabel => $"Page {PageNumber}";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public partial class MergePdfDialog : Window
{
    public ObservableCollection<PdfPageItem> PdfPages { get; } = new();
    public ICommand RemovePageCommand { get; }

    private System.Windows.Point _dragStartPoint;

    public MergePdfDialog()
    {
        InitializeComponent();
        PageListView.ItemsSource = PdfPages;
        RemovePageCommand = new RelayCommand<PdfPageItem>(RemovePage);
        DataContext = this;
    }

    public void SetPdfFiles(List<ExplorerItem> files)
    {
        PdfPages.Clear();
        LoadPagesFromFilesAsync(files.Select(f => f.FullPath).ToList());
    }

    public List<PdfPageItem> GetOrderedPdfPages()
    {
        return PdfPages.ToList();
    }

    private async void LoadPagesFromFilesAsync(List<string> paths)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;

            try
            {
                // Load count in background
                int pageCount = 0;
                await Task.Run(() =>
                {
                    try
                    {
                        using var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(10));
                        pageCount = doc.GetPageCount();
                    }
                    catch { }
                });

                for (int i = 1; i <= pageCount; i++)
                {
                    var pageNum = i;
                    var item = new PdfPageItem
                    {
                        SourceFilePath = path,
                        PageNumber = pageNum,
                        Thumbnail = null // loaded asynchronously next
                    };
                    PdfPages.Add(item);

                    // Load thumbnail asynchronously
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            // 0.25 scale is super fast
                            using var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(0.2));
                            using var pageReader = doc.GetPageReader(pageNum - 1); // 0-based in docnet
                            int w = pageReader.GetPageWidth();
                            int h = pageReader.GetPageHeight();
                            byte[] bgra = pageReader.GetImage();

                            Dispatcher.Invoke(() =>
                            {
                                var writeable = new WriteableBitmap(w, h, 72, 72, PixelFormats.Bgra32, null);
                                writeable.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
                                writeable.Freeze();
                                item.Thumbnail = writeable;
                            });
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }
    }

    private void RemovePage(PdfPageItem? item)
    {
        if (item != null)
        {
            PdfPages.Remove(item);
        }
    }

    private void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var openDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select PDF Files to Add",
            Filter = "PDF Files (*.pdf)|*.pdf",
            Multiselect = true
        };

        if (openDialog.ShowDialog(this) == true)
        {
            LoadPagesFromFilesAsync(openDialog.FileNames.ToList());
        }
    }

    // ── Drag and Drop ────────────────────────────────────────────────────────

    private void PageListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void PageListView_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            System.Windows.Point mousePos = e.GetPosition(null);
            Vector diff = _dragStartPoint - mousePos;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                // Get the dragged item
                var listView = sender as System.Windows.Controls.ListView;
                if (listView == null) return;

                var item = FindAncestor<System.Windows.Controls.ListViewItem>((DependencyObject)e.OriginalSource);
                if (item == null) return;

                var data = item.DataContext as PdfPageItem;
                if (data == null) return;

                DragDrop.DoDragDrop(listView, data, System.Windows.DragDropEffects.Move);
            }
        }
    }

    private void PageListView_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(PdfPageItem)))
        {
            e.Effects = System.Windows.DragDropEffects.Move;
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void PageListView_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(PdfPageItem)))
        {
            var droppedItem = e.Data.GetData(typeof(PdfPageItem)) as PdfPageItem;
            if (droppedItem == null) return;

            // Find target element
            var parent = sender as System.Windows.Controls.ListView;
            if (parent == null) return;

            var targetItemElement = FindAncestor<System.Windows.Controls.ListViewItem>((DependencyObject)e.OriginalSource);
            int newIndex;

            if (targetItemElement != null)
            {
                var targetItem = targetItemElement.DataContext as PdfPageItem;
                if (targetItem == null) return;

                newIndex = PdfPages.IndexOf(targetItem);
            }
            else
            {
                newIndex = PdfPages.Count;
            }

            int oldIndex = PdfPages.IndexOf(droppedItem);
            if (oldIndex >= 0 && oldIndex != newIndex)
            {
                PdfPages.RemoveAt(oldIndex);
                if (newIndex > oldIndex) newIndex--; // Adjust index since we removed one before it
                
                if (newIndex >= PdfPages.Count)
                    PdfPages.Add(droppedItem);
                else
                    PdfPages.Insert(newIndex, droppedItem);
            }
        }
    }

    // ── Navigation & Pruning Buttons ────────────────────────────────────────

    private void MoveLeftButton_Click(object sender, RoutedEventArgs e)
    {
        int index = PageListView.SelectedIndex;
        if (index > 0)
        {
            var item = PdfPages[index];
            PdfPages.RemoveAt(index);
            PdfPages.Insert(index - 1, item);
            PageListView.SelectedIndex = index - 1;
        }
    }

    private void MoveRightButton_Click(object sender, RoutedEventArgs e)
    {
        int index = PageListView.SelectedIndex;
        if (index >= 0 && index < PdfPages.Count - 1)
        {
            var item = PdfPages[index];
            PdfPages.RemoveAt(index);
            PdfPages.Insert(index + 1, item);
            PageListView.SelectedIndex = index + 1;
        }
    }

    private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = PageListView.SelectedItems.Cast<PdfPageItem>().ToList();
        foreach (var item in selected)
        {
            PdfPages.Remove(item);
        }
    }

    private void OKButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (PdfPages.Count == 0)
        {
            System.Windows.MessageBox.Show("Please arrange at least one page to merge.", "Empty Document", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    // Helpers
    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        do
        {
            if (current is T t)
            {
                return t;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        while (current != null);
        return null;
    }
}

// Simple RelayCommand implementation to support Context/Command binding
public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Predicate<T?>? _canExecute;

    public RelayCommand(Action<T?> execute, Predicate<T?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute == null || _canExecute((T?)parameter);
    public void Execute(object? parameter) => _execute((T?)parameter);
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
