using System.IO;
using System.Text.Json;

namespace FileExplorerCS;

internal static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppUserSettings Load()
    {
        try
        {
            string path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return new AppUserSettings();
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppUserSettings>(json) ?? new AppUserSettings();
        }
        catch
        {
            return new AppUserSettings();
        }
    }

    public static void Save(AppUserSettings settings)
    {
        try
        {
            string path = GetSettingsPath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Ignore persistence failures.
        }
    }

    private static string GetSettingsPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "FileExplorerCS", "settings.json");
    }
}

internal sealed class AppUserSettings
{
    public string LicenseEmail { get; set; } = string.Empty;
    public string LicenseKey { get; set; } = string.Empty;

    public string ArchiveRoot { get; set; } = string.Empty;

    public string LastTag { get; set; } = "Receipt";

    public List<string> SavedTags { get; set; } = new() { "Receipts", "Invoice", "Bank Statement" };

    /// <summary>yyyy-MM format.</summary>
    public string LastYearMonth { get; set; } = DateTime.Now.ToString("yyyy-MM");

    /// <summary>When true, quick rename prepends selected date (yyyy-MM-dd_) to the file name.</summary>
    public bool QuickRenameDatePrefix { get; set; }

    /// <summary>When true, quick rename prepends selected tag after the date prefix.</summary>
    public bool QuickRenameTagPrefix { get; set; } = true;

    /// <summary>yyyy-MM-dd format for selected prefix date.</summary>
    public string LastPrefixDate { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");

    /// <summary>Whether advanced controls are expanded in the rename panel.</summary>
    public bool ShowAdvancedPanel { get; set; }

    public bool IsDarkMode { get; set; }
    public bool IsThumbnailView { get; set; }
    public string LastPath { get; set; } = string.Empty;
    public double WindowWidth { get; set; } = 1000;
    public double WindowHeight { get; set; } = 700;
    public double WindowLeft { get; set; } = 100;
    public double WindowTop { get; set; } = 100;
    public bool IsWindowMaximized { get; set; }
    public double ColumnWidthName { get; set; } = 300;
    public double ColumnWidthType { get; set; } = 120;
    public double ColumnWidthSize { get; set; } = 80;
    public double ColumnWidthModified { get; set; } = 140;
}
