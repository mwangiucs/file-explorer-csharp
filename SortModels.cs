using System.IO;
using System.Collections.Generic;
// ── SortModels.cs ────────────────────────────────────────────────────────────
// Replaces the old SortOptions record. Drop into your project.

namespace FileExplorerCS;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum DateFolderMode
{
    None,       // flat — no date subfolder
    ByYear,     // YYYY
    ByMonth,    // YYYY-MM
    ByDay       // YYYY-MM-DD
}

public enum DuplicateAction
{
    Skip,       // leave the file in place
    Overwrite,  // replace destination
    Rename      // auto-suffix  (1), (2) …
}

// ── Options passed to FileSorter ──────────────────────────────────────────────

public sealed class SortOptions
{
    // Date folder
    public DateFolderMode DateMode      { get; init; } = DateFolderMode.ByMonth;

    // Tag subfolder  (uses the tag already embedded in the filename via FileSorter.ExtractTagFromFileName)
    public bool           TagSubfolders { get; init; } = false;

    // Extension subfolder  e.g.  PDF / DOCX / JPG
    public bool           ExtSubfolders { get; init; } = false;

    // Archive root  (null → sort inside the current folder)
    public string?        ArchiveRoot   { get; init; } = null;

    // What to do when a file already exists at the destination
    public DuplicateAction OnDuplicate  { get; init; } = DuplicateAction.Rename;

    // Dry-run: compute moves but don't touch the filesystem
    public bool           DryRun        { get; init; } = false;

    // ── Back-compat shim (old call sites used the 2-arg record ctor) ──────────
    public SortOptions() { }

    /// <summary>Shim so old  new SortOptions(mode, tagSub)  still compiles.</summary>
    public SortOptions(SortMode legacyMode, bool tagSubfolders)
    {
        DateMode      = legacyMode switch
        {
            SortMode.ByMonth => DateFolderMode.ByMonth,
            SortMode.ByYear  => DateFolderMode.ByYear,
            _                => DateFolderMode.None
        };
        TagSubfolders = tagSubfolders;
    }
}

// ── Legacy enum kept for back-compat ─────────────────────────────────────────
public enum SortMode { None, ByYear, ByMonth }

// ── Per-file move result ──────────────────────────────────────────────────────

public sealed class SortMoveResult
{
    public string  SourcePath  { get; init; } = "";
    public string  DestPath    { get; init; } = "";
    public bool    Success     { get; init; }
    public string? Error       { get; init; }
    public bool    WasDryRun   { get; init; }

    public string FileName => Path.GetFileName(SourcePath);
    public string DestFolder => Path.GetDirectoryName(DestPath) ?? "";
}
