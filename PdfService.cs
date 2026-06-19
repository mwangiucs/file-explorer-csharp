using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SixLabors.ImageSharp;

namespace FileExplorerCS;

public interface IPdfService
{
    Task MergePdfFilesAsync(string destPath, List<PdfPageItem> pdfPages, IProgress<string> progress);
    Task ConvertImagesToPdfAsync(string outputPath, List<string> imagePaths, IProgress<string> progress);
    Task CompressPdfAsync(string srcPath, string destPath, bool useGhostscript, int maxEdgePx, int jpegQuality, IProgress<string> progress);
    Task SplitPdfAsync(string srcPath, string outputDir, int pagesPerSplit, IProgress<string> progress);
    Task ConvertFilesToPdfAsync(List<string> filePaths, string outputDir, List<string> imageExtensions, List<string> textExtensions, List<string> textFileBasenames, IProgress<string> progress);
}

public class PdfService : IPdfService
{
    public async Task MergePdfFilesAsync(string destPath, List<PdfPageItem> pdfPages, IProgress<string> progress)
    {
        var sourceFullPaths = pdfPages.Select(p => Path.GetFullPath(p.SourceFilePath)).Distinct().ToList();

        await Task.Run(() =>
        {
            WriteFileViaTempIfOverlapsSources(destPath, sourceFullPaths, tempOrFinalPath =>
            {
                using var outputDocument = new PdfSharp.Pdf.PdfDocument();
                var cachedDocs = new Dictionary<string, PdfSharp.Pdf.PdfDocument>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    int count = 0;
                    foreach (var page in pdfPages)
                    {
                        count++;
                        progress.Report($"Merging page {count} of {pdfPages.Count}...");

                        if (!cachedDocs.TryGetValue(page.SourceFilePath, out var srcPdf))
                        {
                            srcPdf = PdfSharp.Pdf.IO.PdfReader.Open(page.SourceFilePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                            cachedDocs[page.SourceFilePath] = srcPdf;
                        }

                        var srcPage = srcPdf.Pages[page.PageNumber - 1];
                        outputDocument.AddPage(srcPage);
                    }
                    outputDocument.Save(tempOrFinalPath);
                }
                finally
                {
                    foreach (var doc in cachedDocs.Values)
                    {
                        doc.Dispose();
                    }
                }
            });
        });
    }

    public async Task ConvertImagesToPdfAsync(string outputPath, List<string> imagePaths, IProgress<string> progress)
    {
        await Task.Run(() =>
        {
            using var doc = new PdfSharp.Pdf.PdfDocument();
            int count = 0;
            foreach (var imgPath in imagePaths)
            {
                count++;
                progress.Report($"Converting image {count} of {imagePaths.Count}...");

                var page = doc.AddPage();
                using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                using var image = PdfSharp.Drawing.XImage.FromFile(imgPath);

                page.Width = PdfSharp.Drawing.XUnit.FromPoint(image.PointWidth);
                page.Height = PdfSharp.Drawing.XUnit.FromPoint(image.PointHeight);

                gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
            }
            doc.Save(outputPath);
        });
    }

    public async Task CompressPdfAsync(string srcPath, string destPath, bool useGhostscript, int maxEdgePx, int jpegQuality, IProgress<string> progress)
    {
        await Task.Run(() =>
        {
            WriteFileViaTempIfOverlapsSources(destPath, [srcPath], tempOrFinalPath =>
            {
                bool success = false;
                string? gsError = null;

                if (useGhostscript)
                {
                    progress.Report("Compressing PDF: running Ghostscript...");
                    success = PdfCompressionHelper.TryCompressWithGhostscript(srcPath, tempOrFinalPath, out gsError);
                }

                if (!success)
                {
                    progress.Report("Compressing PDF: recompressing embedded images...");
                    PdfCompressionHelper.RecompressEmbeddedJpegImages(srcPath, tempOrFinalPath, maxEdgePx, jpegQuality);
                }
            });
        });
    }

    public async Task SplitPdfAsync(string srcPath, string outputDir, int pagesPerSplit, IProgress<string> progress)
    {
        await Task.Run(() =>
        {
            using var srcPdf = PdfSharp.Pdf.IO.PdfReader.Open(srcPath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            int pageCount = srcPdf.PageCount;

            if (pageCount < 2)
            {
                throw new InvalidOperationException("PDF must have at least 2 pages to split.");
            }

            int partNumber = 1;
            string sourceName = Path.GetFileNameWithoutExtension(srcPath);
            for (int i = 0; i < pageCount; i += pagesPerSplit)
            {
                int endPage = Math.Min(i + pagesPerSplit, pageCount);
                string outputPath = Path.Combine(outputDir, $"{sourceName}_part{partNumber}.pdf");
                string fullPath = Path.GetFullPath(outputPath);

                progress.Report($"Splitting: creating part {partNumber}...");

                using var destPdf = new PdfSharp.Pdf.PdfDocument();
                for (int p = i; p < endPage; p++)
                {
                    destPdf.AddPage(srcPdf.Pages[p]);
                }
                destPdf.Save(fullPath);
                partNumber++;
            }
        });
    }

    public async Task ConvertFilesToPdfAsync(List<string> filePaths, string outputDir, List<string> imageExtensions, List<string> textExtensions, List<string> textFileBasenames, IProgress<string> progress)
    {
        await Task.Run(() =>
        {
            int convertedCount = 0;
            int failedCount = 0;
            string? lastErrorMsg = null;

            foreach (var file in filePaths)
            {
                try
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    string outputFileName = Path.GetFileNameWithoutExtension(file) + ".pdf";
                    string outputPath = Path.Combine(outputDir, outputFileName);

                    progress.Report($"Converting file {convertedCount + failedCount + 1} of {filePaths.Count} ({Path.GetFileName(file)})...");

                    if (imageExtensions.Contains(ext))
                    {
                        ConvertImageToPdf(file, outputPath);
                        convertedCount++;
                    }
                    else if (textExtensions.Contains(ext) || textFileBasenames.Contains(Path.GetFileName(file).ToLowerInvariant()))
                    {
                        ConvertTextToPdf(file, outputPath);
                        convertedCount++;
                    }
                    else
                    {
                        failedCount++;
                    }
                }
                catch (Exception ex)
                {
                    failedCount++;
                    lastErrorMsg = ex.Message;
                }
            }

            if (failedCount > 0 && lastErrorMsg != null)
            {
                throw new Exception($"Converted {convertedCount} file(s), {failedCount} failed. Last error: {lastErrorMsg}");
            }
        });
    }

    private static void ConvertImageToPdf(string imagePath, string outputPath)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
        using var image = PdfSharp.Drawing.XImage.FromFile(imagePath);

        page.Width = PdfSharp.Drawing.XUnit.FromPoint(image.PointWidth);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(image.PointHeight);

        gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
        doc.Save(outputPath);
    }

    private static void ConvertTextToPdf(string textPath, string outputPath)
    {
        string textContent = File.ReadAllText(textPath);
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);

        var font = new PdfSharp.Drawing.XFont("Courier New", 10);
        
        string[] lines = textContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        double y = 20;
        double margin = 20;
        double lineHeight = 12;

        try
        {
            foreach (var line in lines)
            {
                gfx.DrawString(line, font, PdfSharp.Drawing.XBrushes.Black, margin, y);
                y += lineHeight;

                if (y > page.Height.Point - margin)
                {
                    gfx.Dispose();
                    page = doc.AddPage();
                    gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                    y = margin;
                }
            }
        }
        finally
        {
            gfx.Dispose();
        }

        doc.Save(outputPath);
    }

    private static void WriteFileViaTempIfOverlapsSources(string finalPath, IEnumerable<string> sourcePaths, Action<string> writeToPath)
    {
        string fullFinal = Path.GetFullPath(finalPath);
        bool overlaps = sourcePaths.Any(s =>
            string.Equals(Path.GetFullPath(s), fullFinal, StringComparison.OrdinalIgnoreCase));

        if (!overlaps)
        {
            writeToPath(fullFinal);
            return;
        }

        string temp = Path.Combine(Path.GetTempPath(), $"pdfop_{Guid.NewGuid():N}.pdf");
        try
        {
            writeToPath(temp);
            File.Copy(temp, fullFinal, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }
}
