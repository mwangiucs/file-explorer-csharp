using System.IO;
using System.Collections.Generic;
using System.Linq;
// ── FileSorter.cs ─────────────────────────────────────────────────────────────
// Drop this file into your project, replacing the existing FileSorter.cs.

using System.Globalization;
using System.Text.RegularExpressions;

namespace FileExplorerCS;

public static class FileSorter
{
    // ── Date patterns tried in priority order ─────────────────────────────────
    // Each pattern must expose a named group  "y" (year), optionally "m" (month), "d" (day).

    private static readonly (Regex Re, string Format)[] DatePatterns =
    [
        // ISO prefix/suffix  2024-06-15  or  20240615
        (new Regex(@"(?<!\d)(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})(?!\d)", RegexOptions.Compiled), "ISO"),
        (new Regex(@"(?<!\d)(?<y>\d{4})(?<m>\d{2})(?<d>\d{2})(?!\d)",   RegexOptions.Compiled), "compact"),
        // dd-MM-yyyy  or  dd.MM.yyyy
        (new Regex(@"(?<!\d)(?<d>\d{2})[-.](?<m>\d{2})[-.](?<y>\d{4})(?!\d)", RegexOptions.Compiled), "dmy"),
        // Month name  "June 2024"  or  "Jun-2024"
        (new Regex(@"(?<m>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*[-\s](?<y>\d{4})",
                   RegexOptions.Compiled | RegexOptions.IgnoreCase), "monthname"),
        // Year + month only  2024-06
        (new Regex(@"(?<!\d)(?<y>\d{4})-(?<m>\d{2})(?!\d)", RegexOptions.Compiled), "ym"),
        // Year-only fallback — uses file modified date for month/day
        (new Regex(@"(?<!\d)(?<y>20\d{2}|19\d{2})(?!\d)",  RegexOptions.Compiled), "yearonly"),
    ];

    private static readonly Regex TagPattern =
        new(@"_#(?<tag>[A-Za-z0-9]+)", RegexOptions.Compiled);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Sort <paramref name="filePaths"/> according to <paramref name="options"/>.
    /// Returns one <see cref="SortMoveResult"/> per file (including failures).
    /// </summary>
    public static List<SortMoveResult> Sort(
        IEnumerable<string> filePaths,
        string              currentFolder,
        SortOptions         options)
    {
        string root = string.IsNullOrWhiteSpace(options.ArchiveRoot)
            ? currentFolder
            : options.ArchiveRoot;

        var results = new List<SortMoveResult>();

        foreach (string src in filePaths)
        {
            try
            {
                string dest = BuildDestPath(src, root, options);

                if (options.DryRun)
                {
                    results.Add(new SortMoveResult
                    {
                        SourcePath = src,
                        DestPath   = dest,
                        Success    = true,
                        WasDryRun  = true
                    });
                    continue;
                }

                string? destDir = Path.GetDirectoryName(dest);
                if (destDir is not null)
                    Directory.CreateDirectory(destDir);

                dest = ResolveDestPath(src, dest, options.OnDuplicate);

                if (dest is not null)
                {
                    File.Move(src, dest);
                    results.Add(new SortMoveResult { SourcePath = src, DestPath = dest, Success = true });
                }
                else
                {
                    // Skip action
                    results.Add(new SortMoveResult
                    {
                        SourcePath = src,
                        DestPath   = Path.Combine(destDir ?? root, Path.GetFileName(src)),
                        Success    = false,
                        Error      = "Skipped — file already exists at destination."
                    });
                }
            }
            catch (Exception ex)
            {
                results.Add(new SortMoveResult
                {
                    SourcePath = src,
                    DestPath   = src,
                    Success    = false,
                    Error      = ex.Message
                });
            }
        }

        return results;
    }

    /// <summary>Preview-only: returns the destination path that would be used without touching disk.</summary>
    public static string PreviewDestPath(string filePath, string currentFolder, SortOptions options)
    {
        string root = string.IsNullOrWhiteSpace(options.ArchiveRoot)
            ? currentFolder
            : options.ArchiveRoot;

        return BuildDestPath(filePath, root, options);
    }

    /// <summary>Extracts an embedded  _#tag  suffix from a filename stem (no extension).</summary>
    public static string? ExtractTagFromFileName(string stem)
    {
        Match m = TagPattern.Match(stem);
        return m.Success ? m.Groups["tag"].Value : null;
    }

    /// <summary>
    /// Tries to infer a <see cref="DateTime"/> from the filename, falling back to
    /// the file's LastWriteTime when nothing is found.
    /// </summary>
    public static DateTime InferDate(string filePath)
    {
        string stem = Path.GetFileNameWithoutExtension(filePath);
        FileInfo fi = new(filePath);

        foreach (var (re, fmt) in DatePatterns)
        {
            Match m = re.Match(stem);
            if (!m.Success) continue;

            try
            {
                int year  = int.Parse(m.Groups["y"].Value);
                int month = m.Groups["m"].Success ? ParseMonth(m.Groups["m"].Value) : fi.LastWriteTime.Month;
                int day   = m.Groups["d"].Success ? int.Parse(m.Groups["d"].Value)  : 1;

                if (year is >= 1970 and <= 2099 && month is >= 1 and <= 12 && day is >= 1 and <= 31)
                    return new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
            }
            catch { /* bad parse — try next pattern */ }
        }

        return fi.LastWriteTime;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string BuildDestPath(string src, string root, SortOptions options)
    {
        DateTime date = InferDate(src);
        string   ext  = Path.GetExtension(src).TrimStart('.').ToUpperInvariant();
        string   tag  = ExtractTagFromFileName(Path.GetFileNameWithoutExtension(src)) ?? "";

        // Build folder segments
        var segments = new List<string> { root };

        // 1. Date folder
        string dateSeg = options.DateMode switch
        {
            DateFolderMode.ByYear  => date.ToString("yyyy"),
            DateFolderMode.ByMonth => date.ToString("yyyy-MM"),
            DateFolderMode.ByDay   => date.ToString("yyyy-MM-dd"),
            _                      => ""
        };
        if (!string.IsNullOrEmpty(dateSeg))
            segments.Add(dateSeg);

        // 2. Tag subfolder
        if (options.TagSubfolders && !string.IsNullOrWhiteSpace(tag))
            segments.Add(tag);

        // 3. Extension subfolder
        if (options.ExtSubfolders && !string.IsNullOrWhiteSpace(ext))
            segments.Add(ext);

        string destDir  = Path.Combine([.. segments]);
        string destFile = Path.Combine(destDir, Path.GetFileName(src));
        return destFile;
    }

    /// <summary>
    /// Resolves the final destination path given a duplicate action.
    /// Returns null when action is Skip and the file already exists.
    /// </summary>
    private static string? ResolveDestPath(string src, string dest, DuplicateAction action)
    {
        string fullSrc  = Path.GetFullPath(src);
        string fullDest = Path.GetFullPath(dest);

        // Moving to itself — no-op, treat as success
        if (string.Equals(fullSrc, fullDest, StringComparison.OrdinalIgnoreCase))
            return fullDest;

        if (!File.Exists(fullDest))
            return fullDest;

        return action switch
        {
            DuplicateAction.Overwrite => fullDest,
            DuplicateAction.Rename    => GetUniquePath(fullDest),
            _                         => null   // Skip
        };
    }

    private static string GetUniquePath(string path)
    {
        string dir  = Path.GetDirectoryName(path)!;
        string body = Path.GetFileNameWithoutExtension(path);
        string ext  = Path.GetExtension(path);

        for (int i = 1; i < 10_000; i++)
        {
            string candidate = Path.Combine(dir, $"{body} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        throw new IOException($"Cannot find a unique name for '{path}'.");
    }

    private static int ParseMonth(string value)
    {
        if (int.TryParse(value, out int n)) return n;

        // Month abbreviation
        return DateTime.ParseExact(value[..3], "MMM", CultureInfo.InvariantCulture).Month;
    }
}
