using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace ParentControl.Overlay;

internal static class MarkdownDocument
{
    private static readonly System.Windows.Media.Brush Ink = new SolidColorBrush(Color.FromRgb(26, 41, 41));
    private static readonly System.Windows.Media.Brush Muted = new SolidColorBrush(Color.FromRgb(82, 97, 94));

    public static FlowDocument Create(string source)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 15,
            Foreground = Ink,
            LineHeight = 24
        };
        if (string.IsNullOrWhiteSpace(source))
        {
            document.Blocks.Add(new Paragraph(new Run("Администратор пока не добавил правила получения минут."))
            { Foreground = Muted });
            return document;
        }

        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var fence = new string((char)96, 3);
        var paragraphLines = new System.Collections.Generic.List<string>();
        void Flush()
        {
            if (paragraphLines.Count == 0) return;
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
            for (var lineIndex = 0; lineIndex < paragraphLines.Count; lineIndex++)
            {
                if (lineIndex > 0) paragraph.Inlines.Add(new LineBreak());
                AddInline(paragraph.Inlines, paragraphLines[lineIndex]);
            }
            document.Blocks.Add(paragraph);
            paragraphLines.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.Length == 0) { Flush(); continue; }
            if (trimmed.StartsWith(fence, StringComparison.Ordinal))
            {
                Flush();
                var code = new System.Collections.Generic.List<string>();
                while (++i < lines.Length && !lines[i].Trim().StartsWith(fence, StringComparison.Ordinal))
                    code.Add(lines[i]);
                document.Blocks.Add(new Paragraph(new Run(string.Join(Environment.NewLine, code)))
                {
                    FontFamily = new FontFamily("Consolas"), FontSize = 13,
                    Background = new SolidColorBrush(Color.FromRgb(233, 240, 234)),
                    Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 14)
                });
                continue;
            }

            var headingLevel = 0;
            while (headingLevel < 3 && headingLevel < trimmed.Length && trimmed[headingLevel] == '#')
                headingLevel++;
            if (headingLevel > 0 && headingLevel < trimmed.Length && trimmed[headingLevel] == ' ')
            {
                Flush();
                var heading = new Paragraph
                {
                    FontFamily = new FontFamily("Georgia"),
                    FontSize = headingLevel switch { 1 => 25, 2 => 21, _ => 18 },
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, headingLevel == 1 ? 0 : 8, 0, 10)
                };
                AddInline(heading.Inlines, trimmed[(headingLevel + 1)..]);
                document.Blocks.Add(heading);
                continue;
            }
            if (trimmed is "---" or "***")
            {
                Flush();
                document.Blocks.Add(new Paragraph(new Run(" "))
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(214, 226, 220)),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 8, 0, 12)
                });
                continue;
            }
            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                Flush();
                var quote = new Paragraph
                {
                    Foreground = Muted,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(95, 150, 148)),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 0, 0, 0), Margin = new Thickness(0, 4, 0, 12)
                };
                AddInline(quote.Inlines, trimmed[2..]);
                document.Blocks.Add(quote);
                continue;
            }

            var bullet = trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                         trimmed.StartsWith("* ", StringComparison.Ordinal);
            var numberEnd = trimmed.IndexOf(". ", StringComparison.Ordinal);
            var numbered = numberEnd > 0 && numberEnd < 4 &&
                           int.TryParse(trimmed[..numberEnd], out _);
            if (bullet || numbered)
            {
                Flush();
                var list = new System.Windows.Documents.List
                {
                    MarkerStyle = bullet ? TextMarkerStyle.Disc : TextMarkerStyle.Decimal,
                    Margin = new Thickness(8, 0, 0, 13)
                };
                do
                {
                    var text = bullet ? trimmed[2..] : trimmed[(numberEnd + 2)..];
                    var item = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                    AddInline(item.Inlines, text);
                    list.ListItems.Add(new ListItem(item));
                    if (i + 1 >= lines.Length) break;
                    var next = lines[i + 1].Trim();
                    var nextNumberEnd = next.IndexOf(". ", StringComparison.Ordinal);
                    var sameType = bullet
                        ? next.StartsWith("- ", StringComparison.Ordinal) || next.StartsWith("* ", StringComparison.Ordinal)
                        : nextNumberEnd > 0 && nextNumberEnd < 4 &&
                          int.TryParse(next[..nextNumberEnd], out _);
                    if (!sameType) break;
                    i++;
                    trimmed = next;
                    numberEnd = nextNumberEnd;
                } while (true);
                document.Blocks.Add(list);
                continue;
            }
            paragraphLines.Add(trimmed);
        }
        Flush();
        return document;
    }

    private static void AddInline(InlineCollection inlines, string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var marker = text.AsSpan(i).StartsWith("**", StringComparison.Ordinal) ? "**" :
                text[i] == '*' ? "*" : text[i] == (char)96 ? new string((char)96, 1) : "";
            if (marker.Length > 0)
            {
                var end = text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal);
                if (end > i + marker.Length)
                {
                    var value = text.Substring(i + marker.Length, end - i - marker.Length);
                    Inline inline = marker switch
                    {
                        "**" => new Bold(new Run(value)),
                        "*" => new Italic(new Run(value)),
                        _ => new Run(value)
                        {
                            FontFamily = new FontFamily("Consolas"),
                            Background = new SolidColorBrush(Color.FromRgb(233, 240, 234))
                        }
                    };
                    inlines.Add(inline);
                    i = end + marker.Length;
                    continue;
                }
            }
            var next = i + 1;
            while (next < text.Length && text[next] is not '*' && text[next] != (char)96) next++;
            inlines.Add(new Run(text[i..next]));
            i = next;
        }
    }
}
