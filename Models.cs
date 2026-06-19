using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace FileExplorerCS;

public sealed class ExplorerItem : INotifyPropertyChanged
{
    private string _newName = "";
    private string _status = "";
    private string _name = "";
    private string _baseName = "";
    private bool _isBaseNameInitialized;
    private ImageSource? _thumbnail;
    private bool _isMarkedForRename;
    private string _smartDate = "";

    public required string Name
    {
        get => _name;
        set
        {
            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FileCategory));
        }
    }
    public required string FullPath { get; init; }
    public required string Type { get; init; }
    public string Size { get; init; } = "-";
    public string Modified { get; init; } = "-";
    private string _tag = "";

    public string FileCategory
    {
        get
        {
            if (IsFolder) return "Folder";
            string ext = Path.GetExtension(Name).ToLowerInvariant();
            switch (ext)
            {
                case ".pdf":
                    return "Pdf";
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".bmp":
                case ".ico":
                case ".webp":
                    return "Image";
                case ".txt":
                case ".md":
                case ".log":
                case ".ini":
                case ".cfg":
                    return "Text";
                case ".zip":
                case ".rar":
                case ".7z":
                case ".tar":
                case ".gz":
                    return "Archive";
                case ".mp3":
                case ".wav":
                case ".flac":
                case ".ogg":
                case ".m4a":
                    return "Audio";
                case ".mp4":
                case ".avi":
                case ".mkv":
                case ".mov":
                case ".wmv":
                    return "Video";
                case ".cs":
                case ".xaml":
                case ".xml":
                case ".json":
                case ".js":
                case ".ts":
                case ".py":
                case ".html":
                case ".css":
                case ".sln":
                case ".csproj":
                    return "Code";
                case ".exe":
                case ".msi":
                case ".bat":
                case ".cmd":
                case ".ps1":
                    return "Executable";
                default:
                    return "Generic";
            }
        }
    }
    public string Tag
    {
        get => _tag;
        set { _tag = value; OnPropertyChanged(); }
    }

    public bool IsMarkedForRename
    {
        get => _isMarkedForRename;
        set
        {
            if (_isMarkedForRename != value)
            {
                _isMarkedForRename = value;
                OnPropertyChanged();
            }
        }
    }

    public string SmartDate
    {
        get => _smartDate;
        set
        {
            if (_smartDate != value)
            {
                _smartDate = value;
                OnPropertyChanged();
            }
        }
    }

    public string BaseName
    {
        get
        {
            if (!_isBaseNameInitialized)
            {
                _baseName = IsFolder ? Name : Path.GetFileNameWithoutExtension(Name);
                _isBaseNameInitialized = true;
            }
            return _baseName;
        }
        set
        {
            var val = value ?? "";
            if (_baseName != val)
            {
                _baseName = val;
                _isBaseNameInitialized = true;
                OnPropertyChanged();
                
                // Update NewName
                if (IsFolder)
                {
                    NewName = _baseName;
                }
                else
                {
                    string ext = Path.GetExtension(Name);
                    NewName = _baseName + ext;
                }
            }
        }
    }

    public string NewName
    {
        get => string.IsNullOrEmpty(_newName) ? Name : _newName;
        set
        {
            if (_newName != value)
            {
                _newName = value;
                OnPropertyChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    public bool IsFolder => Type == "File folder";
    public long LengthValue { get; set; } = -1;

    public string EmojiIcon => IsFolder 
        ? "📁" 
        : (Path.GetExtension(Name).Equals(".pdf", StringComparison.OrdinalIgnoreCase) 
            ? "📕" 
            : (IsImageExtension(Path.GetExtension(Name)) ? "🖼️" : "📄"));

    private static bool IsImageExtension(string ext)
    {
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public override string ToString() => Name;
}

public sealed class DirectoryNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public ObservableCollection<DirectoryNode> Children { get; } = [];

    // Placeholder is used to show expand arrow before loading.
    public bool IsPlaceholder { get; init; }
}

