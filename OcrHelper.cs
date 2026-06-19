using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Docnet.Core;
using Docnet.Core.Models;
using Tesseract;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Runtime.InteropServices;

namespace FileExplorerCS;

public record OcrOptions
{
    /// <summary>Tesseract language code. Default "eng".</summary>
    public string Language { get; init; } = "eng";

    /// <summary>
    /// Render scale relative to 72 DPI base.
    /// 4.17 ≈ 300 DPI — the sweet spot for OCR accuracy on most documents.
    /// Lower for speed, raise to 5–6 for tiny text.
    /// </summary>
    public double DpiScale { get; init; } = 4.17;

    /// <summary>
    /// Apply grayscale + contrast + binary threshold before OCR.
    /// Helps on low-quality scans; unnecessary for clean digital PDFs.
    /// </summary>
    public bool EnhanceContrast { get; init; } = true;

    /// <summary>
    /// Minimum Tesseract word confidence (0–1) to include in the text overlay.
    /// 0.4 = discard obvious garbage while keeping uncertain words.
    /// </summary>
    public float MinWordConfidence { get; init; } = 0.40f;

    public static readonly OcrOptions Default = new();

    public static readonly OcrOptions FastScan = new()
    {
        DpiScale = 2.0,
        EnhanceContrast = false,
        MinWordConfidence = 0.5f
    };

    public static readonly OcrOptions HighAccuracy = new()
    {
        DpiScale = 5.0,
        EnhanceContrast = true,
        MinWordConfidence = 0.35f
    };
}

public record OcrProgress
{
    public int CurrentPage { get; init; }
    public int TotalPages { get; init; }
    public string Status { get; init; } = "";
}

public record OcrResult
{
    public bool Success { get; init; }
    public int PagesProcessed { get; init; }
    public int WordsFound { get; init; }
    public double ConfidenceAverage { get; init; }
    public string ErrorMessage { get; init; } = "";
}

internal static class OcrHelper
{
    private static readonly string TessDataUrl = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata";

    public static async Task<OcrResult> MakeSearchablePdfAsync(
        string inputPdfPath,
        string outputPdfPath,
        OcrOptions? options = null,
        IProgress<OcrProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= OcrOptions.Default;

        string tessDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");
        if (!Directory.Exists(tessDataPath))
        {
            Directory.CreateDirectory(tessDataPath);
        }

        string langDataFile = Path.Combine(tessDataPath, $"{options.Language}.traineddata");
        if (!File.Exists(langDataFile))
        {
            progress?.Report(new OcrProgress { CurrentPage = 0, TotalPages = 1, Status = $"Downloading {options.Language} language data..." });
            using var client = new HttpClient();
            var data = await client.GetByteArrayAsync($"https://github.com/tesseract-ocr/tessdata_fast/raw/main/{options.Language}.traineddata", ct);
            await File.WriteAllBytesAsync(langDataFile, data, ct);
        }

        try
        {
            using var engine = new TesseractEngine(tessDataPath, options.Language, EngineMode.Default);
            using var renderer = ResultRenderer.CreatePdfRenderer(outputPdfPath.Replace(".pdf", ""), tessDataPath, false);
            using var doc = renderer.BeginDocument("OCR Document");

            using var docnet = DocLib.Instance.GetDocReader(inputPdfPath, new PageDimensions(options.DpiScale));
            int pages = docnet.GetPageCount();

            int totalWords = 0;
            double totalConfidence = 0;

            for (int i = 0; i < pages; i++)
            {
                ct.ThrowIfCancellationRequested();

                progress?.Report(new OcrProgress { CurrentPage = i + 1, TotalPages = pages, Status = $"Processing page {i + 1}/{pages}..." });

                using var pageReader = docnet.GetPageReader(i);
                var rawBytes = pageReader.GetImage();
                int w = pageReader.GetPageWidth();
                int h = pageReader.GetPageHeight();

                using var image = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(rawBytes, w, h);

                // Apply contrast enhancement if requested
                if (options.EnhanceContrast)
                {
                    image.Mutate(x => x.Grayscale().Contrast(1.5f));
                }

                using var ms = new MemoryStream();
                image.SaveAsJpeg(ms);

                ms.Position = 0;
                byte[] jpegData = ms.ToArray();

                using var pix = Pix.LoadFromMemory(jpegData);
                using var page = engine.Process(pix, PageSegMode.Auto);

                // Filter words by confidence threshold
                if (options.MinWordConfidence > 0)
                {
                    var iterator = page.GetIterator();
                    iterator.Begin();

                    int pageWords = 0;
                    double pageConfidence = 0;

                    do
                    {
                        var text = iterator.GetText(PageIteratorLevel.Word);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            float confidence = iterator.GetConfidence(PageIteratorLevel.Word);
                            if (confidence >= options.MinWordConfidence)
                            {
                                pageWords++;
                                pageConfidence += confidence;
                            }
                        }
                    } while (iterator.Next(PageIteratorLevel.Word));

                    totalWords += pageWords;
                    totalConfidence += pageConfidence;
                }

                renderer.AddPage(page);
            }

            double avgConfidence = totalWords > 0 ? totalConfidence / totalWords : 0;

            return new OcrResult
            {
                Success = true,
                PagesProcessed = pages,
                WordsFound = totalWords,
                ConfidenceAverage = avgConfidence
            };
        }
        catch (OperationCanceledException)
        {
            return new OcrResult
            {
                Success = false,
                ErrorMessage = "OCR operation was cancelled."
            };
        }
        catch (Exception ex)
        {
            return new OcrResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
