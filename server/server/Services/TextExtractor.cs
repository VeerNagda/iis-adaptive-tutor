using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using UglyToad.PdfPig;
using A = DocumentFormat.OpenXml.Drawing;

namespace server.Services;

/// <summary>
/// Extracts plain text from PDF, DOCX, and PPTX files, returning one entry per page or slide.
/// Page numbers are 1-based throughout.
/// </summary>
public static class TextExtractor
{
    public sealed record PageText(int PageNumber, string Text);

    public static IReadOnlyList<PageText> Extract(string filePath, string fileType) =>
        fileType.ToLowerInvariant() switch
        {
            "pdf"  => ExtractPdf(filePath),
            "docx" => ExtractDocx(filePath),
            "pptx" => ExtractPptx(filePath),
            "txt"  => [new PageText(1, File.ReadAllText(filePath))],
            _      => throw new NotSupportedException($"Unsupported file type: {fileType}")
        };

    // ── PDF ───────────────────────────────────────────────────────────────────

    private static IReadOnlyList<PageText> ExtractPdf(string path)
    {
        using var doc = PdfDocument.Open(path);
        return doc.GetPages()
                  .Select(p => new PageText(
                      p.Number,
                      string.Join(" ", p.GetWords().Select(w => w.Text))))
                  .Where(p => !string.IsNullOrWhiteSpace(p.Text))
                  .ToList();
    }

    // ── DOCX ──────────────────────────────────────────────────────────────────
    // DOCX has no fixed page boundaries; we assign a "virtual page" every
    // 40 paragraphs so chunks can still carry meaningful page_start/page_end numbers.

    private static IReadOnlyList<PageText> ExtractDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body
            ?? throw new InvalidDataException("DOCX has no body.");

        var pages = new List<PageText>();
        int pageNum = 1;
        int paraCount = 0;
        var current = new System.Text.StringBuilder();

        foreach (var para in body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            var text = para.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            current.AppendLine(text);
            paraCount++;

            // Treat every 40 paragraphs as a new virtual page.
            if (paraCount % 40 == 0)
            {
                pages.Add(new PageText(pageNum++, current.ToString().Trim()));
                current.Clear();
            }
        }

        if (current.Length > 0)
            pages.Add(new PageText(pageNum, current.ToString().Trim()));

        return pages;
    }

    // ── PPTX ──────────────────────────────────────────────────────────────────
    // Each slide maps to one "page".

    private static IReadOnlyList<PageText> ExtractPptx(string path)
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
                return new PageText(i + 1, text);
            })
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .ToList();
    }
}
