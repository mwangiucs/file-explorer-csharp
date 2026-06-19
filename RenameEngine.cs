using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FileExplorerCS;

public class RenameOptions
{
    public bool UseRegex { get; set; }
    public string RegexFind { get; set; } = string.Empty;
    public string RegexReplace { get; set; } = string.Empty;
    public bool RegexIgnoreCase { get; set; }
    public bool RegexReplaceAll { get; set; }

    public bool StripDates { get; set; }
    public string CaseTransform { get; set; } = string.Empty; // "UPPERCASE", "lowercase", "Title Case", "camelCase"

    public bool NumberingEnabled { get; set; }
    public string NumberingFormat { get; set; } = string.Empty; // "_001", "_01", "_1", "-001"

    public bool DateEnabled { get; set; }
    public string DateFormat { get; set; } = string.Empty;
    public DateTime? DateValue { get; set; }
    public bool DateIsPrefix { get; set; }

    public bool TagEnabled { get; set; }
    public string TagValue { get; set; } = string.Empty;
    public bool TagIsPrefix { get; set; }
}

public class RenameEngine
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public string BuildSmartNameFor(ExplorerItem item, int index, int totalCount, RenameOptions options, string smartDate, string tagValue)
    {
        var originalStem = item.BaseName;
        var extension = item.IsFolder ? string.Empty : Path.GetExtension(item.Name);
        var stem = originalStem;

        // Step 1: Regex Mode OR Find-and-Replace
        if (options.UseRegex)
        {
            var findText = options.RegexFind;
            var replaceText = options.RegexReplace;
            if (!string.IsNullOrEmpty(findText))
            {
                var regexOptions = options.RegexIgnoreCase 
                    ? System.Text.RegularExpressions.RegexOptions.IgnoreCase 
                    : System.Text.RegularExpressions.RegexOptions.None;
                
                try
                {
                    if (!options.RegexReplaceAll)
                    {
                        var regex = new System.Text.RegularExpressions.Regex(findText, regexOptions);
                        stem = regex.Replace(stem, replaceText, 1);
                    }
                    else
                    {
                        stem = System.Text.RegularExpressions.Regex.Replace(stem, findText, replaceText, regexOptions);
                    }
                }
                catch
                {
                    // If regex is invalid, retain current stem
                }
            }
        }

        // Step 2: Strip embedded dates
        if (options.StripDates)
        {
            var patterns = new[] 
            { 
                @"\b\d{4}-\d{2}-\d{2}\b", 
                @"\b\d{2}-\d{2}-\d{4}\b", 
                @"\b\d{4}_\d{2}_\d{2}\b",
                @"\b\d{8}\b"
            };
            foreach (var pattern in patterns)
            {
                stem = System.Text.RegularExpressions.Regex.Replace(stem, pattern, "");
            }
            stem = System.Text.RegularExpressions.Regex.Replace(stem, @"[_\-\s]+", "_");
            stem = stem.Trim('_', '-', ' ');
        }

        // Step 3: Case Transform
        if (!string.IsNullOrEmpty(options.CaseTransform))
        {
            if (options.CaseTransform == "UPPERCASE")
            {
                stem = stem.ToUpper();
            }
            else if (options.CaseTransform == "lowercase")
            {
                stem = stem.ToLower();
            }
            else if (options.CaseTransform == "Title Case")
            {
                stem = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(stem.ToLower());
            }
            else if (options.CaseTransform == "camelCase")
            {
                stem = ToCamelCase(stem);
            }
        }

        // Step 4: Sequential Numbering
        if (options.NumberingEnabled && !string.IsNullOrEmpty(options.NumberingFormat))
        {
            var fmt = options.NumberingFormat;
            var number = index + 1;
            var numStr = string.Empty;
            if (fmt == "_001") numStr = $"_{number:D3}";
            else if (fmt == "_01") numStr = $"_{number:D2}";
            else if (fmt == "_1") numStr = $"_{number}";
            else if (fmt == "-001") numStr = $"-{number:D3}";
            stem += numStr;
        }

        // Step 5: Prefixes and Suffixes (Date / Tags)
        var prefix = string.Empty;
        var suffix = string.Empty;

        if (options.DateEnabled && !string.IsNullOrEmpty(smartDate))
        {
            if (options.DateIsPrefix) prefix += smartDate + "_";
            else suffix += "_" + smartDate;
        }

        if (options.TagEnabled && !string.IsNullOrEmpty(tagValue))
        {
            if (options.TagIsPrefix) prefix += tagValue + "_";
            else suffix += "_" + tagValue;
        }

        return prefix + stem + suffix + extension;
    }

    public (bool ok, string msg, bool isWarn) ValidateSmartName(string originalName, string candidate, HashSet<string> usedNames, string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return (false, "Name cannot be empty.", false);

        if (candidate.IndexOfAny(InvalidChars) >= 0)
            return (false, "Name contains invalid characters.", false);

        if (candidate.Length > 260)
            return (false, "Full filename is too long.", false);

        if (candidate.StartsWith('.') || candidate.EndsWith('.'))
            return (false, "Name cannot start or end with a period.", false);

        if (usedNames.Contains(candidate))
            return (false, "Duplicate name in batch.", false);

        if (!string.IsNullOrEmpty(currentPath))
        {
            var target = Path.Combine(currentPath, candidate);
            if (File.Exists(target) || Directory.Exists(target))
            {
                if (originalName.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, string.Empty, false);
                }
                return (false, $"\"{candidate}\" already exists.", true);
            }
        }

        return (true, string.Empty, false);
    }

    private static string ToCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var words = s.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return s;
        var sb = new StringBuilder(words[0].ToLowerInvariant());
        for (int i = 1; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length > 0)
            {
                sb.Append(char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
            }
        }
        return sb.ToString();
    }
}
