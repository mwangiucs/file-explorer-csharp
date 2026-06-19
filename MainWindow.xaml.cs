using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Docnet.Core;
using Docnet.Core.Models;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using SystemColors = System.Windows.SystemColors;
using Orientation = System.Windows.Controls.Orientation;
using Cursors = System.Windows.Input.Cursors;
using Button = System.Windows.Controls.Button;
using VerticalAlignment = System.Windows.VerticalAlignment;

using Microsoft.Win32;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using WinForms = System.Windows.Forms;

namespace FileExplorerCS;

public class BookmarkItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public partial class MainWindow : Window
{
    private const int MaxTextPreviewBytes = 384 * 1024;
    private const int ImageDecodeMaxWidth = 1400;

    private static readonly HashSet<string> ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".webp"
    ];
    private static readonly HashSet<string> PdfExtensions = [".pdf"];

    private static readonly HashSet<string> TextExtensions =
    [
        ".txt", ".log", ".csv", ".tsv", ".json", ".xml", ".xaml", ".cs", ".csproj", ".vb", ".fs", ".fsproj",
        ".md", ".markdown", ".yaml", ".yml", ".ini", ".cfg", ".config", ".props", ".targets", ".sln",
        ".htm", ".html", ".css", ".scss", ".js", ".ts", ".jsx", ".tsx", ".vue", ".sql", ".sh", ".ps1", ".bat", ".cmd",
        ".py", ".rb", ".go", ".rs", ".java", ".c", ".h", ".cpp", ".hpp", ".cmake", ".dockerfile",
        ".svg"
    ];

    private static readonly HashSet<string> TextFileBasenames =
    [
        "dockerfile", "makefile", "rakefile", "jenkinsfile", "gemfile",
        ".gitignore", ".gitattributes", ".editorconfig", ".env"
    ];

    private readonly ObservableCollection<DirectoryNode> _rootNodes = [];
    private readonly ObservableCollection<ExplorerItem> _items = [];
    private readonly ObservableCollection<BookmarkItem> _favorites = [];
    private readonly Stack<string> _backHistory = new();
    private readonly Stack<string> _forwardHistory = new();
    private string? _currentPath;
    private bool _isNavigatingFromHistory;
    private bool _isPdfPreviewReady;
    private int _previewRequestId;
    private int _lastNavigatedPdfRequestId;
    private bool _isUpdatingDetails;

    private AppUserSettings _appSettings = new();
    private readonly Stack<(string Description, Func<Task> Undo)> _undoStack = new();
    private string _currentSortProperty = "Name";
    private System.ComponentModel.ListSortDirection _currentSortDirection = System.ComponentModel.ListSortDirection.Ascending;
    private readonly List<string> _recentPaths = new();
    private FileSystemWatcher? _fileWatcher;
    private System.Windows.Threading.DispatcherTimer? _watcherTimer;
    private GridView? _defaultGridView;

    private readonly RenameEngine _renameEngine = new();
    private readonly PdfService _pdfService = new();
    private readonly FileOperationService _fileOperationService = new();

    private static readonly string FavoritesFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileExplorerCS",
        "favorites.json");
    
    private void DetailsNewName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingDetails) return;
        
        if (ItemsList.SelectedItem is ExplorerItem item)
        {
            var newName = DetailsNewName.Text;
            item.NewName = newName;

            string newBase;
            try
            {
                newBase = item.IsFolder ? newName : Path.GetFileNameWithoutExtension(newName);
            }
            catch
            {
                newBase = newName;
            }

            if (item.BaseName != newBase)
            {
                item.BaseName = newBase;
            }
        }
    }

    private bool _isBulkUpdatingMarked = false;

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ExplorerItem item) return;

        if (e.PropertyName == nameof(ExplorerItem.BaseName))
        {
            UpdateSmartPreview();
        }
        else if (e.PropertyName == nameof(ExplorerItem.IsMarkedForRename))
        {
            if (item.IsMarkedForRename)
            {
                if (SmartTagEnabledCheck?.IsChecked == true)
                {
                    item.Tag = GetSmartTagPart();
                }
                else
                {
                    item.Tag = FileSorter.ExtractTagFromFileName(Path.GetFileNameWithoutExtension(item.Name)) ?? string.Empty;
                }

                if (SmartDateEnabledCheck?.IsChecked == true)
                {
                    item.SmartDate = GetSmartDatePart();
                }
                else
                {
                    item.SmartDate = string.Empty;
                }
            }
            if (!_isBulkUpdatingMarked)
            {
                UpdateSmartPreview();
            }
        }
    }
    
    // --- Smart Panel State ---
    private static readonly char[] _invalidChars = Path.GetInvalidFileNameChars();

    public MainWindow()
    {
        // Set environment variable to make WebView2 backgrounds transparent by default and prevent white flash
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "0x00000000");

        InitializeComponent();

        // Also set control background color to transparent to ensure no flash
        PreviewPdfView.DefaultBackgroundColor = System.Drawing.Color.Transparent;

        _defaultGridView = ItemsList.View as GridView;
        if (_defaultGridView != null)
        {
            _defaultGridView.Columns.Remove(ColChangeBaseName);
            _defaultGridView.Columns.Remove(ColNewName);
        }
        LoadWindowState();
        SourceInitialized += Window_SourceInitialized;
        SmartDatePicker.SelectedDate = DateTime.Today;
        _appSettings = AppSettingsStore.Load();
        LoadSavedTags();
        LoadFavorites();
        FolderTree.ItemsSource = _rootNodes;
        ItemsList.ItemsSource = _items;
        FavoritesList.ItemsSource = _favorites;
        PinnedFoldersList.ItemsSource = _favorites;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowState();
    }

    private void LoadSavedTags()
    {
        SmartTagComboBox.ItemsSource = null;
        SmartTagComboBox.ItemsSource = _appSettings.SavedTags;
        if (_appSettings.SavedTags.Count > 0)
        {
            SmartTagComboBox.SelectedIndex = 0;
        }
    }


    private static string GetUniqueFilePath(string directory, string fileName)
    {
        string full = Path.Combine(directory, fileName);
        if (!File.Exists(full))
        {
            return full;
        }

        string ext = Path.GetExtension(fileName);
        string body = Path.GetFileNameWithoutExtension(fileName);
        for (int i = 1; i < 10_000; i++)
        {
            string candidate = Path.Combine(directory, $"{body} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Could not find a unique file name.");
    }

    private static List<ExplorerItem> GetSelectedPdfFiles(System.Windows.Controls.ListView listView)
    {
        return listView.SelectedItems
            .OfType<ExplorerItem>()
            .Where(i => i.Type != "File folder")
            .Where(i => string.Equals(Path.GetExtension(i.Name), ".pdf", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Returns selected PDF items in the order they appear in the file list (stable for merge).</summary>
    private List<ExplorerItem> GetSelectedPdfsInDisplayOrder()
    {
        HashSet<ExplorerItem> selected = GetSelectedPdfFiles(ItemsList).ToHashSet();
        if (selected.Count == 0)
        {
            return [];
        }

        var ordered = new List<ExplorerItem>();
        foreach (object? o in ItemsList.Items)
        {
            if (o is ExplorerItem item && selected.Contains(item))
            {
                ordered.Add(item);
            }
        }

        return ordered;
    }

    private void ReleasePdfPreviewLocks()
    {
        _previewRequestId++;
        PreviewScrollHost.Visibility = Visibility.Visible;
        PreviewPdfView.Visibility = Visibility.Collapsed;
        if (PreviewPdfView.CoreWebView2 is not null)
        {
            try
            {
                PreviewPdfView.CoreWebView2.Navigate("about:blank");
            }
            catch
            {
                // Ignore if WebView isn't ready.
            }
        }
    }

    private static string? TryGetExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(path.Trim());
            return Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }



    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            this.Unloaded += (s, ev) => {
                System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage -= ComponentDispatcher_ThreadFilterMessage;
            };
            System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage += ComponentDispatcher_ThreadFilterMessage;

            // 0. Load Theme globally before License Gating Check
            RestoreTheme();

            // 1. License Verification & Gating
            LicenseService.Initialize();
            UpdateLicensingUI();

            if (!LicenseService.IsRegistered && LicenseService.IsTrialExpired)
            {
                var regDlg = new RegistrationDialog(isExpiredMode: true)
                {
                    Owner = this
                };
                bool? result = regDlg.ShowDialog();
                if (result != true)
                {
                    System.Windows.Application.Current.Shutdown();
                    return;
                }
                UpdateLicensingUI();
            }

            // 2. Main Initialization
            await EnsurePdfPreviewReadyAsync();

            RestoreColumnWidths();
            RestoreViewMode();

            // 3. Load Drives & Files
            LoadDrives();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Error during initialization: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void LoadDrives()
    {
        _rootNodes.Clear();
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var node = new DirectoryNode
            {
                Name = drive.Name,
                FullPath = drive.RootDirectory.FullName
            };

            AddPlaceholder(node);
            _rootNodes.Add(node);
        }

        string initialPath = _appSettings.LastPath;
        if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
        {
            initialPath = _rootNodes.Count > 0 ? _rootNodes[0].FullPath : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        await NavigateTo(initialPath);
    }

    private static void AddPlaceholder(DirectoryNode node)
    {
        node.Children.Clear();
        node.Children.Add(new DirectoryNode
        {
            Name = "...",
            FullPath = string.Empty,
            IsPlaceholder = true
        });
    }

    private static bool HasSubDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Any();
        }
        catch
        {
            return false;
        }
    }

    private static string GetFriendlySize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int index = 0;
        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return index == 0 ? $"{value:0} {suffixes[index]}" : $"{value:0.##} {suffixes[index]}";
    }

    private bool _isNavigating;

    private async Task NavigateTo(string path, bool addToHistory = true)
    {
        if (_isNavigating) return;
        _isNavigating = true;

        try
        {
            try
            {
                path = Path.GetFullPath(path);
            }
            catch
            {
                // Keep original path if normalization fails
            }

            if (!Directory.Exists(path))
            {
                StatusText.Text = $"Path not found: {path}";
                return;
            }

            AddToRecentPaths(path);

            if (addToHistory && !_isNavigatingFromHistory && !string.IsNullOrWhiteSpace(_currentPath))
            {
                _backHistory.Push(_currentPath);
                _forwardHistory.Clear();
            }

            try
            {
                string fullPath = Path.GetFullPath(path);
                if (string.IsNullOrWhiteSpace(_currentPath) || !string.Equals(fullPath, Path.GetFullPath(_currentPath), StringComparison.OrdinalIgnoreCase))
                {
                    _currentSearchTerm = null;
                    if (SearchTextBox != null && !string.IsNullOrEmpty(SearchTextBox.Text))
                    {
                        _isClearingSearchFromNavigation = true;
                        SearchTextBox.Text = string.Empty;
                        _isClearingSearchFromNavigation = false;
                    }
                }
            }
            catch { }

            _currentPath = path;
            PathTextBox.Text = path;
            UpButton.IsEnabled = Directory.GetParent(path) is not null;
            BackButton.IsEnabled = _backHistory.Count > 0;
            ForwardButton.IsEnabled = _forwardHistory.Count > 0;

            // Reset view mode to List View by default when opening/navigating to a folder
            SetViewMode(false);

            StatusText.Text = "Loading...";
            await RefreshItemsAsync(path, _currentSearchTerm);
            ExpandTreeToPath(path);
            ClearPreviewPane();
            StatusText.Text = "Ready";

            SetupFileWatcher(path);
        }
        finally
        {
            _isNavigating = false;
        }
    }

    private async Task RefreshItemsAsync(string path, string? searchTerm = null)
    {
        Dispatcher.Invoke(() =>
        {
            if (HeaderMarkAllCheck != null)
            {
                HeaderMarkAllCheck.IsChecked = false;
            }
            foreach (var item in _items)
            {
                item.PropertyChanged -= Item_PropertyChanged;
            }
            _items.Clear();
        });
        try
        {
            List<ExplorerItem> loadedItems = await Task.Run(() =>
            {
                var list = new List<ExplorerItem>();

                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    // Recursive search through subfolders (capped at 5000 results)
                    SearchRecursive(path, searchTerm.ToLowerInvariant(), list);
                }
                else
                {
                    // Standard non-recursive load of current directory
                    // Get directories
                    try
                    {
                        foreach (string dir in Directory.GetDirectories(path))
                        {
                            try
                            {
                                var info = new DirectoryInfo(dir);
                                list.Add(new ExplorerItem
                                {
                                    Name = info.Name,
                                    FullPath = info.FullName,
                                    Type = "File folder",
                                    Modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                                    LengthValue = -1
                                });
                            }
                            catch
                            {
                                // Skip folders that throw exceptions on read/permissions
                            }
                        }
                    }
                    catch
                    {
                        // Ignore directory retrieval failure
                    }

                    // Get files
                    try
                    {
                        foreach (string file in Directory.GetFiles(path))
                        {
                            try
                            {
                                var info = new FileInfo(file);
                                var stem = Path.GetFileNameWithoutExtension(file);
                                var tag = FileSorter.ExtractTagFromFileName(stem) ?? "";

                                string sizeStr = "—";
                                long len = -1;
                                try
                                {
                                    len = info.Length;
                                    sizeStr = GetFriendlySize(len);
                                }
                                catch { }

                                string modStr = "—";
                                try
                                {
                                    modStr = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                                }
                                catch { }

                                list.Add(new ExplorerItem
                                {
                                    Name = info.Name,
                                    FullPath = info.FullName,
                                    Type = info.Extension.Length > 1 ? $"{info.Extension[1..].ToUpperInvariant()} File" : "File",
                                    Size = sizeStr,
                                    Modified = modStr,
                                    Tag = tag,
                                    LengthValue = len
                                });
                            }
                            catch
                            {
                                // Skip files that throw exceptions (e.g. exclusively locked files)
                            }
                        }
                    }
                    catch
                    {
                        // Ignore file retrieval failure
                    }
                }

                return list;

                void SearchRecursive(string currentDir, string term, List<ExplorerItem> results)
                {
                    if (results.Count >= 5000) return;

                    // Search files in currentDir
                    try
                    {
                        foreach (string file in Directory.GetFiles(currentDir))
                        {
                            if (results.Count >= 5000) return;

                            try
                            {
                                var info = new FileInfo(file);
                                var stem = Path.GetFileNameWithoutExtension(file);
                                var tag = FileSorter.ExtractTagFromFileName(stem) ?? "";

                                if (info.Name.ToLowerInvariant().Contains(term) ||
                                    tag.ToLowerInvariant().Contains(term))
                                {
                                    string sizeStr = "—";
                                    long len = -1;
                                    try
                                    {
                                        len = info.Length;
                                        sizeStr = GetFriendlySize(len);
                                    }
                                    catch { }

                                    string modStr = "—";
                                    try
                                    {
                                        modStr = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                                    }
                                    catch { }

                                    results.Add(new ExplorerItem
                                    {
                                        Name = info.Name,
                                        FullPath = info.FullName,
                                        Type = info.Extension.Length > 1 ? $"{info.Extension[1..].ToUpperInvariant()} File" : "File",
                                        Size = sizeStr,
                                        Modified = modStr,
                                        Tag = tag,
                                        LengthValue = len
                                    });
                                }
                            }
                            catch
                            {
                                // Skip files that throw exceptions on read
                            }
                        }
                    }
                    catch
                    {
                        // Skip folder if it throws exceptions on get files
                    }

                    // Search directories in currentDir and recurse
                    try
                    {
                        foreach (string subDir in Directory.GetDirectories(currentDir))
                        {
                            if (results.Count >= 5000) return;

                            try
                            {
                                var info = new DirectoryInfo(subDir);
                                if (info.Name.ToLowerInvariant().Contains(term))
                                {
                                    results.Add(new ExplorerItem
                                    {
                                        Name = info.Name,
                                        FullPath = info.FullName,
                                        Type = "File folder",
                                        Modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                                        LengthValue = -1
                                    });
                                }

                                // Recurse
                                SearchRecursive(subDir, term, results);
                            }
                            catch
                            {
                                // Skip directory exceptions
                            }
                        }
                    }
                    catch
                    {
                        // Skip folder if it throws exceptions on get directories
                    }
                }
            });

            Dispatcher.Invoke(() =>
            {
                foreach (var item in loadedItems)
                {
                    item.PropertyChanged += Item_PropertyChanged;
                    _items.Add(item);
                }
                ApplySorting();
            });
            
            // Kick off async thumbnail loading if in grid/thumbnail view
            if (_appSettings.IsThumbnailView)
            {
                LoadThumbnailsAsync(path);
            }
        }
        catch (UnauthorizedAccessException)
        {
            StatusText.Text = "Access denied to this folder.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }

        UpdateStatusBar();
    }


    private void UpdateStatusBar()
    {
        int folderCount = _items.Count(i => i.Type == "File folder");
        int fileCount = _items.Count(i => i.Type != "File folder");
        FileCountText.Text = $"{folderCount} folders, {fileCount} files";

        int selectedCount = ItemsList.SelectedItems.Count;
        SelectionCountText.Text = selectedCount > 0 ? $"{selectedCount} selected" : "";

        try
        {
            if (!string.IsNullOrWhiteSpace(_currentPath))
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(_currentPath)!);
                long freeSpace = driveInfo.AvailableFreeSpace;
                FreeSpaceText.Text = $"{GetFriendlySize(freeSpace)} free";
            }
        }
        catch
        {
            FreeSpaceText.Text = "";
        }
    }

    private void ExpandTreeToPath(string path)
    {
        // Keep this lightweight: tree selection sync is best effort.
        foreach (DirectoryNode root in _rootNodes)
        {
            if (path.StartsWith(root.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                EnsureChildrenLoaded(root);
                break;
            }
        }
    }

    private void EnsureChildrenLoaded(DirectoryNode node)
    {
        if (node.Children.Count == 1 && node.Children[0].IsPlaceholder)
        {
            node.Children.Clear();
            try
            {
                foreach (string dir in Directory.GetDirectories(node.FullPath))
                {
                    var child = new DirectoryNode
                    {
                        Name = Path.GetFileName(dir),
                        FullPath = dir
                    };

                    if (HasSubDirectories(dir))
                    {
                        AddPlaceholder(child);
                    }

                    node.Children.Add(child);
                }
            }
            catch
            {
                // Ignore inaccessible folders.
            }
        }
    }

    private async void FolderTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (FolderTree.SelectedItem is DirectoryNode node && !node.IsPlaceholder)
        {
            EnsureChildrenLoaded(node);
            await NavigateTo(node.FullPath);
        }
    }

    /*
    private void LeftTabFolders_Click(object sender, RoutedEventArgs e)
    {
        LeftTabFolders.IsChecked = true;
        FoldersTabPanel.Visibility = Visibility.Visible;
        SortPanel.Visibility = Visibility.Collapsed;
        RenamePanel.Visibility = Visibility.Collapsed;
        FavoritesPanel.Visibility = Visibility.Collapsed;
    }

    private void LeftTabSort_Click(object sender, RoutedEventArgs e)
    {
        LeftTabSort.IsChecked = true;
        FoldersTabPanel.Visibility = Visibility.Collapsed;
        SortPanel.Visibility = Visibility.Visible;
        RenamePanel.Visibility = Visibility.Collapsed;
        FavoritesPanel.Visibility = Visibility.Collapsed;
    }
    */

    // ── Favorites Management ─────────────────────────────────────────────────
    private void LoadFavorites()
    {
        _favorites.Clear();
        try
        {
            if (File.Exists(FavoritesFilePath))
            {
                string json = File.ReadAllText(FavoritesFilePath);
                var items = JsonSerializer.Deserialize<List<BookmarkItem>>(json);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        _favorites.Add(item);
                    }
                }
            }
        }
        catch
        {
            // Silently fail if we can't load favorites
        }
    }

    private void SaveFavorites()
    {
        try
        {
            string directory = Path.GetDirectoryName(FavoritesFilePath)!;
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(_favorites.ToList());
            File.WriteAllText(FavoritesFilePath, json);
        }
        catch
        {
            // Silently fail if we can't save favorites
        }
    }

    private void AddFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentPath))
        {
            StatusText.Text = "No folder selected to add to favorites.";
            return;
        }

        string folderName = new DirectoryInfo(_currentPath).Name;
        var existing = _favorites.FirstOrDefault(f => f.Path == _currentPath);
        if (existing != null)
        {
            StatusText.Text = "This folder is already in favorites.";
            return;
        }

        _favorites.Add(new BookmarkItem
        {
            Name = folderName,
            Path = _currentPath
        });
        SaveFavorites();
        StatusText.Text = $"Added '{folderName}' to favorites.";
    }

    private void RemoveFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is BookmarkItem item)
        {
            _favorites.Remove(item);
            SaveFavorites();
            StatusText.Text = $"Removed '{item.Name}' from favorites.";
        }
    }

    private void FavoritesList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoritesList.SelectedItem is BookmarkItem item)
        {
            e.Handled = true;
            if (Directory.Exists(item.Path))
            {
                string path = item.Path;
                Dispatcher.BeginInvoke(async () => await NavigateTo(path));
            }
            else
            {
                StatusText.Text = "Folder no longer exists.";
                _favorites.Remove(item);
                SaveFavorites();
            }
        }
    }

    private void PinnedFoldersList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PinnedFoldersList.SelectedItem is BookmarkItem item)
        {
            e.Handled = true;
            if (Directory.Exists(item.Path))
            {
                string path = item.Path;
                Dispatcher.BeginInvoke(async () => await NavigateTo(path));
            }
            else
            {
                StatusText.Text = "Folder no longer exists.";
                _favorites.Remove(item);
                SaveFavorites();
            }
        }
    }


    /*
    private void LeftTabRename_Click(object sender, RoutedEventArgs e)
    {
        LeftTabRename.IsChecked = true;
        FoldersTabPanel.Visibility = Visibility.Collapsed;
        SortPanel.Visibility = Visibility.Collapsed;
        RenamePanel.Visibility = Visibility.Visible;
        FavoritesPanel.Visibility = Visibility.Collapsed;
    }

    private void LeftTabFavorites_Click(object sender, RoutedEventArgs e)
    {
        LeftTabFavorites.IsChecked = true;
        FoldersTabPanel.Visibility = Visibility.Collapsed;
        SortPanel.Visibility = Visibility.Collapsed;
        RenamePanel.Visibility = Visibility.Collapsed;
        FavoritesPanel.Visibility = Visibility.Visible;
        LoadFavorites();
    }
    */

    private void AddDateCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        // RenameDatePicker.Visibility = Visibility.Visible; // TODO: Element not in new XAML
    }

    private void AddDateCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        // RenameDatePicker.Visibility = Visibility.Collapsed; // TODO: Element not in new XAML
    }

    private void AddTagCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        // TagComboBoxGrid.Visibility = Visibility.Visible; // TODO: Element not in new XAML
    }

    private void AddTagCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        // TagComboBoxGrid.Visibility = Visibility.Collapsed; // TODO: Element not in new XAML
    }

    private void NextNameButton_Click(object sender, RoutedEventArgs e)
    {
        // Apply rename and move to next file
        // TODO: Implement rename logic
        System.Windows.MessageBox.Show("Apply rename and move to next file");
    }

    private void AddTagButton_Click(object sender, RoutedEventArgs e)
    {
        // Show popup dialog to add new tag
        var inputDialog = new Window
        {
            Title = "Add New Tag",
            Width = 300,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = System.Windows.ResizeMode.NoResize
        };

        var stackPanel = new StackPanel { Margin = new Thickness(20) };
        var textBlock = new TextBlock { Text = "Enter new tag name:", Margin = new Thickness(0, 0, 0, 10) };
        var textBox = new System.Windows.Controls.TextBox { Height = 26, Margin = new Thickness(0, 0, 0, 10) };
        var button = new Button { Content = "Add", Height = 32, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Width = 80 };

        button.Click += (s, args) =>
        {
            if (!string.IsNullOrWhiteSpace(textBox.Text))
            {
                // TagComboBox.Items.Add(new ComboBoxItem { Content = textBox.Text }); // TODO: Element not in new XAML
                // TagComboBox.SelectedItem = TagComboBox.Items[TagComboBox.Items.Count - 1];
                inputDialog.Close();
            }
        };

        stackPanel.Children.Add(textBlock);
        stackPanel.Children.Add(textBox);
        stackPanel.Children.Add(button);
        inputDialog.Content = stackPanel;

        inputDialog.ShowDialog();
    }

    private void ItemsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsList.SelectedItem is not ExplorerItem item)
        {
            return;
        }

        e.Handled = true;

        if (item.Type == "File folder")
        {
            string path = item.FullPath;
            Dispatcher.BeginInvoke(async () => await NavigateTo(path));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.FullPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to open file: {ex.Message}";
        }
    }

    private void ItemsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        List<ExplorerItem> selected = ItemsList.SelectedItems.OfType<ExplorerItem>().ToList();
        List<ExplorerItem> files = selected.Where(i => i.Type != "File folder").ToList();

        UpdateStatusBar();

        if (files.Count > 0)
        {
            ExplorerItem first = files[0];
            StatusText.Text = files.Count > 1
                ? $"{files.Count} files selected — first: {first.Name} ({first.Size})"
                : $"{first.Name} | {first.Type} | {first.Size}";
            UpdatePreviewPane(first);
        }
        else if (selected.Count == 1)
        {
            ExplorerItem only = selected[0];
            StatusText.Text = $"{only.Name} | {only.Type} | {only.Size}";
            UpdatePreviewPane(only);
        }
        else if (selected.Count > 1)
        {
            StatusText.Text = $"{selected.Count} folders selected";
            ClearPreviewPane();
        }
        else
        {
            ClearPreviewPane();
        }

        if (_isRenameMode)
        {
            SyncSmartPanelWithSelection(selected.Count > 0 ? selected[0] : null);
        }
    }

    private void ClearPreviewPane()
    {
        _previewRequestId++;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewScrollHost.Visibility = Visibility.Visible;
        PreviewPdfView.Visibility = Visibility.Collapsed;
        if (PreviewPdfView.CoreWebView2 is not null)
        {
            PreviewPdfView.CoreWebView2.Navigate("about:blank");
        }
        PreviewTextBox.Visibility = Visibility.Collapsed;
        PreviewTextBox.Text = string.Empty;
        PreviewMessageText.Visibility = Visibility.Collapsed;
        PreviewMessageText.Text = string.Empty;
        PreviewHeaderText.Text = "Preview";
        PreviewHintText.Visibility = Visibility.Visible;
        PreviewHintText.Text = "Select a file to see a preview.";
        ZoomRow.Visibility = Visibility.Collapsed;

        DetailsOriginalName.Text = "-";
        DetailsType.Text = "-";
        DetailsSize.Text = "-";
        DetailsDate.Text = "-";
        DetailsFolder.Text = "-";
        
        _isUpdatingDetails = true;
        DetailsNewName.Text = "";
        DetailsNewName.IsEnabled = false;
        _isUpdatingDetails = false;
    }

    private enum FileType
    {
        Unknown,
        Image,
        Pdf,
        Text
    }

    private static FileType DetectFileType(string path)
    {
        try
        {
            if (!File.Exists(path)) return FileType.Unknown;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length == 0) return FileType.Text;

                var header = new byte[Math.Min(16, fs.Length)];
                int bytesRead = fs.Read(header, 0, header.Length);
                if (bytesRead >= 4)
                {
                    // Check PDF: %PDF
                    if (header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46)
                    {
                        return FileType.Pdf;
                    }

                    // Check PNG: 89 50 4E 47
                    if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                    {
                        return FileType.Image;
                    }

                    // Check JPEG: FF D8 FF
                    if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                    {
                        return FileType.Image;
                    }

                    // Check GIF: GIF8 (47 49 46 38)
                    if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38)
                    {
                        return FileType.Image;
                    }

                    // Check BMP: BM (42 4D)
                    if (header[0] == 0x42 && header[1] == 0x4D)
                    {
                        return FileType.Image;
                    }

                    // Check ICO: 00 00 01 00
                    if (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && header[3] == 0x00)
                    {
                        return FileType.Image;
                    }

                    // Check WEBP: RIFF....WEBP
                    if (bytesRead >= 12 &&
                        header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                        header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                    {
                        return FileType.Image;
                    }

                    // Check TIFF: II* (49 49 2A 00) or MM (4D 4D 00 2A)
                    if ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00) ||
                        (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A))
                    {
                        return FileType.Image;
                    }
                }
            }

            if (IsLikelyTextFile(path))
            {
                return FileType.Text;
            }
        }
        catch
        {
            // Ignore
        }

        return FileType.Unknown;
    }

    private void SetPreviewVisibility(FileType type)
    {
        PreviewHintText.Visibility = Visibility.Collapsed;
        PreviewMessageText.Visibility = Visibility.Collapsed;

        if (type != FileType.Image)
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;
        }
        if (type != FileType.Text)
        {
            PreviewTextBox.Visibility = Visibility.Collapsed;
            PreviewTextBox.Text = string.Empty;
        }
        if (type != FileType.Pdf)
        {
            if (PreviewPdfView.CoreWebView2 is not null && PreviewPdfView.Visibility == Visibility.Visible)
            {
                PreviewPdfView.CoreWebView2.Navigate("about:blank");
            }
            PreviewPdfView.Visibility = Visibility.Collapsed;
        }
        
        if (type == FileType.Pdf)
        {
            PreviewScrollHost.Visibility = Visibility.Collapsed;
            PreviewPdfView.Visibility = Visibility.Visible;
        }
        else
        {
            PreviewScrollHost.Visibility = Visibility.Visible;
        }

        ZoomRow.Visibility = (type == FileType.Image || type == FileType.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePreviewPane(ExplorerItem item)
    {
        int requestId = ++_previewRequestId;
        PreviewHeaderText.Text = item.Name;
        if (ZoomSlider is not null)
        {
            ZoomSlider.Value = 100;
        }

        DetailsOriginalName.Text = item.Name;
        DetailsType.Text = item.Type;
        DetailsSize.Text = item.Size;
        DetailsDate.Text = item.Modified;
        DetailsFolder.Text = Path.GetDirectoryName(item.FullPath) ?? "-";

        _isUpdatingDetails = true;
        DetailsNewName.Text = item.NewName;
        DetailsNewName.IsEnabled = true;
        _isUpdatingDetails = false;

        if (item.Type == "File folder")
        {
            SetPreviewVisibility(FileType.Unknown);
            PreviewMessageText.Text = "Folders are not previewed. Open the folder or select a file.";
            PreviewMessageText.Visibility = Visibility.Visible;
            return;
        }

        if (!File.Exists(item.FullPath))
        {
            SetPreviewVisibility(FileType.Unknown);
            PreviewMessageText.Text = "File no longer exists.";
            PreviewMessageText.Visibility = Visibility.Visible;
            return;
        }

        string ext = Path.GetExtension(item.FullPath).ToLowerInvariant();
        string baseName = Path.GetFileName(item.FullPath).ToLowerInvariant();

        try
        {
            FileType detected = DetectFileType(item.FullPath);
            if (detected == FileType.Image)
            {
                ShowImagePreview(item.FullPath);
                return;
            }
            if (detected == FileType.Pdf)
            {
                _ = ShowPdfPreviewAsync(item.FullPath, requestId);
                return;
            }
            if (detected == FileType.Text)
            {
                ShowTextPreview(item.FullPath);
                return;
            }

            if (ImageExtensions.Contains(ext))
            {
                ShowImagePreview(item.FullPath);
                return;
            }
            if (PdfExtensions.Contains(ext))
            {
                _ = ShowPdfPreviewAsync(item.FullPath, requestId);
                return;
            }

            if (TextExtensions.Contains(ext)
                || TextFileBasenames.Contains(baseName)
                || IsLikelyTextFile(item.FullPath))
            {
                ShowTextPreview(item.FullPath);
                return;
            }
        }
        catch (Exception ex)
        {
            SetPreviewVisibility(FileType.Unknown);
            PreviewMessageText.Text = $"Could not load preview: {ex.Message}";
            PreviewMessageText.Visibility = Visibility.Visible;
            return;
        }

        SetPreviewVisibility(FileType.Unknown);
        PreviewMessageText.Text = "No built-in preview for this file type. Double-click to open with the default app.";
        PreviewMessageText.Visibility = Visibility.Visible;
    }

    private static bool IsLikelyTextFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0)
            {
                return true;
            }

            int n = (int)Math.Min(8192, fs.Length);
            var buffer = new byte[n];
            fs.ReadExactly(buffer, 0, n);
            return !buffer.Any(b => b == 0);
        }
        catch
        {
            return false;
        }
    }

    private void ShowImagePreview(string fullPath)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(Path.GetFullPath(fullPath));
        bmp.EndInit();
        bmp.Freeze();
        
        SetPreviewVisibility(FileType.Image);
        PreviewImage.Source = bmp;
        ApplyZoom();
        PreviewImage.Visibility = Visibility.Visible;
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomValueText is null)
            return;
        
        ZoomValueText.Text = $"{e.NewValue:F0}%";
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (ZoomSlider is null) return;
        double zoomLevel = ZoomSlider.Value / 100.0;

        if (PreviewImage is not null && PreviewImage.Source is not null)
        {
            PreviewImage.LayoutTransform = new System.Windows.Media.ScaleTransform(zoomLevel, zoomLevel);
        }

        if (PreviewTextBox is not null && PreviewTextBox.Visibility == Visibility.Visible)
        {
            PreviewTextBox.FontSize = 11.0 * zoomLevel;
        }
    }

    private void PreviewThumbContainer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            double step = 10.0;
            if (e.Delta > 0)
            {
                ZoomSlider.Value = Math.Min(ZoomSlider.Maximum, ZoomSlider.Value + step);
            }
            else if (e.Delta < 0)
            {
                ZoomSlider.Value = Math.Max(ZoomSlider.Minimum, ZoomSlider.Value - step);
            }
        }
    }

    private void PreviewThumbContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        PreviewThumbContainer.Focus();
    }

    private void FocusActivePreviewControl()
    {
        if (PreviewTextBox != null && PreviewTextBox.Visibility == Visibility.Visible)
        {
            PreviewTextBox.Focus();
        }
        else if (PreviewPdfView != null && PreviewPdfView.Visibility == Visibility.Visible)
        {
            PreviewPdfView.Focus();
        }
        else if (PreviewThumbContainer != null)
        {
            PreviewThumbContainer.Focus();
        }
    }

    private void NavigateFileListFromPreview(bool isShiftDown)
    {
        if (ItemsList == null || ItemsList.Items.Count == 0)
        {
            return;
        }

        int currentIndex = ItemsList.SelectedIndex;
        int nextIndex;

        if (isShiftDown)
        {
            nextIndex = currentIndex - 1;
            if (nextIndex < 0)
            {
                nextIndex = ItemsList.Items.Count - 1;
            }
        }
        else
        {
            nextIndex = currentIndex + 1;
            if (nextIndex >= ItemsList.Items.Count || nextIndex < 0)
            {
                nextIndex = 0;
            }
        }

        ItemsList.SelectedIndex = nextIndex;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);

        // Re-apply focus on the correct active control in the preview pane
        // We use Dispatcher to ensure layout/visibility has updated after the selection change.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            FocusActivePreviewControl();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private bool IsFocusInPreviewPane()
    {
        if (PreviewPane == null) return false;

        DependencyObject? focused = Keyboard.FocusedElement as DependencyObject;
        if (focused == null) return false;

        if (focused == PreviewPane || focused == PreviewThumbContainer || focused == PreviewTextBox || focused == PreviewPdfView || focused == PreviewImage)
        {
            return true;
        }

        DependencyObject? current = focused;
        while (current != null)
        {
            if (current == PreviewPane)
            {
                return true;
            }
            DependencyObject? parent = VisualTreeHelper.GetParent(current);
            if (parent == null && current is FrameworkElement fe)
            {
                parent = fe.Parent;
            }
            current = parent;
        }

        return false;
    }

    private void ComponentDispatcher_ThreadFilterMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        const int VK_TAB = 0x09;

        if (msg.message == WM_KEYDOWN && msg.wParam.ToInt32() == VK_TAB)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (IsFocusInPreviewPane())
                {
                    bool isShiftDown = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                    NavigateFileListFromPreview(isShiftDown);
                    handled = true;
                }
            }
        }
    }

    private void ShowTextPreview(string fullPath)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long len = fs.Length;
        int toRead = (int)Math.Min(MaxTextPreviewBytes, len);
        var buffer = new byte[toRead];
        fs.ReadExactly(buffer, 0, toRead);
        string text = Encoding.UTF8.GetString(buffer);
        if (len > MaxTextPreviewBytes)
        {
            text += Environment.NewLine + Environment.NewLine + "... (preview truncated)";
        }

        SetPreviewVisibility(FileType.Text);
        PreviewTextBox.Text = text;
        PreviewTextBox.Visibility = Visibility.Visible;
        ApplyZoom();
    }

    private string? _pdfPreviewInitializationError;

    private async Task EnsurePdfPreviewReadyAsync()
    {
        if (_isPdfPreviewReady)
        {
            return;
        }

        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FileExplorerCS_WebView2");
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: tempDir);
            await PreviewPdfView.EnsureCoreWebView2Async(env);
            
            // Set background color to transparent to prevent white flashing
            PreviewPdfView.DefaultBackgroundColor = System.Drawing.Color.Transparent;

            // Wire up navigation completed event to transition visibility smoothly
            PreviewPdfView.NavigationCompleted += PreviewPdfView_NavigationCompleted;

            _isPdfPreviewReady = true;
            _pdfPreviewInitializationError = null;
        }
        catch (Exception ex)
        {
            _isPdfPreviewReady = false;
            _pdfPreviewInitializationError = ex.Message;
        }
    }

    private void PreviewPdfView_NavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_lastNavigatedPdfRequestId == _previewRequestId)
        {
            // Specifically ignore cancelled navigations to prevent flash of wrong/incomplete pages
            if (e.WebErrorStatus == Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.OperationCanceled)
            {
                return;
            }
            SetPreviewVisibility(FileType.Pdf);
        }
    }

    private async Task ShowPdfPreviewAsync(string fullPath, int requestId)
    {
        await EnsurePdfPreviewReadyAsync();
        if (!_isPdfPreviewReady || requestId != _previewRequestId)
        {
            SetPreviewVisibility(FileType.Unknown);
            PreviewMessageText.Text = "PDF preview is unavailable. Details: " + (_pdfPreviewInitializationError ?? "WebView2 runtime may be missing.");
            PreviewMessageText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            string absolutePath = Path.GetFullPath(fullPath);
            string fileUrl = new Uri(absolutePath).AbsoluteUri;
            
            _lastNavigatedPdfRequestId = requestId;
            
            // Ensure background remains transparent (in case theme changed or reset)
            PreviewPdfView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            
            PreviewPdfView.CoreWebView2.Navigate(fileUrl);
        }
        catch (Exception ex)
        {
            SetPreviewVisibility(FileType.Unknown);
            PreviewMessageText.Text = $"Could not load PDF preview: {ex.Message}";
            PreviewMessageText.Visibility = Visibility.Visible;
        }
    }

    private void RibbonTab_Click(object sender, RoutedEventArgs e)
    {
        PanelHome.Visibility = TabHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelRename.Visibility = TabRename.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelOrganize.Visibility = TabOrganize.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelPdf.Visibility = TabPdf.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelHelp.Visibility = TabHelp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        SetRenameMode(TabRename.IsChecked == true);

        // Automatically switch to Details tab when Rename tab is selected
        if (TabRename.IsChecked == true && InspectorDetailsTab != null)
        {
            InspectorDetailsTab.IsChecked = true;
        }
    }

    private async void OpenFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            ShowNewFolderButton = true,
            Description = "Select folder"
        };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
        {
            await NavigateTo(dlg.SelectedPath);
        }
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentPath))
        {
            await NavigateTo(_currentPath);
        }
    }

    private void SearchButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SearchTextBox != null)
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
        }
    }

    private void SearchTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isClearingSearchFromNavigation) return;

        if (_searchDebounceTimer == null)
        {
            _searchDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _searchDebounceTimer.Tick += async (s, ev) =>
            {
                _searchDebounceTimer.Stop();
                await PerformSearchAsync(SearchTextBox.Text);
            };
        }
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private async void SearchTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            _searchDebounceTimer?.Stop();
            e.Handled = true;
            await PerformSearchAsync(SearchTextBox.Text);
        }
    }

    private async Task PerformSearchAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;

        string query = text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _currentSearchTerm = null;
            await RefreshItemsAsync(_currentPath);
            StatusText.Text = "Search cleared.";
            StatusDot.Fill = System.Windows.Media.Brushes.Transparent;
        }
        else
        {
            _currentSearchTerm = query.ToLowerInvariant();
            await RefreshItemsAsync(_currentPath, _currentSearchTerm);
            StatusText.Text = $"Filtered by search term: \"{query}\"";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
        }
    }

    private void SelectAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        ItemsList.SelectAll();
    }

    private void InvertSelectionButton_OnClick(object sender, RoutedEventArgs e)
    {
        var allItems = ItemsList.Items.OfType<ExplorerItem>().ToList();
        var selected = ItemsList.SelectedItems.OfType<ExplorerItem>().ToHashSet();

        ItemsList.SelectedItems.Clear();
        foreach (var item in allItems)
        {
            if (!selected.Contains(item))
            {
                ItemsList.SelectedItems.Add(item);
            }
        }
    }

    private void TogglePreviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        // Toggle preview pane visibility
        // TODO: Implement preview toggle functionality
    }

    private void ToggleDetailsButton_OnClick(object sender, RoutedEventArgs e)
    {
        // Toggle details view implementation
    }

    private void ConvertPdfButton_OnClick(object sender, RoutedEventArgs e)
    {
        ConvertToPdfButton_OnClick(sender, e);
    }

    private void BrowseArchive_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            ShowNewFolderButton = true,
            Description = "Choose the root folder for sorted files (YYYY-MM / Tag).",
            UseDescriptionForTitle = true
        };

        if (!string.IsNullOrWhiteSpace(ArchivePathTextBox.Text))
        {
            try
            {
                dlg.SelectedPath = Path.GetFullPath(ArchivePathTextBox.Text.Trim());
            }
            catch
            {
            }
        }

        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
        {
            ArchivePathTextBox.Text = dlg.SelectedPath;
        }
    }

    private async void MoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ItemsList.SelectedItems.OfType<ExplorerItem>().ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Please select one or more items to move.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange
            return;
        }

        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Choose destination folder to move selected files",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
        {
            string targetPath = dlg.SelectedPath;
            if (!Directory.Exists(targetPath)) return;

            if (string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(_currentPath), StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "Destination is the same as the current folder.";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange
                return;
            }

            StatusProgressBar.Visibility = Visibility.Visible;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange
            StatusText.Text = "Moving items...";

            var progress = new Progress<string>(msg =>
            {
                StatusText.Text = msg;
            });

            try
            {
                var sourcePaths = selected.Select(i => i.FullPath).ToList();
                var result = await _fileOperationService.MoveItemsAsync(sourcePaths, targetPath, progress);

                if (result.SucceededMoves.Count > 0)
                {
                    var mappings = result.SucceededMoves.ToList();
                    _undoStack.Push(("move button", () => UndoMoveOperationAsync(mappings)));
                }

                if (!result.Success)
                {
                    var firstErr = result.FailedPaths.First();
                    throw new Exception($"Failed to move item {Path.GetFileName(firstErr.Path)}: {firstErr.Exception.Message}");
                }

                StatusText.Text = $"Moved {result.SucceededMoves.Count} item(s) to {Path.GetFileName(targetPath)}.";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
                RefreshAfterMutation();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Unable to move files: {ex.Message}";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
                RefreshAfterMutation();
            }
            finally
            {
                StatusProgressBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OpenArchive_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ArchivePathTextBox.Text))
        {
            try
            {
                string path = Path.GetFullPath(ArchivePathTextBox.Text.Trim());
                if (Directory.Exists(path))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }
                else
                {
                    StatusText.Text = "Archive path does not exist.";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error opening archive: " + ex.Message;
            }
        }
    }

    private async void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_backHistory.Count == 0 || string.IsNullOrWhiteSpace(_currentPath))
        {
            return;
        }

        _forwardHistory.Push(_currentPath);
        _isNavigatingFromHistory = true;
        string target = _backHistory.Pop();
        await NavigateTo(target, addToHistory: false);
        _isNavigatingFromHistory = false;
    }

    private async void ForwardButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_forwardHistory.Count == 0 || string.IsNullOrWhiteSpace(_currentPath))
        {
            return;
        }

        _backHistory.Push(_currentPath);
        _isNavigatingFromHistory = true;
        string target = _forwardHistory.Pop();
        await NavigateTo(target, addToHistory: false);
        _isNavigatingFromHistory = false;
    }

    private async void UpButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentPath))
        {
            return;
        }

        DirectoryInfo? parent = Directory.GetParent(_currentPath);
        if (parent is not null)
        {
            await NavigateTo(parent.FullName);
        }
    }

    private async void PathTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            await NavigateTo(PathTextBox.Text.Trim());
        }
    }


    private async void Window_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase)
        {
            return;
        }

        // F2 to Rename
        if (e.Key == System.Windows.Input.Key.F2)
        {
            RenameButton_OnClick(sender, e);
            e.Handled = true;
            return;
        }

        // Delete key to Delete
        if (e.Key == System.Windows.Input.Key.Delete)
        {
            DeleteButton_OnClick(sender, e);
            e.Handled = true;
            return;
        }

        // Ctrl shortcuts
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Key == System.Windows.Input.Key.A)
            {
                ItemsList.SelectAll();
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.C)
            {
                ContextMenu_Copy_Click(sender, e);
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.X)
            {
                ContextMenu_Cut_Click(sender, e);
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.V)
            {
                ContextMenu_Paste_Click(sender, e);
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.Z)
            {
                await ExecuteUndoAsync();
                e.Handled = true;
                return;
            }
        }
    }

    private async void UndoButton_OnClick(object sender, RoutedEventArgs e)
    {
        await ExecuteUndoAsync();
    }

    private async Task ExecuteUndoAsync()
    {
        if (_undoStack.Count == 0)
        {
            StatusText.Text = "No actions to undo.";
            return;
        }

        var (description, undoFunc) = _undoStack.Pop();
        try
        {
            StatusText.Text = $"Undoing last {description}...";
            StatusProgressBar.Visibility = Visibility.Visible;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange

            await undoFunc();

            StatusText.Text = $"Successfully undid last {description}.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
            RefreshAfterMutation();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Undo {description} failed: {ex.Message}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
        }
        finally
        {
            StatusProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private Task UndoRenameBatchAsync(List<(string OldPath, string NewPath)> batch)
    {
        return Task.Run(() =>
        {
            int successCount = 0;
            int failCount = 0;

            for (int i = batch.Count - 1; i >= 0; i--)
            {
                var pair = batch[i];
                try
                {
                    if (File.Exists(pair.NewPath))
                    {
                        string? dir = Path.GetDirectoryName(pair.OldPath);
                        if (dir != null) Directory.CreateDirectory(dir);
                        
                        File.Move(pair.NewPath, pair.OldPath);
                        successCount++;
                    }
                    else if (Directory.Exists(pair.NewPath))
                    {
                        string? dir = Path.GetDirectoryName(pair.OldPath);
                        if (dir != null) Directory.CreateDirectory(dir);
                        
                        Directory.Move(pair.NewPath, pair.OldPath);
                        successCount++;
                    }
                    else
                    {
                        failCount++;
                    }
                }
                catch
                {
                    failCount++;
                }
            }

            if (failCount > 0)
            {
                throw new Exception($"{failCount} item(s) failed to restore during undo rename.");
            }
        });
    }

    private static readonly string[] RestoreVerbTokens = new[]
    {
        "restore", "estore", "wiederherstellen", "restaurar", "restaurer", 
        "ripristina", "восстановить", "元に戻す", "还原", "還原", "복원", 
        "herstellen", "przywróć", "przywroc", "geri yükle", "geri yukle",
        "återställ", "aterstall", "gjenopprett", "gendan", "palauta"
    };

    private static Task RunInSTAThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private Task RestoreDeletedItemsAsync(List<string> originalPaths)
    {
        return RunInSTAThreadAsync(() =>
        {
            dynamic? shell = null;
            dynamic? recycleBin = null;
            try
            {
                Type? shellAppType = Type.GetTypeFromProgID("Shell.Application");
                if (shellAppType == null) throw new InvalidOperationException("Shell.Application COM class not registered.");

                shell = Activator.CreateInstance(shellAppType)!;
                recycleBin = shell.NameSpace(10); // ssfBITBUCKET
                if (recycleBin == null) throw new InvalidOperationException("Could not open Recycle Bin namespace.");

                int originalLocationIdx = GetOriginalLocationColumnIndex(recycleBin);
                if (originalLocationIdx == -1)
                {
                    originalLocationIdx = 1; // Fallback
                }

                var pathsToRestore = new HashSet<string>(originalPaths, StringComparer.OrdinalIgnoreCase);

                foreach (var item in recycleBin.Items())
                {
                    if (pathsToRestore.Count == 0) break;

                    string name = item.Name;
                    string originalDir = recycleBin.GetDetailsOf(item, originalLocationIdx);
                    if (string.IsNullOrEmpty(originalDir)) continue;

                    string originalFullPath = Path.Combine(originalDir, name);

                    if (pathsToRestore.Contains(originalFullPath))
                    {
                        bool restored = false;
                        foreach (var verb in item.Verbs())
                        {
                            string verbName = verb.Name;
                            if (!string.IsNullOrEmpty(verbName))
                            {
                                string cleanVerb = verbName.Replace("&", "");
                                if (RestoreVerbTokens.Any(token => cleanVerb.Equals(token, StringComparison.OrdinalIgnoreCase) || cleanVerb.Contains(token, StringComparison.OrdinalIgnoreCase)))
                                {
                                    verb.DoIt();
                                    restored = true;
                                    break;
                                }
                            }
                        }

                        if (restored)
                        {
                            pathsToRestore.Remove(originalFullPath);
                        }
                    }
                }

                if (pathsToRestore.Count > 0)
                {
                    throw new Exception($"Could not restore all items from Recycle Bin. Missing: {string.Join(", ", pathsToRestore)}");
                }
            }
            finally
            {
                if (recycleBin != null && System.Runtime.InteropServices.Marshal.IsComObject(recycleBin))
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(recycleBin);
                }
                if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
                }
            }
        });
    }

    private static int GetOriginalLocationColumnIndex(dynamic recycleBin)
    {
        for (int i = 0; i < 100; i++)
        {
            string headerName = recycleBin.GetDetailsOf(null, i);
            if (!string.IsNullOrEmpty(headerName) && 
                (headerName.Contains("Original location", StringComparison.OrdinalIgnoreCase) || 
                 headerName.Contains("Originalfolder", StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }
        return -1;
    }

    private Task UndoCopyOperationAsync(List<string> createdPaths)
    {
        return Task.Run(() =>
        {
            int failCount = 0;
            for (int i = createdPaths.Count - 1; i >= 0; i--)
            {
                var path = createdPaths[i];
                try
                {
                    if (Directory.Exists(path))
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                            path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException
                        );
                    }
                    else if (File.Exists(path))
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException
                        );
                    }
                }
                catch
                {
                    failCount++;
                }
            }

            if (failCount > 0)
            {
                throw new Exception($"{failCount} item(s) failed to recycle during undo copy/paste.");
            }
        });
    }

    private Task UndoMoveOperationAsync(List<(string Source, string Dest)> mappings)
    {
        return Task.Run(() =>
        {
            int failCount = 0;
            for (int i = mappings.Count - 1; i >= 0; i--)
            {
                var (src, dest) = mappings[i];
                try
                {
                    if (File.Exists(dest))
                    {
                        string? dir = Path.GetDirectoryName(src);
                        if (dir != null) Directory.CreateDirectory(dir);
                        
                        File.Move(dest, src);
                    }
                    else if (Directory.Exists(dest))
                    {
                        string? dir = Path.GetDirectoryName(src);
                        if (dir != null) Directory.CreateDirectory(dir);
                        
                        Directory.Move(dest, src);
                    }
                    else
                    {
                        failCount++;
                    }
                }
                catch
                {
                    failCount++;
                }
            }

            if (failCount > 0)
            {
                throw new Exception($"{failCount} item(s) failed to move back during undo move.");
            }
        });
    }


    private void ItemsListHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader header && header.Column != null)
        {
            string? sortBy = null;
            if (header.Column.DisplayMemberBinding is System.Windows.Data.Binding binding)
            {
                sortBy = binding.Path.Path;
            }
            else if (header.Column.Header as string == "Name")
            {
                sortBy = "Name";
            }

            if (sortBy != null)
            {
                SortFileList(sortBy);
            }
        }
    }

    private void SortFileList(string propertyName)
    {
        if (_currentSortProperty == propertyName)
        {
            _currentSortDirection = _currentSortDirection == System.ComponentModel.ListSortDirection.Ascending 
                ? System.ComponentModel.ListSortDirection.Descending 
                : System.ComponentModel.ListSortDirection.Ascending;
        }
        else
        {
            _currentSortProperty = propertyName;
            _currentSortDirection = System.ComponentModel.ListSortDirection.Ascending;
        }

        ApplySorting();
    }

    private void ApplySorting()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_items);
        if (view != null)
        {
            view.SortDescriptions.Clear();
            // Folders always sorted first (IsFolder descending since true = 1, false = 0)
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription("IsFolder", System.ComponentModel.ListSortDirection.Descending));

            if (_currentSortProperty == "Size")
            {
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription("LengthValue", _currentSortDirection));
            }
            else
            {
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription(_currentSortProperty, _currentSortDirection));
            }
        }
    }


    private void NewFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentPath))
        {
            return;
        }

        string baseName = "New Folder";
        string candidate = Path.Combine(_currentPath, baseName);
        int index = 1;

        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(_currentPath, $"{baseName} ({index})");
            index++;
        }

        try
        {
            Directory.CreateDirectory(candidate);
            RefreshAfterMutation(Path.GetFileName(candidate));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to create folder: {ex.Message}";
        }
    }

    private void RenameButton_OnClick(object sender, RoutedEventArgs e)
    {
        List<ExplorerItem> selectedItems = ItemsList.SelectedItems.OfType<ExplorerItem>().ToList();
        if (selectedItems.Count == 0)
        {
            StatusText.Text = "Select one or more items to rename.";
            return;
        }

        // Switch to the Rename tab
        if (TabRename != null)
        {
            TabRename.IsChecked = true;
            RibbonTab_Click(TabRename, e);
        }

        // Focus the textbox of the first selected item in the list
        var selectedItem = selectedItems.FirstOrDefault();
        if (selectedItem != null)
        {
            ItemsList.ScrollIntoView(selectedItem);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var container = ItemsList.ItemContainerGenerator.ContainerFromItem(selectedItem) as System.Windows.Controls.ListViewItem;
                if (container != null)
                {
                    var targetTextBox = FindVisualChild<System.Windows.Controls.TextBox>(container);
                    if (targetTextBox != null)
                    {
                        targetTextBox.Focus();
                        targetTextBox.SelectAll();
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private async void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        List<ExplorerItem> selectedItems = ItemsList.SelectedItems.OfType<ExplorerItem>().ToList();
        if (selectedItems.Count == 0)
        {
            StatusText.Text = "Select one or more items to delete.";
            return;
        }

        string message = selectedItems.Count == 1
            ? $"Move '{selectedItems[0].Name}' to the Recycle Bin?"
            : $"Move these {selectedItems.Count} items to the Recycle Bin?";

        MessageBoxResult result = System.Windows.MessageBox.Show(
            message,
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        StatusText.Text = "Deleting items...";
        StatusProgressBar.Visibility = Visibility.Visible;
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange

        var progress = new Progress<string>(msg =>
        {
            StatusText.Text = msg;
        });

        int successCount = selectedItems.Count;
        try
        {
            var paths = selectedItems.Select(i => i.FullPath).ToList();
            var isFolder = selectedItems.Select(i => i.Type == "File folder").ToList();

            var recycleResult = await _fileOperationService.RecycleItemsAsync(paths, isFolder, progress);

            if (recycleResult.SucceededPaths.Count > 0)
            {
                var pathsCopy = recycleResult.SucceededPaths.ToList();
                _undoStack.Push(("delete", () => RestoreDeletedItemsAsync(pathsCopy)));
            }

            if (!recycleResult.Success)
            {
                var firstErr = recycleResult.FailedPaths.First();
                throw new Exception($"Failed to delete {Path.GetFileName(firstErr.Path)}: {firstErr.Exception.Message}");
            }

            StatusText.Text = $"Moved {recycleResult.SucceededPaths.Count} item(s) to the Recycle Bin.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Deletion canceled.";
        }
        catch (Exception ex)
        {
            string detail = string.IsNullOrWhiteSpace(ex.InnerException?.Message) ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";
            StatusText.Text = $"Deletion failed: {detail}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
            System.Windows.MessageBox.Show(detail, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StatusProgressBar.Visibility = Visibility.Collapsed;
            RefreshAfterMutation();
        }
    }

    private async void MergePdfButton_OnClick(object sender, RoutedEventArgs e)
    {
        ReleasePdfPreviewLocks();

        List<ExplorerItem> pdfFiles = GetSelectedPdfsInDisplayOrder();
        if (pdfFiles.Count < 2)
        {
            pdfFiles = GetSelectedPdfFiles(ItemsList);
        }

        if (pdfFiles.Count < 2)
        {
            StatusText.Text = "Select at least 2 PDF files to merge.";
            return;
        }

        var mergeDialog = new MergePdfDialog { Owner = this };
        mergeDialog.SetPdfFiles(pdfFiles);

        if (mergeDialog.ShowDialog() != true)
        {
            return;
        }

        var pdfPages = mergeDialog.GetOrderedPdfPages();
        if (pdfPages.Count == 0) return;

        string? defaultDir = TryGetExistingDirectory(Path.GetDirectoryName(pdfPages[0].SourceFilePath))
                             ?? TryGetExistingDirectory(_currentPath);

        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save merged PDF",
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = $"merged_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
        };
        if (defaultDir is not null)
        {
            saveDialog.InitialDirectory = defaultDir;
        }

        if (saveDialog.ShowDialog(this) != true)
        {
            return;
        }

        string destPath = Path.GetFullPath(saveDialog.FileName);

        StatusText.Text = "Merging PDFs...";
        StatusProgressBar.Visibility = Visibility.Visible;
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange

        var progress = new Progress<string>(msg =>
        {
            StatusText.Text = msg;
        });

        try
        {
            await _pdfService.MergePdfFilesAsync(destPath, pdfPages, progress);

            StatusText.Text = $"Merged {pdfPages.Count} pages to {Path.GetFileName(destPath)}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
            RefreshAfterMutation();
        }
        catch (Exception ex)
        {
            string detail = string.IsNullOrWhiteSpace(ex.InnerException?.Message) ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";
            StatusText.Text = $"Merge failed: {detail}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
            System.Windows.MessageBox.Show(detail, "Merge PDF failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StatusProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void ImagesToPdfButton_OnClick(object sender, RoutedEventArgs e)
    {
        var imgs = ItemsList.SelectedItems.OfType<ExplorerItem>().Where(i => i.Type != "File folder" && (Path.GetExtension(i.Name).Equals(".jpg", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(i.Name).Equals(".png", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(i.Name).Equals(".jpeg", StringComparison.OrdinalIgnoreCase))).ToList();
        if (imgs.Count == 0 || string.IsNullOrWhiteSpace(_currentPath))
        {
            StatusText.Text = "Select at least 1 Image (JPG/PNG) to convert.";
            return;
        }

        string outPath = GetUniqueFilePath(_currentPath, "ImagesConverted.pdf");
        
        StatusText.Text = "Converting images to PDF...";
        StatusProgressBar.Visibility = Visibility.Visible;
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange

        var progress = new Progress<string>(msg =>
        {
            StatusText.Text = msg;
        });

        try
        {
            await _pdfService.ConvertImagesToPdfAsync(outPath, imgs.Select(i => i.FullPath).ToList(), progress);
            StatusText.Text = $"Converted {imgs.Count} images to PDF.";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
            RefreshAfterMutation();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error converting images: " + ex.Message;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
        }
        finally
        {
            StatusProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void OcrPdfButton_OnClick(object sender, RoutedEventArgs e)
    {
        ReleasePdfPreviewLocks();
        var pdfFiles = GetSelectedPdfsInDisplayOrder();
        if (pdfFiles.Count == 0)
        {
            StatusText.Text = "Select at least 1 PDF to OCR.";
            return;
        }

        StatusText.Text = "Downloading OCR data and processing... This may take a while.";
        StatusProgressBar.Visibility = Visibility.Visible;
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange

        var progress = new Progress<OcrProgress>(p =>
        {
            StatusText.Text = $"OCR: {p.Status}";
        });

        try
        {
            int success = 0;
            foreach(var pdf in pdfFiles)
            {
                string outPath = GetUniqueFilePath(Path.GetDirectoryName(pdf.FullPath)!, Path.GetFileNameWithoutExtension(pdf.Name) + "_ocr.pdf");
                var result = await OcrHelper.MakeSearchablePdfAsync(pdf.FullPath, outPath, progress: progress);
                if (result.Success) success++;
            }
            RefreshAfterMutation();
            StatusText.Text = $"OCR completed for {success} of {pdfFiles.Count} PDF(s).";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
        }
        catch (Exception ex)
        {
            StatusText.Text = "OCR failed: " + ex.Message;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
        }
        finally
        {
            StatusProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void CompressPdfButton_OnClick(object sender, RoutedEventArgs e)
    {
        ReleasePdfPreviewLocks();

        List<ExplorerItem> pdfFiles = GetSelectedPdfFiles(ItemsList);
        if (pdfFiles.Count != 1)
        {
            StatusText.Text = "Select exactly 1 PDF file to compress.";
            return;
        }

        ExplorerItem source = pdfFiles[0];
        string? sourceDir = TryGetExistingDirectory(Path.GetDirectoryName(source.FullPath))
                             ?? TryGetExistingDirectory(_currentPath);
        string sourceName = Path.GetFileNameWithoutExtension(source.Name);

        var compDialog = new PdfCompressionDialog { Owner = this };
        if (compDialog.ShowDialog() != true)
        {
            return;
        }

        int maxEdgePx = compDialog.MaxEdgePx;
        int jpegQuality = compDialog.JpegQuality;
        bool useGhostscript = compDialog.UseGhostscript;

        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save compressed PDF",
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = $"{sourceName}_compressed.pdf"
        };
        if (sourceDir is not null)
        {
            saveDialog.InitialDirectory = sourceDir;
        }

        if (saveDialog.ShowDialog(this) != true)
        {
            return;
        }

        string destPath = Path.GetFullPath(saveDialog.FileName);
        string srcPath = Path.GetFullPath(source.FullPath);

        StatusText.Text = "Compressing PDF...";
        StatusProgressBar.Visibility = Visibility.Visible;
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange

        var progress = new Progress<string>(msg =>
        {
            StatusText.Text = msg;
        });

        try
        {
            await _pdfService.CompressPdfAsync(srcPath, destPath, useGhostscript, maxEdgePx, jpegQuality, progress);

            var srcInfo = new FileInfo(source.FullPath);
            var dstInfo = new FileInfo(destPath);
            StatusText.Text =
                $"Compressed PDF saved ({GetFriendlySize(srcInfo.Length)} -> {GetFriendlySize(dstInfo.Length)}).";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
            RefreshAfterMutation();
        }
        catch (Exception ex)
        {
            string detail = string.IsNullOrWhiteSpace(ex.InnerException?.Message) ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";
            StatusText.Text = $"Compression failed: {detail}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
            System.Windows.MessageBox.Show(detail, "Compress PDF failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StatusProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void SplitPdfButton_OnClick(object sender, RoutedEventArgs e)
    {
        ReleasePdfPreviewLocks();

        List<ExplorerItem> pdfFiles = GetSelectedPdfFiles(ItemsList);
        if (pdfFiles.Count != 1)
        {
            StatusText.Text = "Select exactly 1 PDF file to split.";
            return;
        }

        ExplorerItem source = pdfFiles[0];
        string? sourceDir = TryGetExistingDirectory(Path.GetDirectoryName(source.FullPath))
                             ?? TryGetExistingDirectory(_currentPath);
        string sourceName = Path.GetFileNameWithoutExtension(source.Name);

        var splitDialog = new SplitPdfDialog { Owner = this };
        if (splitDialog.ShowDialog() != true)
        {
            return;
        }

        int pagesPerSplit = splitDialog.PagesPerSplit;

        var folderDialog = new WinForms.FolderBrowserDialog
        {
            Description = "Choose folder to save split PDF pages",
            UseDescriptionForTitle = true
        };
        if (sourceDir is not null)
        {
            folderDialog.SelectedPath = sourceDir;
        }

        if (folderDialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        string outputDir = folderDialog.SelectedPath;
        string srcPath = Path.GetFullPath(source.FullPath);

        StatusText.Text = "Splitting PDF...";
        StatusProgressBar.Visibility = Visibility.Visible;
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange

        var progress = new Progress<string>(msg =>
        {
            StatusText.Text = msg;
        });

        try
        {
            await _pdfService.SplitPdfAsync(srcPath, outputDir, pagesPerSplit, progress);

            // Compute split count if it succeeds
            using var inputDocument = PdfSharp.Pdf.IO.PdfReader.Open(srcPath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            int pageCount = inputDocument.PageCount;
            int splitCount = (pageCount + pagesPerSplit - 1) / pagesPerSplit;

            StatusText.Text = $"Split PDF into {splitCount} file(s) ({pagesPerSplit} pages per split) in {outputDir}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
            RefreshAfterMutation();
        }
        catch (Exception ex)
        {
            string detail = string.IsNullOrWhiteSpace(ex.InnerException?.Message) ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";
            StatusText.Text = $"Split failed: {detail}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
            System.Windows.MessageBox.Show(detail, "Split PDF failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StatusProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void ConvertToPdfButton_OnClick(object sender, RoutedEventArgs e)
    {
        List<ExplorerItem> selectedItems = ItemsList.SelectedItems.OfType<ExplorerItem>()
            .Where(i => i.Type != "File folder")
            .ToList();

        if (selectedItems.Count == 0)
        {
            StatusText.Text = "Select one or more files to convert to PDF.";
            return;
        }

        string? sourceDir = TryGetExistingDirectory(_currentPath);
        var folderDialog = new WinForms.FolderBrowserDialog
        {
            Description = "Choose folder to save converted PDF files",
            UseDescriptionForTitle = true
        };
        if (sourceDir is not null)
        {
            folderDialog.SelectedPath = sourceDir;
        }

        if (folderDialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        string outputDir = folderDialog.SelectedPath;
        
        StatusText.Text = "Converting files to PDF...";
        StatusProgressBar.Visibility = Visibility.Visible;
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange

        var progress = new Progress<string>(msg =>
        {
            StatusText.Text = msg;
        });

        try
        {
            await _pdfService.ConvertFilesToPdfAsync(
                selectedItems.Select(i => i.FullPath).ToList(),
                outputDir,
                ImageExtensions.ToList(),
                TextExtensions.ToList(),
                TextFileBasenames.ToList(),
                progress
            );

            StatusText.Text = $"Converted files to PDF in {outputDir}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
            RefreshAfterMutation();
        }
        catch (Exception ex)
        {
            string detail = string.IsNullOrWhiteSpace(ex.InnerException?.Message) ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";
            StatusText.Text = $"Conversion failed: {detail}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
            System.Windows.MessageBox.Show(detail, "Conversion failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StatusProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void RefreshAfterMutation(string? selectedName = null)
    {
        if (string.IsNullOrWhiteSpace(_currentPath))
        {
            return;
        }

        string current = _currentPath;
        await NavigateTo(current, addToHistory: false);

        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            ExplorerItem? item = _items.FirstOrDefault(i => i.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
            if (item is not null)
            {
                ItemsList.SelectedItem = item;
            }
        }

        StatusText.Text = "Ready";
    }

    // ── Smart Panel Logic ────────────────────────────────────────────────────────
    
    private bool _isRenameMode = false;

    private void SetRenameMode(bool enabled)
    {
        if (_isRenameMode == enabled) return;
        _isRenameMode = enabled;

        if (_defaultGridView == null) return;

        if (HeaderMarkAllCheck != null)
        {
            HeaderMarkAllCheck.IsChecked = false;
        }

        if (_isRenameMode)
        {
            // Insert columns if not present
            if (!_defaultGridView.Columns.Contains(ColChangeBaseName))
            {
                _defaultGridView.Columns.Insert(1, ColChangeBaseName);
            }
            if (!_defaultGridView.Columns.Contains(ColNewName))
            {
                _defaultGridView.Columns.Insert(2, ColNewName);
            }

            // Update preview for current selection
            UpdateSmartPreview();
        }
        else
        {
            // Remove columns
            _defaultGridView.Columns.Remove(ColChangeBaseName);
            _defaultGridView.Columns.Remove(ColNewName);

            // Revert all item previews
            foreach (var item in _items)
            {
                item.NewName = item.Name;
                item.Status = string.Empty;
                item.Tag = FileSorter.ExtractTagFromFileName(Path.GetFileNameWithoutExtension(item.Name)) ?? string.Empty;
                item.IsMarkedForRename = false;
                item.SmartDate = string.Empty;
            }
        }
    }

    private void SyncSmartPanelWithSelection(ExplorerItem? item)
    {
        var selectedFiles = ItemsList.SelectedItems.OfType<ExplorerItem>().ToList();

        if (selectedFiles.Count == 0)
        {
            if (SmartApplyRenameButton != null)
            {
                SmartApplyRenameButton.IsEnabled = false;
            }
            return;
        }

        UpdateSmartPreview();
    }

    private void SmartPanel_OnAnyChange(object sender, RoutedEventArgs e) => UpdateSmartPreview();
    private void SmartPanel_OnAnyChange(object sender, SelectionChangedEventArgs e) => UpdateSmartPreview();

    private void HeaderMarkAllCheck_Checked(object sender, RoutedEventArgs e)
    {
        _isBulkUpdatingMarked = true;
        try
        {
            foreach (var item in _items)
            {
                item.IsMarkedForRename = true;
            }
        }
        finally
        {
            _isBulkUpdatingMarked = false;
        }
        UpdateSmartPreview();
    }

    private void HeaderMarkAllCheck_Unchecked(object sender, RoutedEventArgs e)
    {
        _isBulkUpdatingMarked = true;
        try
        {
            foreach (var item in _items)
            {
                item.IsMarkedForRename = false;
            }
        }
        finally
        {
            _isBulkUpdatingMarked = false;
        }
        UpdateSmartPreview();
    }

    private void RenameMode_Changed(object sender, RoutedEventArgs e)
    {
        if (SmartModePanel == null || RegexModePanel == null) return;
        if (ModeBasicRadio != null && ModeBasicRadio.IsChecked == true)
        {
            SmartModePanel.Visibility = Visibility.Visible;
            RegexModePanel.Visibility = Visibility.Collapsed;
        }
        else if (ModeRegexRadio != null && ModeRegexRadio.IsChecked == true)
        {
            SmartModePanel.Visibility = Visibility.Collapsed;
            RegexModePanel.Visibility = Visibility.Visible;
        }
        UpdateSmartPreview();
    }

    private RenameOptions GetRenameOptions()
    {
        return new RenameOptions
        {
            UseRegex = ModeRegexRadio?.IsChecked == true,
            RegexFind = RegexFindBox?.Text ?? string.Empty,
            RegexReplace = RegexReplaceBox?.Text ?? string.Empty,
            RegexIgnoreCase = RegexIgnoreCaseCheck?.IsChecked == true,
            RegexReplaceAll = RegexReplaceAllCheck?.IsChecked == true,
            StripDates = SmartStripDatesCheck?.IsChecked == true,
            CaseTransform = (SmartCaseTransformCombo?.SelectedItem as ComboBoxItem)?.Content as string ?? string.Empty,
            NumberingEnabled = SmartNumberingEnabledCheck?.IsChecked == true,
            NumberingFormat = (SmartNumberingFormatCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "_001",
            DateEnabled = SmartDateEnabledCheck?.IsChecked == true,
            DateFormat = (SmartDateFormatCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "yyyy-MM-dd",
            DateValue = SmartDatePicker?.SelectedDate,
            DateIsPrefix = SmartDatePrefixRadio?.IsChecked == true,
            TagEnabled = SmartTagEnabledCheck?.IsChecked == true,
            TagValue = (SmartTagComboBox?.SelectedItem as string) ?? SmartTagComboBox?.Text?.Trim() ?? string.Empty,
            TagIsPrefix = SmartTagPrefixRadio?.IsChecked == true
        };
    }

    private string GetSmartDatePart()
    {
        if (SmartDateEnabledCheck?.IsChecked != true) return string.Empty;
        var fmt = (SmartDateFormatCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "yyyy-MM-dd";
        var dateToUse = SmartDatePicker?.SelectedDate ?? DateTime.Today;
        return dateToUse.ToString(fmt);
    }

    private string GetSmartTagPart()
    {
        if (SmartTagEnabledCheck?.IsChecked != true || SmartTagComboBox == null) return string.Empty;
        var selected = SmartTagComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(selected))
        {
            selected = SmartTagComboBox.Text.Trim();
        }
        return selected;
    }

    private void UpdateSmartPreview()
    {
        if (RegexErrorBorder != null) RegexErrorBorder.Visibility = Visibility.Collapsed;
        if (SmartApplyRenameButton == null) return;

        // Revert unmarked items to original name (or preserve manually edited names)
        foreach (var item in _items)
        {
            if (!item.IsMarkedForRename)
            {
                var originalBase = item.IsFolder ? item.Name : Path.GetFileNameWithoutExtension(item.Name);
                if (item.BaseName != originalBase)
                {
                    if (item.IsFolder)
                    {
                        item.NewName = item.BaseName;
                    }
                    else
                    {
                        string ext = Path.GetExtension(item.Name);
                        item.NewName = item.BaseName + ext;
                    }
                }
                else
                {
                    item.NewName = item.Name;
                }
                item.Status = string.Empty;
                item.Tag = FileSorter.ExtractTagFromFileName(Path.GetFileNameWithoutExtension(item.Name)) ?? string.Empty;
                item.SmartDate = string.Empty;
            }
        }

        var filesToRename = _items.Where(item => item.IsMarkedForRename).ToList();
        var options = GetRenameOptions();

        for (int i = 0; i < filesToRename.Count; i++)
        {
            var item = filesToRename[i];

            var datePart = options.DateEnabled ? item.SmartDate : string.Empty;
            var tagPart = options.TagEnabled ? item.Tag : string.Empty;

            var candidate = _renameEngine.BuildSmartNameFor(item, i, filesToRename.Count, options, datePart, tagPart);
            item.NewName = candidate;
        }

        int totalChanges = _items.Count(item => !item.Name.Equals(item.NewName, StringComparison.OrdinalIgnoreCase));

        if (totalChanges == 0)
        {
            foreach (var item in _items)
            {
                item.Status = string.Empty;
            }
            SmartApplyRenameButton.IsEnabled = false;
            StatusText.Text = "Select files via checkboxes or edit names to rename them.";
            return;
        }

        bool hasError = false;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Include names of files that are NOT changing to avoid conflicts
        foreach (var item in _items)
        {
            if (item.Name.Equals(item.NewName, StringComparison.OrdinalIgnoreCase))
            {
                usedNames.Add(item.Name);
            }
        }

        string firstMsg = null;
        bool firstIsWarn = false;
        int changeCount = 0;

        foreach (var item in _items)
        {
            if (!item.Name.Equals(item.NewName, StringComparison.OrdinalIgnoreCase))
            {
                var (ok, msg, isWarn) = ValidateSmartName(item.Name, item.NewName, usedNames);
                if (!ok && !isWarn) hasError = true;
                if (changeCount == 0)
                {
                    firstMsg = msg;
                    firstIsWarn = isWarn;
                }
                changeCount++;

                usedNames.Add(item.NewName);
                item.Status = ok ? "OK" : (isWarn ? "! Warning" : "Error");
            }
            else
            {
                if (item.IsMarkedForRename)
                {
                    item.Status = "OK";
                }
            }

            // Update details preview if the selected item is this item
            if (ItemsList.SelectedItem == item && !_isUpdatingDetails && DetailsNewName.Text != item.NewName)
            {
                _isUpdatingDetails = true;
                DetailsNewName.Text = item.NewName;
                _isUpdatingDetails = false;
            }
        }

        if (!hasError)
        {
            SmartApplyRenameButton.IsEnabled = true;
            if (totalChanges == 1 && string.IsNullOrEmpty(firstMsg)) StatusText.Text = "Ready";
            else if (!string.IsNullOrEmpty(firstMsg)) StatusText.Text = firstMsg;
            else StatusText.Text = $"{totalChanges} files to rename.";
        }
        else
        {
            SmartApplyRenameButton.IsEnabled = false;
            StatusText.Text = "Cannot rename: Name conflicts or invalid names detected.";
        }
    }

    private (bool ok, string msg, bool isWarn) ValidateSmartName(string originalName, string candidate, HashSet<string> usedNames)
    {
        return _renameEngine.ValidateSmartName(originalName, candidate, usedNames, _currentPath);
    }

    private void SmartTagEnabledCheck_OnChange(object sender, RoutedEventArgs e)
    {
        if (SmartTagEnabledCheck?.IsChecked == true && SmartTagComboBox?.Items.Count > 0)
        {
            // Automatically select the first tag when checkbox is checked
            SmartTagComboBox.SelectedIndex = 0;
        }
        UpdateSmartPreview();
    }

    private void SmartTagComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SmartTagEnabledCheck?.IsChecked == true)
        {
            UpdateSmartPreview();
        }
    }

    private void SmartTagComboBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SmartTagEnabledCheck?.IsChecked == true)
        {
            UpdateSmartPreview();
        }
    }

    private void SmartNewTagButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog("Enter new tag name:");
        dialog.Owner = this;
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Answer))
        {
            var raw = dialog.Answer.Trim().Trim('_', '-', ' ');
            var safe = new string(raw.Where(c => !_invalidChars.Contains(c)).ToArray());
            if (!string.IsNullOrWhiteSpace(safe) && !_appSettings.SavedTags.Contains(safe, StringComparer.OrdinalIgnoreCase))
            {
                _appSettings.SavedTags.Add(safe);
                AppSettingsStore.Save(_appSettings);
                LoadSavedTags();
                SmartTagComboBox.SelectedItem = safe;
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(obj, i);
            if (child is T t)
            {
                return t;
            }
            T? childOfChild = FindVisualChild<T>(child);
            if (childOfChild != null)
            {
                return childOfChild;
            }
        }
        return null;
    }

    private void BaseNameTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (sender is System.Windows.Controls.TextBox textBox && textBox.DataContext is ExplorerItem item)
            {
                RenameSingleItem(item);
                e.Handled = true;
                System.Windows.Input.Keyboard.ClearFocus();
            }
        }
        else if (e.Key == Key.Up || e.Key == Key.Down)
        {
            if (sender is System.Windows.Controls.TextBox textBox && textBox.DataContext is ExplorerItem item)
            {
                int index = ItemsList.Items.IndexOf(item);
                if (index != -1)
                {
                    int nextIndex = e.Key == Key.Up ? index - 1 : index + 1;
                    if (nextIndex >= 0 && nextIndex < ItemsList.Items.Count)
                    {
                        var nextItem = ItemsList.Items[nextIndex];
                        ItemsList.SelectedItem = nextItem;
                        ItemsList.ScrollIntoView(nextItem);

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            var container = ItemsList.ItemContainerGenerator.ContainerFromItem(nextItem) as System.Windows.Controls.ListViewItem;
                            if (container != null)
                            {
                                var targetTextBox = FindVisualChild<System.Windows.Controls.TextBox>(container);
                                if (targetTextBox != null)
                                {
                                    targetTextBox.Focus();
                                    targetTextBox.SelectAll();
                                }
                            }
                        }), System.Windows.Threading.DispatcherPriority.Input);

                        e.Handled = true;
                    }
                }
            }
        }
    }

    private void RenameSingleItem(ExplorerItem item)
    {
        if (string.IsNullOrWhiteSpace(item.NewName) || item.Name.Equals(item.NewName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var src = item.FullPath;
        var dest = Path.Combine(Path.GetDirectoryName(src) ?? _currentPath ?? "", item.NewName);

        try
        {
            if (File.Exists(dest) || Directory.Exists(dest))
            {
                dest = GetUniqueFilePath(Path.GetDirectoryName(src) ?? _currentPath ?? "", item.NewName);
            }

            if (item.IsFolder)
            {
                Directory.Move(src, dest);
            }
            else
            {
                File.Move(src, dest);
            }

            var renamedBatch = new List<(string OldPath, string NewPath)> { (src, dest) };
            _undoStack.Push(("rename", () => UndoRenameBatchAsync(renamedBatch)));

            StatusText.Text = $"Renamed '{item.Name}' to '{Path.GetFileName(dest)}'.";
            RefreshAfterMutation();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to rename '{item.Name}' to '{item.NewName}': {ex.Message}", "Rename Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void SmartApplyRenameButton_OnClick(object sender, RoutedEventArgs e)
    {
        var changedItems = _items.Where(item => !item.Name.Equals(item.NewName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (changedItems.Count == 0 || string.IsNullOrWhiteSpace(_currentPath)) return;

        int successCount = 0;
        int failCount = 0;
        var renamedBatch = new List<(string OldPath, string NewPath)>();

        foreach (var item in changedItems)
        {
            if (item.Status == "Error") 
            {
                failCount++;
                continue;
            }

            var src = item.FullPath;
            var dest = Path.Combine(_currentPath, item.NewName);

            try
            {
                if (File.Exists(dest) || Directory.Exists(dest))
                {
                    dest = GetUniqueFilePath(_currentPath, item.NewName);
                }

                if (item.IsFolder)
                {
                    Directory.Move(src, dest);
                }
                else
                {
                    File.Move(src, dest);
                }

                renamedBatch.Add((src, dest));
                successCount++;
            }
            catch
            {
                failCount++;
            }
        }

        if (renamedBatch.Count > 0)
        {
            var batchCopy = renamedBatch.ToList();
            _undoStack.Push(("rename", () => UndoRenameBatchAsync(batchCopy)));
        }

        RefreshAfterMutation();
        if (failCount > 0)
        {
            StatusText.Text = $"Renamed {successCount} files, {failCount} failed.";
        }
        else
        {
            StatusText.Text = $"Successfully renamed {successCount} file(s).";
        }
    }


    // ── Context Menu Handlers ─────────────────────────────────────────────────
    private void ContextMenu_Open_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is ExplorerItem item)
        {
            ItemsList_OnMouseDoubleClick(sender, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
        }
    }

    private void ContextMenu_Copy_Click(object sender, RoutedEventArgs e)
    {
        var files = ItemsList.SelectedItems.OfType<ExplorerItem>()
            .Where(i => i.Type != "File folder")
            .Select(i => i.FullPath)
            .ToArray();

        if (files.Length == 0)
        {
            StatusText.Text = "No files selected to copy.";
            return;
        }

        try
        {
            var collection = new System.Collections.Specialized.StringCollection();
            collection.AddRange(files);
            System.Windows.Clipboard.SetFileDropList(collection);
            StatusText.Text = $"Copied {files.Length} file(s) to clipboard.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to copy: {ex.Message}";
        }
    }

    private void ContextMenu_Cut_Click(object sender, RoutedEventArgs e)
    {
        var files = ItemsList.SelectedItems.OfType<ExplorerItem>()
            .Where(i => i.Type != "File folder")
            .Select(i => i.FullPath)
            .ToArray();

        if (files.Length == 0)
        {
            StatusText.Text = "No files selected to cut.";
            return;
        }

        try
        {
            var collection = new System.Collections.Specialized.StringCollection();
            collection.AddRange(files);
            System.Windows.Clipboard.SetFileDropList(collection);
            StatusText.Text = $"Cut {files.Length} file(s) to clipboard.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to cut: {ex.Message}";
        }
    }

    private async void ContextMenu_Paste_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentPath))
        {
            StatusText.Text = "No folder selected for paste.";
            return;
        }

        try
        {
            var files = System.Windows.Clipboard.GetFileDropList();
            if (files.Count == 0)
            {
                StatusText.Text = "No files in clipboard to paste.";
                return;
            }

            StatusText.Text = "Pasting items...";
            StatusProgressBar.Visibility = Visibility.Visible;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange

            var progress = new Progress<string>(msg =>
            {
                StatusText.Text = msg;
            });

            var sourcePaths = files.Cast<string>().ToList();
            var result = await _fileOperationService.CopyItemsAsync(sourcePaths, _currentPath, progress);

            if (result.SucceededPaths.Count > 0)
            {
                var createdPaths = result.SucceededPaths.ToList();
                _undoStack.Push(("paste", () => UndoCopyOperationAsync(createdPaths)));
            }

            if (!result.Success)
            {
                var firstErr = result.FailedPaths.First();
                throw new Exception($"Failed to paste {Path.GetFileName(firstErr.Path)}: {firstErr.Exception.Message}");
            }

            StatusText.Text = $"Pasted {result.SucceededPaths.Count} item(s).";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
            RefreshAfterMutation();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to paste: {ex.Message}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
            RefreshAfterMutation();
        }
        finally
        {
            StatusProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ContextMenu_ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is ExplorerItem item)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.FullPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Unable to show in folder: {ex.Message}";
            }
        }
    }

    private void ContextMenu_Properties_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is ExplorerItem item)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = item.FullPath,
                    Verb = "properties",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Unable to show properties: {ex.Message}";
            }
        }
    }

    // ── Drag and Drop Internal & External ────────────────────────────────────

    private System.Windows.Point _dragStartPoint;
    private System.Windows.Controls.ListViewItem? _clickedSelectedElement = null;
    private bool _isInternalDropOccurred = false;
    private string? _currentSearchTerm = null;
    private System.Windows.Threading.DispatcherTimer? _searchDebounceTimer = null;
    private bool _isClearingSearchFromNavigation = false;

    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);

        if (sender is System.Windows.Controls.ListView listView)
        {
            DependencyObject? dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep != listView)
            {
                if (dep is System.Windows.Controls.ListViewItem item)
                {
                    if (item.IsSelected && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None)
                    {
                        _clickedSelectedElement = item;
                        e.Handled = true;
                        item.Focus();
                    }
                    break;
                }
                dep = VisualTreeHelper.GetParent(dep);
            }
        }
    }

    private void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_clickedSelectedElement != null)
        {
            ItemsList.SelectedItems.Clear();
            _clickedSelectedElement.IsSelected = true;
            _clickedSelectedElement = null;
        }
    }

    private void ItemsList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            System.Windows.Point mousePos = e.GetPosition(null);
            Vector diff = _dragStartPoint - mousePos;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _clickedSelectedElement = null;

                var selected = ItemsList.SelectedItems.OfType<ExplorerItem>().ToList();
                if (selected.Count > 0)
                {
                    _isInternalDropOccurred = false;
                    var data = new System.Windows.DataObject();
                    data.SetData("ExplorerItems", selected);
                    var filePaths = selected.Select(i => i.FullPath).ToArray();
                    data.SetData(System.Windows.DataFormats.FileDrop, filePaths);

                    var resultEffect = System.Windows.DragDrop.DoDragDrop(ItemsList, data, System.Windows.DragDropEffects.Move | System.Windows.DragDropEffects.Copy);

                    if (resultEffect == System.Windows.DragDropEffects.Move && !_isInternalDropOccurred)
                    {
                        // External move operation occurred; delete local source files
                        foreach (var path in filePaths)
                        {
                            try
                            {
                                if (File.Exists(path))
                                {
                                    File.Delete(path);
                                }
                                else if (Directory.Exists(path))
                                {
                                    Directory.Delete(path, true);
                                }
                            }
                            catch (Exception ex)
                            {
                                StatusText.Text = $"Failed to delete source item after external move: {ex.Message}";
                                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
                            }
                        }
                        RefreshAfterMutation();
                    }
                }
            }
        }
    }

    private void ItemsList_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        bool isInternal = e.Data.GetDataPresent("ExplorerItems");
        bool isExternal = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);

        if (isInternal || isExternal)
        {
            bool isOverFolder = false;
            DependencyObject? dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep != ItemsList)
            {
                if (dep is System.Windows.Controls.ListViewItem item && item.DataContext is ExplorerItem explorerItem)
                {
                    if (explorerItem.Type == "File folder")
                    {
                        isOverFolder = true;
                    }
                    break;
                }
                dep = VisualTreeHelper.GetParent(dep);
            }

            if (isOverFolder)
            {
                e.Effects = isInternal ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.Copy;
            }
            else
            {
                e.Effects = isExternal ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
            }
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void ItemsList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        _isInternalDropOccurred = true;
        if (string.IsNullOrWhiteSpace(_currentPath)) return;

        try
        {
            string targetPath = _currentPath;

            DependencyObject? dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep != ItemsList)
            {
                if (dep is System.Windows.Controls.ListViewItem item && item.DataContext is ExplorerItem explorerItem)
                {
                    if (explorerItem.Type == "File folder" && Directory.Exists(explorerItem.FullPath))
                    {
                        targetPath = explorerItem.FullPath;
                    }
                    break;
                }
                dep = VisualTreeHelper.GetParent(dep);
            }

            StatusProgressBar.Visibility = Visibility.Visible;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange

            var progress = new Progress<string>(msg =>
            {
                StatusText.Text = msg;
            });

            if (e.Data.GetDataPresent("ExplorerItems"))
            {
                var items = e.Data.GetData("ExplorerItems") as List<ExplorerItem>;
                if (items != null && items.Count > 0)
                {
                    if (targetPath != _currentPath)
                    {
                        StatusText.Text = "Moving items...";
                        var sourcePaths = items.Select(i => i.FullPath).ToList();
                        var result = await _fileOperationService.MoveItemsAsync(sourcePaths, targetPath, progress);

                        if (result.SucceededMoves.Count > 0)
                        {
                            var mappings = result.SucceededMoves.ToList();
                            _undoStack.Push(("drop move", () => UndoMoveOperationAsync(mappings)));
                        }

                        if (!result.Success)
                        {
                            var firstErr = result.FailedPaths.First();
                            throw new Exception($"Failed to move dropped item {Path.GetFileName(firstErr.Path)}: {firstErr.Exception.Message}");
                        }

                        StatusText.Text = $"Moved {result.SucceededMoves.Count} item(s) to {Path.GetFileName(targetPath)}.";
                        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
                        RefreshAfterMutation();
                    }
                    else
                    {
                        StatusProgressBar.Visibility = Visibility.Collapsed;
                        StatusDot.Fill = System.Windows.Media.Brushes.Transparent;
                    }
                }
            }
            else if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    StatusText.Text = "Copying dropped items...";
                    var sourcePaths = files.ToList();
                    var result = await _fileOperationService.CopyItemsAsync(sourcePaths, targetPath, progress);

                    if (result.SucceededPaths.Count > 0)
                    {
                        var createdPaths = result.SucceededPaths.ToList();
                        _undoStack.Push(("drop copy", () => UndoCopyOperationAsync(createdPaths)));
                    }

                    if (!result.Success)
                    {
                        var firstErr = result.FailedPaths.First();
                        throw new Exception($"Failed to copy dropped item {Path.GetFileName(firstErr.Path)}: {firstErr.Exception.Message}");
                    }

                    StatusText.Text = $"Dropped and copied {result.SucceededPaths.Count} item(s).";
                    StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
                    RefreshAfterMutation();
                }
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to copy/move dropped items: {ex.Message}";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
            RefreshAfterMutation();
        }
        finally
        {
            StatusProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void TreeViewItem_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is TreeViewItem tvi && tvi.Header is DirectoryNode node && !node.IsPlaceholder)
        {
            if (e.Data.GetDataPresent("ExplorerItems") || e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Move;
                e.Handled = true;
                return;
            }
        }
        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void TreeViewItem_Drop(object sender, System.Windows.DragEventArgs e)
    {
        _isInternalDropOccurred = true;
        if (sender is TreeViewItem tvi && tvi.Header is DirectoryNode targetNode && !targetNode.IsPlaceholder)
        {
            string targetPath = targetNode.FullPath;
            if (!Directory.Exists(targetPath)) return;

            try
            {
                StatusProgressBar.Visibility = Visibility.Visible;
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange

                var progress = new Progress<string>(msg =>
                {
                    StatusText.Text = msg;
                });

                if (e.Data.GetDataPresent("ExplorerItems"))
                {
                    var items = e.Data.GetData("ExplorerItems") as List<ExplorerItem>;
                    if (items != null && items.Count > 0)
                    {
                        StatusText.Text = "Moving items...";
                        var sourcePaths = items.Select(i => i.FullPath).ToList();
                        var result = await _fileOperationService.MoveItemsAsync(sourcePaths, targetPath, progress);

                        if (result.SucceededMoves.Count > 0)
                        {
                            var mappings = result.SucceededMoves.ToList();
                            _undoStack.Push(("drop move", () => UndoMoveOperationAsync(mappings)));
                        }

                        if (!result.Success)
                        {
                            var firstErr = result.FailedPaths.First();
                            throw new Exception($"Failed to move dropped item {Path.GetFileName(firstErr.Path)}: {firstErr.Exception.Message}");
                        }

                        StatusText.Text = $"Moved {result.SucceededMoves.Count} item(s) to {targetNode.Name}.";
                        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
                        RefreshAfterMutation();
                    }
                }
                else if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        StatusText.Text = "Copying items...";
                        var sourcePaths = files.ToList();
                        var result = await _fileOperationService.CopyItemsAsync(sourcePaths, targetPath, progress);

                        if (result.SucceededPaths.Count > 0)
                        {
                            var createdPaths = result.SucceededPaths.ToList();
                            _undoStack.Push(("drop copy", () => UndoCopyOperationAsync(createdPaths)));
                        }

                        if (!result.Success)
                        {
                            var firstErr = result.FailedPaths.First();
                            throw new Exception($"Failed to copy dropped item {Path.GetFileName(firstErr.Path)}: {firstErr.Exception.Message}");
                        }

                        StatusText.Text = $"Copied {result.SucceededPaths.Count} item(s) to {targetNode.Name}.";
                        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 200, 64)); // Green
                        RefreshAfterMutation();
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Drag and drop failed: {ex.Message}";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
                RefreshAfterMutation();
            }
            finally
            {
                StatusProgressBar.Visibility = Visibility.Collapsed;
            }
            e.Handled = true;
        }
    }

    // ── Helper & Event Handlers for View Mode, Theme & Recent Paths ─────────

    private GridViewColumn? FindColumn(GridView? gridView, string headerName)
    {
        if (gridView == null) return null;
        foreach (var col in gridView.Columns)
        {
            if (col.Header?.ToString() == headerName)
            {
                return col;
            }
        }
        return null;
    }

    private void RestoreColumnWidths()
    {
        if (ItemsList.View is GridView gridView)
        {
            var colName = FindColumn(gridView, "Name");
            if (colName != null) colName.Width = _appSettings.ColumnWidthName;

            var colType = FindColumn(gridView, "Type");
            if (colType != null) colType.Width = _appSettings.ColumnWidthType;

            var colSize = FindColumn(gridView, "Size");
            if (colSize != null) colSize.Width = _appSettings.ColumnWidthSize;

            var colModified = FindColumn(gridView, "Modified");
            if (colModified != null) colModified.Width = _appSettings.ColumnWidthModified;
        }
    }

    private void SaveColumnWidths()
    {
        if (ItemsList.View is GridView gridView)
        {
            var colName = FindColumn(gridView, "Name");
            if (colName != null) _appSettings.ColumnWidthName = colName.Width;

            var colType = FindColumn(gridView, "Type");
            if (colType != null) _appSettings.ColumnWidthType = colType.Width;

            var colSize = FindColumn(gridView, "Size");
            if (colSize != null) _appSettings.ColumnWidthSize = colSize.Width;

            var colModified = FindColumn(gridView, "Modified");
            if (colModified != null) _appSettings.ColumnWidthModified = colModified.Width;
        }
    }

    private void RestoreTheme()
    {
        ToggleTheme(_appSettings.IsDarkMode);
    }

    private void RestoreViewMode()
    {
        SetViewMode(_appSettings.IsThumbnailView);
    }

    private void SetViewMode(bool thumbnailMode)
    {
        _appSettings.IsThumbnailView = thumbnailMode;
        if (thumbnailMode)
        {
            ItemsList.View = null;
            ItemsList.ItemTemplate = (DataTemplate)this.Resources["FileThumbnailTemplate"];
            ItemsList.ItemsPanel = (ItemsPanelTemplate)this.Resources["FileThumbnailPanel"];
            ItemsList.ClearValue(ItemsControl.ItemContainerStyleProperty);
            System.Windows.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(ItemsList, System.Windows.Controls.ScrollBarVisibility.Disabled);
        }
        else
        {
            ItemsList.ClearValue(ItemsControl.ItemTemplateProperty);
            ItemsList.ClearValue(ItemsControl.ItemsPanelProperty);
            ItemsList.ItemContainerStyle = (System.Windows.Style)this.Resources["FileListItemStyle"];
            ItemsList.View = _defaultGridView;
            System.Windows.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(ItemsList, System.Windows.Controls.ScrollBarVisibility.Auto);
            
            // Re-apply column widths
            if (_defaultGridView != null)
            {
                var colName = FindColumn(_defaultGridView, "Name");
                if (colName != null) colName.Width = _appSettings.ColumnWidthName;

                var colType = FindColumn(_defaultGridView, "Type");
                if (colType != null) colType.Width = _appSettings.ColumnWidthType;

                var colSize = FindColumn(_defaultGridView, "Size");
                if (colSize != null) colSize.Width = _appSettings.ColumnWidthSize;

                var colModified = FindColumn(_defaultGridView, "Modified");
                if (colModified != null) colModified.Width = _appSettings.ColumnWidthModified;
            }
        }

        if (thumbnailMode && !string.IsNullOrWhiteSpace(_currentPath))
        {
            LoadThumbnailsAsync(_currentPath);
        }
    }

    private async void LoadThumbnailsAsync(string path)
    {
        int reqId = ++_previewRequestId; 

        List<ExplorerItem> itemsToProcess;
        lock (_items)
        {
            itemsToProcess = _items.ToList();
        }

        foreach (var item in itemsToProcess)
        {
            if (reqId != _previewRequestId) return;
            if (item.IsFolder || item.Thumbnail != null) continue;

            string ext = Path.GetExtension(item.Name).ToLowerInvariant();
            FileType detected = DetectFileType(item.FullPath);
            bool isImage = detected == FileType.Image || (detected == FileType.Unknown && ImageExtensions.Contains(ext));
            bool isPdf = detected == FileType.Pdf || (detected == FileType.Unknown && PdfExtensions.Contains(ext));

            if (isImage)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(item.FullPath);
                        bitmap.DecodePixelWidth = 100;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();

                        Dispatcher.Invoke(() =>
                        {
                            if (reqId == _previewRequestId)
                            {
                                item.Thumbnail = bitmap;
                            }
                        });
                    }
                    catch { }
                });
            }
            else if (isPdf)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        using var doc = DocLib.Instance.GetDocReader(item.FullPath, new PageDimensions(0.2));
                        if (doc.GetPageCount() > 0)
                        {
                            using var pageReader = doc.GetPageReader(0);
                            int w = pageReader.GetPageWidth();
                            int h = pageReader.GetPageHeight();
                            byte[] bgra = pageReader.GetImage();

                            Dispatcher.Invoke(() =>
                            {
                                if (reqId == _previewRequestId)
                                {
                                    var writeable = new WriteableBitmap(w, h, 72, 72, PixelFormats.Bgra32, null);
                                    writeable.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
                                    writeable.Freeze();
                                    item.Thumbnail = writeable;
                                }
                            });
                        }
                    }
                    catch { }
                });
            }
        }
    }

    private void ToggleTheme(bool isDark)
    {
        _appSettings.IsDarkMode = isDark;
        var dicts = System.Windows.Application.Current.Resources.MergedDictionaries;
        ResourceDictionary? oldDict = null;
        foreach (var dict in dicts)
        {
            if (dict.Source != null && (dict.Source.OriginalString.Contains("ThemeLight.xaml") || dict.Source.OriginalString.Contains("ThemeDark.xaml")))
            {
                oldDict = dict;
                break;
            }
        }

        var newDict = new ResourceDictionary
        {
            Source = new Uri(isDark ? "ThemeDark.xaml" : "ThemeLight.xaml", UriKind.RelativeOrAbsolute)
        };

        if (oldDict != null)
        {
            dicts.Remove(oldDict);
        }
        dicts.Add(newDict);
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleTheme(!_appSettings.IsDarkMode);
    }

    private void ListViewButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetViewMode(false);
    }

    private void GridViewButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetViewMode(true);
    }

    private void AddToRecentPaths(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _recentPaths.Remove(path);
        _recentPaths.Insert(0, path);
        if (_recentPaths.Count > 10)
        {
            _recentPaths.RemoveAt(_recentPaths.Count - 1);
        }
    }


    private void SetupFileWatcher(string path)
    {
        try
        {
            if (_fileWatcher != null)
            {
                _fileWatcher.EnableRaisingEvents = false;
                _fileWatcher.Dispose();
                _fileWatcher = null;
            }

            if (!Directory.Exists(path)) return;

            _fileWatcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false
            };

            _fileWatcher.Created += FileWatcher_Changed;
            _fileWatcher.Deleted += FileWatcher_Changed;
            _fileWatcher.Renamed += FileWatcher_Changed;
            _fileWatcher.Changed += FileWatcher_Changed;

            _fileWatcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Watcher could fail on special paths (like network roots), fail silently
        }
    }

    private void FileWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_watcherTimer == null)
            {
                _watcherTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(300)
                };
                _watcherTimer.Tick += (s, ev) =>
                {
                    _watcherTimer.Stop();
                    if (!string.IsNullOrWhiteSpace(_currentPath))
                    {
                        RefreshAfterMutation();
                    }
                };
            }
            _watcherTimer.Stop();
            _watcherTimer.Start();
        });
    }

    private void UpdateLicensingUI()
    {
        if (LicenseService.IsRegistered)
        {
            TrialStatusContainer.Visibility = Visibility.Collapsed;
            if (HelpVersionText != null)
            {
                HelpVersionText.Text = "v1.0.0 Pro";
            }
        }
        else
        {
            TrialStatusContainer.Visibility = Visibility.Visible;
            if (LicenseService.IsTrialExpired)
            {
                TrialStatusText.Text = "Trial Expired";
                TrialStatusText.Foreground = (System.Windows.Media.Brush)FindResource("RedAccentBrush");
            }
            else
            {
                TrialStatusText.Text = $"Trial Mode: {LicenseService.DaysRemaining} days remaining";
                TrialStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AmberAccentBrush");
            }

            if (HelpVersionText != null)
            {
                HelpVersionText.Text = "v1.0.0 Trial";
            }
        }
    }

    private void UnlockProButton_Click(object sender, RoutedEventArgs e)
    {
        var regDlg = new RegistrationDialog(isExpiredMode: false)
        {
            Owner = this
        };
        if (regDlg.ShowDialog() == true)
        {
            UpdateLicensingUI();
        }
    }

    private void RegisterHelpButton_OnClick(object sender, RoutedEventArgs e)
    {
        var regDlg = new RegistrationDialog(isExpiredMode: false)
        {
            Owner = this
        };
        if (regDlg.ShowDialog() == true)
        {
            UpdateLicensingUI();
        }
    }

    private void BuyHelpButton_OnClick(object sender, RoutedEventArgs e)
    {
        var payWin = new PaymentWindow(_appSettings.LicenseEmail)
        {
            Owner = this
        };
        if (payWin.ShowDialog() == true)
        {
            UpdateLicensingUI();
        }
    }

    private void CheckUpdatesButton_OnClick(object sender, RoutedEventArgs e)
    {
        string mode = LicenseService.IsRegistered ? "Pro" : "Trial";
        System.Windows.MessageBox.Show($"FileExplorerCS is up to date.\n\nInstalled Version: v1.0.0 ({mode} Edition)", "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

