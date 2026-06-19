using System.Diagnostics;
using System.IO;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;
using Size = SixLabors.ImageSharp.Size;

namespace FileExplorerCS;

internal static class PdfCompressionHelper
{
    /// <summary>Strong: Ghostscript with /ebook profile — large gains on scanned PDFs if Ghostscript is installed.</summary>
    public static bool TryCompressWithGhostscript(string sourcePath, string destPath, out string? errorMessage)
    {
        errorMessage = null;
        string? gs = FindGhostscriptExecutable();
        if (string.IsNullOrEmpty(gs))
        {
            errorMessage = "Ghostscript (gswin64c.exe) not found. Install from https://ghostscript.com or use Recompress images mode.";
            return false;
        }

        string args =
            "-sDEVICE=pdfwrite " +
            "-dCompatibilityLevel=1.4 " +
            "-dPDFSETTINGS=/ebook " +
            "-dColorImageDownsampleType=/Bicubic -dGrayImageDownsampleType=/Bicubic -dMonoImageDownsampleType=/Bicubic " +
            "-dNOPAUSE -dBATCH -dQUIET " +
            $"-sOutputFile=\"{destPath}\" \"{sourcePath}\"";

        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = gs,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            p.Start();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);
            if (p.ExitCode != 0)
            {
                errorMessage = string.IsNullOrWhiteSpace(err) ? $"Ghostscript exited with code {p.ExitCode}." : err.Trim();
                return false;
            }

            return File.Exists(destPath) && new FileInfo(destPath).Length > 0;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Rewrites JPEG image streams at lower quality / max dimension. Best on scan-heavy PDFs when Ghostscript is unavailable.
    /// </summary>
    public static void RecompressEmbeddedJpegImages(string sourcePath, string destPath, int maxEdgePx, int jpegQuality)
    {
        using var pdfDoc = PdfSharp.Pdf.IO.PdfReader.Open(sourcePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);

        int pages = pdfDoc.PageCount;
        for (int i = 0; i < pages; i++)
        {
            var page = pdfDoc.Pages[i];
            var resources = page.Elements.GetDictionary("/Resources");
            if (resources == null) continue;

            var xObjects = resources.Elements.GetDictionary("/XObject");
            if (xObjects == null) continue;

            foreach (var key in xObjects.Elements.Keys)
            {
                var item = xObjects.Elements[key];
                if (item is PdfReference reference)
                {
                    var xObject = reference.Value as PdfDictionary;
                    if (xObject == null) continue;

                    var subtype = xObject.Elements.GetName("/Subtype");
                    if (subtype != "/Image" && subtype != "Image") continue;

                    // Check if DCTDecode (JPEG)
                    var filter = xObject.Elements["/Filter"];
                    bool isJpeg = false;
                    if (filter is PdfName name && (name.Value == "/DCTDecode" || name.Value == "DCTDecode" || name.Value == "/DCT" || name.Value == "DCT"))
                    {
                        isJpeg = true;
                    }
                    else if (filter is PdfArray array)
                    {
                        foreach (var element in array.Elements)
                        {
                            if (element is PdfName n && (n.Value == "/DCTDecode" || n.Value == "DCTDecode" || n.Value == "/DCT" || n.Value == "DCT"))
                            {
                                isJpeg = true;
                                break;
                            }
                        }
                    }

                    if (!isJpeg) continue;

                    if (xObject.Stream != null)
                    {
                        byte[] raw = xObject.Stream.Value;
                        if (raw == null || raw.Length == 0) continue;

                        try
                        {
                            using var img = Image.Load<Rgba32>(raw);
                            int w = img.Width;
                            int h = img.Height;
                            if (w > maxEdgePx || h > maxEdgePx)
                            {
                                img.Mutate(x => x.Resize(new ResizeOptions
                                {
                                    Size = new Size(maxEdgePx, maxEdgePx),
                                    Mode = ResizeMode.Max
                                }));
                            }

                            using var ms = new MemoryStream();
                            var encoder = new JpegEncoder { Quality = jpegQuality };
                            img.SaveAsJpeg(ms, encoder);
                            byte[] jpegOut = ms.ToArray();

                            if (jpegOut.Length >= raw.Length)
                            {
                                continue;
                            }

                            xObject.Stream.Value = jpegOut;
                            xObject.Elements.SetInteger("/Width", img.Width);
                            xObject.Elements.SetInteger("/Height", img.Height);
                        }
                        catch
                        {
                            // Skip images we cannot decode/replace
                        }
                    }
                }
            }
        }

        pdfDoc.Save(destPath);
    }

    private static string? FindGhostscriptExecutable()
    {
        foreach (string exe in new[] { "gswin64c.exe", "gswin32c.exe", "gs.exe" })
        {
            try
            {
                using var which = Process.Start(new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = exe,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });
                if (which is null)
                {
                    continue;
                }

                string? line = which.StandardOutput.ReadLine();
                which.WaitForExit(5000);
                if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim()))
                {
                    return line.Trim();
                }
            }
            catch
            {
                // Ignore.
            }
        }

        foreach (string root in new[]
                 {
                     @"C:\Program Files\gs",
                     @"C:\Program Files (x86)\gs"
                 })
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (string dir in Directory.GetDirectories(root))
                {
                    foreach (string exeName in new[] { "gswin64c.exe", "gswin32c.exe" })
                    {
                        string candidate = Path.Combine(dir, "bin", exeName);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
            catch
            {
                // Ignore.
            }
        }

        return null;
    }
}
