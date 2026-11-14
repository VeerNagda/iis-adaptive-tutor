using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using server.LLM;
using UglyToad.PdfPig;
using A = DocumentFormat.OpenXml.Drawing;

namespace server.Services;

/// <summary>
/// Extracts the plain text and the embedded images from PDF, DOCX, and PPTX files,
/// returning one entry per page or slide. Page numbers are 1-based throughout.
///
/// Images are only collected here. IngestionService sends them to the LLM, which
/// writes a description of each one; that description is then added to the page's text.
/// </summary>
public static class TextExtractor
{
    /// <summary>
    /// UnreadableImages counts the pictures on the page that could not be handed
    /// to the LLM (unsupported format or damaged), so the ingestion report can say so.
    /// </summary>
    public sealed record PageText(int PageNumber, string Text, IReadOnlyList<LlmImage> Images, int UnreadableImages = 0);

    /// <summary>
    /// Every page of the file is returned, including pages with no text, so the
    /// caller can count and report them. includeImages = false skips the (slow)
    /// image extraction; the scope check before ingestion only needs the text.
    /// </summary>
    public static IReadOnlyList<PageText> Extract(string filePath, string fileType, bool includeImages = true) =>
        fileType.ToLowerInvariant() switch
        {
            "pdf"  => ExtractPdf(filePath, includeImages),
            "docx" => ExtractDocx(filePath, includeImages),
            "pptx" => ExtractPptx(filePath, includeImages),
            "txt"  => [new PageText(1, File.ReadAllText(filePath), [])],
            _      => throw new NotSupportedException($"Unsupported file type: {fileType}")
        };

    /// <summary>Image formats the LLM accepts. Others (EMF, WMF, GIF, ...) are skipped.</summary>
    private static bool IsSupported(string mimeType) => mimeType is "image/png" or "image/jpeg" or "image/webp";

    // ── PDF ───────────────────────────────────────────────────────────────────

    private static IReadOnlyList<PageText> ExtractPdf(string path, bool includeImages)
    {
        using var doc = PdfDocument.Open(path);
        return doc.GetPages()
                  .Select(p =>
                  {
                      int unreadable = 0;
                      List<LlmImage> images = includeImages ? PdfImages(p, out unreadable) : [];
                      return new PageText(p.Number, string.Join(" ", p.GetWords().Select(w => w.Text)), images, unreadable);
                  })
                  .ToList();
    }

    /// <summary>
    /// The images on one PDF page. A PDF stores images in its own internal formats:
    /// PdfPig can convert most of them to PNG; photos are usually stored as a
    /// complete JPEG file (it starts with the bytes FF D8), which is used as it is.
    /// Anything else is skipped and counted in <paramref name="unreadable"/>.
    /// </summary>
    private static List<LlmImage> PdfImages(UglyToad.PdfPig.Content.Page page, out int unreadable)
    {
        var images = new List<LlmImage>();
        unreadable = 0;
        foreach (var image in page.GetImages())
        {
            try
            {
                if (image.TryGetPng(out var png))
                    images.Add(new LlmImage(png, "image/png"));
                else if (image.RawBytes.Length > 2 && image.RawBytes[0] == 0xFF && image.RawBytes[1] == 0xD8)
                    images.Add(new LlmImage(image.RawBytes.ToArray(), "image/jpeg"));
                else
                    unreadable++;
            }
            catch (Exception)
            {
                // A damaged or unusual image must not stop the whole document: skip it.
                unreadable++;
            }
        }
        return images;
    }

    // ── DOCX ──────────────────────────────────────────────────────────────────
    // DOCX has no fixed page boundaries; we assign a "virtual page" every
    // 40 paragraphs so chunks can still carry meaningful page_start/page_end numbers.

    private static IReadOnlyList<PageText> ExtractDocx(string path, bool includeImages)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var main = doc.MainDocumentPart;
        var body = main?.Document?.Body
            ?? throw new InvalidDataException("DOCX has no body.");

        var pages = new List<PageText>();
        int pageNum = 1;
        int paraCount = 0;
        var current = new System.Text.StringBuilder();
        var images = new List<LlmImage>();
        int unreadable = 0;

        foreach (var para in body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            // A picture sits inside a paragraph as a "blip" that points (by id) at
            // the image file stored in the document.
            foreach (var blip in includeImages ? para.Descendants<A.Blip>() : [])
            {
                if (blip.Embed?.Value is { } id && main!.GetPartById(id) is ImagePart part && IsSupported(part.ContentType))
                    images.Add(ReadImage(part));
                else
                    unreadable++;
            }

            var text = para.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            current.AppendLine(text);
            paraCount++;

            // Treat every 40 paragraphs as a new virtual page.
            if (paraCount % 40 == 0)
            {
                pages.Add(new PageText(pageNum++, current.ToString().Trim(), images, unreadable));
                current.Clear();
                images = [];
                unreadable = 0;
            }
        }

        if (current.Length > 0 || images.Count > 0 || unreadable > 0)
            pages.Add(new PageText(pageNum, current.ToString().Trim(), images, unreadable));

        return pages;
    }

    // ── PPTX ──────────────────────────────────────────────────────────────────
    // Each slide maps to one "page".

    private static IReadOnlyList<PageText> ExtractPptx(string path, bool includeImages)
    {
        using var doc = PresentationDocument.Open(path, false);
        var parts = doc.PresentationPart?.SlideParts?.ToList()
            ?? throw new InvalidDataException("PPTX has no slides.");

        return parts
            .Select((slide, i) =>
            {
                // Collect all text runs from every shape on this slide.
                var text = string.Join(" ",
                    (slide.Slide?.Descendants<A.Text>() ?? [])
                               .Select(t => t.Text)
                               .Where(t => !string.IsNullOrWhiteSpace(t)));
                // The picture files used on this slide.
                List<LlmImage> images = includeImages
                    ? slide.ImageParts.Where(p => IsSupported(p.ContentType)).Select(ReadImage).ToList()
                    : [];
                int unreadable = includeImages ? slide.ImageParts.Count(p => !IsSupported(p.ContentType)) : 0;
                return new PageText(i + 1, text, images, unreadable);
            })
            .ToList();
    }

    private static LlmImage ReadImage(ImagePart part)
    {
        using var stream = part.GetStream();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new LlmImage(memory.ToArray(), part.ContentType);
    }
}
