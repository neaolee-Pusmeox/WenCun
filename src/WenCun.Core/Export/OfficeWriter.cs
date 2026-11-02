using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using D = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using P = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;
using WenCun.Core.Domain;

namespace WenCun.Core.Export;

public static class OfficeWriter
{
    private const int PageWidth = 11906;
    private const int Margin = 1440;
    private const int ContentWidth = PageWidth - Margin * 2;

    public static void Word(string path, Article article, ArticleContent content, IReadOnlyDictionary<string, string> assets)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new W.Body();
        main.Document = new W.Document(body);

        body.Append(Paragraph(article.Title, 32, bold: true));
        var meta = string.Join(" · ", new[]
        {
            article.Account, article.Author,
            article.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        body.Append(Paragraph(meta, 18, color: "6B7686"));
        body.Append(Paragraph(article.Url, 16, color: "2F6FEB"));
        foreach (var warning in content.Warnings) body.Append(Paragraph("注意：" + warning, 18, color: "8A5A00"));

        using var html = new HtmlParser().ParseDocument("<div id='root'>" + content.BodyHtml + "</div>");
        foreach (var child in html.QuerySelector("#root")!.ChildNodes)
            WriteBlock(child, body, main, assets, 0);

        body.Append(new W.SectionProperties(
            new W.PageSize { Width = PageWidth, Height = 16838 },
            new W.PageMargin { Top = Margin, Right = Margin, Bottom = Margin, Left = Margin }));
        main.Document.Save();
    }

    private static void WriteBlock(INode node, W.Body body, MainDocumentPart main, IReadOnlyDictionary<string, string> assets, int depth)
    {
        if (depth > 64) return;
        if (node is IText text)
        {
            if (!string.IsNullOrWhiteSpace(text.Data)) body.Append(Paragraph(text.Data.Trim(), 21));
            return;
        }
        if (node is not IElement element) return;
        switch (element.LocalName)
        {
            case "h1": body.Append(Paragraph(element.TextContent.Trim(), 28, bold: true)); return;
            case "h2": body.Append(Paragraph(element.TextContent.Trim(), 26, bold: true)); return;
            case "h3": body.Append(Paragraph(element.TextContent.Trim(), 24, bold: true)); return;
            case "h4" or "h5" or "h6": body.Append(Paragraph(element.TextContent.Trim(), 22, bold: true)); return;
            case "p" or "div" or "section" or "article" or "figure" or "figcaption":
                if (element.Children.Any(child => child.LocalName is "p" or "div" or "section" or "article" or "figure" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "ul" or "ol" or "blockquote" or "pre" or "table" or "hr"))
                {
                    foreach (var child in element.ChildNodes) WriteBlock(child, body, main, assets, depth + 1);
                    return;
                }
                var paragraph = new W.Paragraph();
                AppendInline(element, paragraph, main, assets, false, false);
                if (paragraph.HasChildren) body.Append(paragraph);
                return;
            case "ul" or "ol":
                var index = 1;
                foreach (var item in element.Children.Where(child => child.LocalName == "li"))
                {
                    var bullet = element.LocalName == "ol" ? $"{index++}. " : "• ";
                    var listItem = new W.Paragraph(new W.ParagraphProperties(new W.Indentation { Left = "360" }));
                    listItem.Append(Run(bullet, 21));
                    AppendInline(item, listItem, main, assets, false, false);
                    body.Append(listItem);
                }
                return;
            case "blockquote":
                var quote = new W.Paragraph(new W.ParagraphProperties(new W.Indentation { Left = "720" }));
                AppendInline(element, quote, main, assets, false, true);
                body.Append(quote);
                return;
            case "pre":
                body.Append(Paragraph(element.TextContent.TrimEnd(), 18, mono: true));
                return;
            case "table":
                body.Append(Table(element, main, assets));
                return;
            case "img":
                var image = new W.Paragraph();
                AppendImage(element, image, main, assets);
                body.Append(image);
                return;
            case "audio":
                body.Append(Paragraph("[音频文件见导出目录]", 18, color: "6B7686"));
                return;
            case "hr":
                body.Append(new W.Paragraph(new W.ParagraphProperties(new W.ParagraphBorders(
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 6, Color = "D0D4DA" }))));
                return;
            default:
                var hasBlock = element.Children.Any(child => child.LocalName is "p" or "div" or "section" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "ul" or "ol" or "blockquote" or "pre" or "table");
                if (hasBlock)
                {
                    foreach (var child in element.ChildNodes) WriteBlock(child, body, main, assets, depth + 1);
                }
                else if (!string.IsNullOrWhiteSpace(element.TextContent))
                {
                    var inline = new W.Paragraph();
                    AppendInline(element, inline, main, assets, false, false);
                    body.Append(inline);
                }
                return;
        }
    }

    private static void AppendInline(IElement element, W.Paragraph paragraph, MainDocumentPart main,
        IReadOnlyDictionary<string, string> assets, bool bold, bool italic)
    {
        foreach (var child in element.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    if (text.Data.Length > 0) paragraph.Append(Run(text.Data, 21, bold, italic));
                    break;
                case IElement childElement:
                    switch (childElement.LocalName)
                    {
                        case "br": paragraph.Append(new W.Run(new W.Break())); break;
                        case "img": AppendImage(childElement, paragraph, main, assets); break;
                        case "strong" or "b": AppendInline(childElement, paragraph, main, assets, true, italic); break;
                        case "em" or "i": AppendInline(childElement, paragraph, main, assets, bold, true); break;
                        case "audio": paragraph.Append(Run("[音频]", 18, false, true)); break;
                        default: AppendInline(childElement, paragraph, main, assets, bold, italic); break;
                    }
                    break;
            }
        }
    }

    private static void AppendImage(IElement element, W.Paragraph paragraph, MainDocumentPart main, IReadOnlyDictionary<string, string> assets)
    {
        var source = element.GetAttribute("src");
        if (source is null || !assets.TryGetValue(source, out var local) || !File.Exists(local))
        {
            paragraph.Append(Run(source is null ? "[图片未归档]" : "[图片：" + source + "]", 18, false, true));
            return;
        }
        var extension = Path.GetExtension(local).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".gif"))
        {
            paragraph.Append(Run("[图片：" + Path.GetFileName(local) + "]", 18, false, true));
            return;
        }
        var part = main.AddImagePart(extension == ".png" ? ImagePartType.Png : extension == ".gif" ? ImagePartType.Gif : ImagePartType.Jpeg);
        using (var stream = File.OpenRead(local)) part.FeedData(stream);
        var (width, height) = ImageSize(local);
        var maxWidth = ContentWidth * 635L;
        var maxHeight = 8229600L;
        var scale = Math.Min(9525d, Math.Min(maxWidth / (double)width, maxHeight / (double)height));
        var cx = Math.Max(1L, (long)(width * scale));
        var cy = Math.Max(1L, (long)(height * scale));
        var id = (uint)(main.ImageParts.Count() + 1);
        var picture = new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "image" + id },
                new P.NonVisualPictureDrawingProperties()),
            new P.BlipFill(new D.Blip { Embed = main.GetIdOfPart(part) }, new D.Stretch(new D.FillRectangle())),
            new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = 0, Y = 0 }, new D.Extents { Cx = cx, Cy = cy }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle }));
        var drawing = new W.Drawing(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.DocProperties { Id = id, Name = "image" + id },
            new D.Graphic(new D.GraphicData(picture) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 });
        paragraph.Append(new W.Run(drawing));
    }

    private static W.Table Table(IElement element, MainDocumentPart main, IReadOnlyDictionary<string, string> assets)
    {
        var rows = element.QuerySelectorAll("tr").ToArray();
        var columns = Math.Max(1, rows.Select(row => row.Children.Count(cell => cell.LocalName is "td" or "th")).DefaultIfEmpty(1).Max());
        var width = ContentWidth / columns;
        var table = new W.Table(
            new W.TableProperties(new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
                new W.TableLayout { Type = W.TableLayoutValues.Fixed }),
            new W.TableGrid(Enumerable.Range(0, columns).Select(_ => new W.GridColumn { Width = width.ToString(CultureInfo.InvariantCulture) })));
        foreach (var row in rows)
        {
            var tableRow = new W.TableRow();
            foreach (var cell in row.Children.Where(child => child.LocalName is "td" or "th"))
            {
                var tableCell = new W.TableCell(new W.TableCellProperties(new W.TableCellWidth
                {
                    Width = width.ToString(CultureInfo.InvariantCulture), Type = W.TableWidthUnitValues.Dxa
                }));
                var paragraph = new W.Paragraph();
                AppendInline(cell, paragraph, main, assets, cell.LocalName == "th", false);
                tableCell.Append(paragraph);
                tableRow.Append(tableCell);
            }
            table.Append(tableRow);
        }
        return table;
    }

    private static W.Paragraph Paragraph(string text, int halfPoints, bool bold = false, bool italic = false,
        string? color = null, bool mono = false)
    {
        var paragraph = new W.Paragraph();
        if (text.Length > 0) paragraph.Append(Run(text, halfPoints, bold, italic, color, mono));
        return paragraph;
    }

    private static W.Run Run(string text, int halfPoints, bool bold = false, bool italic = false,
        string? color = null, bool mono = false)
    {
        var properties = new W.RunProperties(
            new W.RunFonts
            {
                Ascii = mono ? "Consolas" : "Microsoft YaHei",
                HighAnsi = mono ? "Consolas" : "Microsoft YaHei",
                EastAsia = "Microsoft YaHei"
            });
        if (bold) properties.Append(new W.Bold());
        if (italic) properties.Append(new W.Italic());
        if (color is not null) properties.Append(new W.Color { Val = color });
        properties.Append(new W.FontSize { Val = halfPoints.ToString(CultureInfo.InvariantCulture) });
        return new W.Run(properties, new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static (int Width, int Height) ImageSize(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 24 && bytes[0] == 0x89 && bytes[1] == 0x50)
            return (ReadInt32BigEndian(bytes, 16), ReadInt32BigEndian(bytes, 20));
        if (bytes.Length > 10 && bytes[0] == 0x47 && bytes[1] == 0x49)
            return (bytes[6] | bytes[7] << 8, bytes[8] | bytes[9] << 8);
        if (bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            var index = 2;
            while (index + 9 < bytes.Length)
            {
                if (bytes[index] != 0xFF) { index++; continue; }
                var marker = bytes[index + 1];
                if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3)
                    return (bytes[index + 7] << 8 | bytes[index + 8], bytes[index + 5] << 8 | bytes[index + 6]);
                var length = bytes[index + 2] << 8 | bytes[index + 3];
                if (length <= 0) break;
                index += length + 2;
            }
        }
        return (600, 400);
    }

    private static int ReadInt32BigEndian(byte[] bytes, int offset) =>
        bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];
}
