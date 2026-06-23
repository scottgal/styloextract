using System.Text;
using AngleSharp.Dom;

namespace StyloExtract.Heuristics;

/// <summary>
/// Walks a block element's subtree and emits structured Markdown that preserves
/// heading levels, paragraph breaks, list/code/table structure, inline emphasis,
/// inline code, links, and images. The output is what consumers of
/// <c>ExtractedBlock.Markdown</c> read instead of the flat <c>Text</c> projection.
///
/// One renderer instance handles a single block subtree; instances are not
/// thread-safe. <see cref="Render(IElement)"/> is the entry point.
/// </summary>
internal sealed class DomMarkdownWalker
{
    private readonly StringBuilder _out = new();

    public static string Render(IElement element)
    {
        var w = new DomMarkdownWalker();
        w.WriteBlockChildren(element);
        return w._out.ToString().Trim() + "\n";
    }

    private void WriteBlockChildren(IElement element)
    {
        foreach (var child in element.ChildNodes)
        {
            WriteNode(child);
        }
    }

    private void WriteNode(INode node)
    {
        if (node is IText t)
        {
            _out.Append(EscapeInline(t.TextContent));
            return;
        }
        if (node is not IElement el) return;
        var tag = el.TagName.ToLowerInvariant();
        switch (tag)
        {
            case "script":
            case "style":
            case "noscript":
            case "template":
                return;

            case "h1": WriteHeading(el, 1); return;
            case "h2": WriteHeading(el, 2); return;
            case "h3": WriteHeading(el, 3); return;
            case "h4": WriteHeading(el, 4); return;
            case "h5": WriteHeading(el, 5); return;
            case "h6": WriteHeading(el, 6); return;

            case "p":
                EnsureBlankLine();
                WriteInlineChildren(el);
                EnsureBlankLine();
                return;

            case "br":
                _out.Append("  \n");
                return;

            case "hr":
                EnsureBlankLine();
                _out.Append("---");
                EnsureBlankLine();
                return;

            case "ul": WriteList(el, ordered: false); return;
            case "ol": WriteList(el, ordered: true); return;

            case "pre": WritePre(el); return;
            case "blockquote": WriteBlockquote(el); return;
            case "table": WriteTable(el); return;

            case "figure": WriteFigure(el); return;
            case "img": WriteInlineImage(el); return;

            // Inline structural elements that may appear at block scope: render as inline.
            case "a":
            case "em":
            case "i":
            case "strong":
            case "b":
            case "code":
            case "kbd":
            case "mark":
            case "sub":
            case "sup":
            case "span":
                WriteInlineNode(el);
                return;

            // Generic wrapper: descend into children at block scope.
            default:
                WriteBlockChildren(el);
                return;
        }
    }

    private void WriteHeading(IElement el, int level)
    {
        EnsureBlankLine();
        _out.Append('#', level);
        _out.Append(' ');
        WriteInlineChildren(el);
        EnsureBlankLine();
    }

    private void WriteList(IElement list, bool ordered)
    {
        EnsureBlankLine();
        int index = 1;
        foreach (var child in list.Children)
        {
            if (!string.Equals(child.TagName, "li", StringComparison.OrdinalIgnoreCase)) continue;
            var marker = ordered ? $"{index++}. " : "- ";
            _out.Append(marker);
            var inline = new StringBuilder();
            var sub = new DomMarkdownWalker { };
            sub._out.Append(' ', 0);
            sub.WriteInlineChildren(child);
            // Single-line items: collapse internal newlines. Lists with paragraphs/nested
            // lists fall out of strict GFM here; that's the trade for keeping the walker
            // single-pass and readable.
            var line = sub._out.ToString().Replace('\n', ' ').Trim();
            _out.Append(line);
            _out.Append('\n');
        }
        EnsureBlankLine();
    }

    private void WritePre(IElement pre)
    {
        EnsureBlankLine();
        var inner = pre.QuerySelector("code");
        var lang = "";
        if (inner is not null)
        {
            foreach (var token in (inner.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("language-", StringComparison.OrdinalIgnoreCase))
                {
                    lang = token["language-".Length..];
                    break;
                }
            }
        }
        var body = (inner ?? pre).TextContent;
        _out.Append("```");
        _out.Append(lang);
        _out.Append('\n');
        _out.Append(body.TrimEnd());
        _out.Append('\n');
        _out.Append("```");
        EnsureBlankLine();
    }

    private void WriteBlockquote(IElement el)
    {
        EnsureBlankLine();
        var sub = new DomMarkdownWalker();
        sub.WriteBlockChildren(el);
        var body = sub._out.ToString().Trim();
        foreach (var line in body.Split('\n'))
        {
            _out.Append("> ");
            _out.Append(line);
            _out.Append('\n');
        }
        EnsureBlankLine();
    }

    // Table rendering follows the WHATWG "forming a table" algorithm to build a slot
    // grid that respects rowspan/colspan, then decides between a GFM pipe table and a
    // raw-HTML fallback. Most converters that try to coerce every <table> into pipes
    // produce garbage on Wikipedia infoboxes, MS Learn complex tables, and any table
    // with block content in cells. The going industry pattern (cmark-gfm, Joplin's
    // turndown-plugin-gfm, Pandoc, JohannesKaufmann/html-to-markdown) is: detect
    // complexity, emit raw HTML when the structure cannot survive the GFM constraint
    // (one header row, no rowspan/colspan, no block cell content). CommonMark passes
    // raw HTML through unchanged, so RAG/markdown viewers still render it.
    private void WriteTable(IElement table)
    {
        EnsureBlankLine();

        var theadRows = CollectRows(table.QuerySelector("thead"));
        var tbodyRows = new List<IElement>();
        var tbody = table.QuerySelector("tbody");
        if (tbody is not null) tbodyRows.AddRange(CollectRows(tbody));
        // Direct <tr> children of <table> (HTML5 lets you omit <tbody>).
        foreach (var child in table.Children)
        {
            if (string.Equals(child.TagName, "tr", StringComparison.OrdinalIgnoreCase))
            {
                tbodyRows.Add(child);
            }
        }
        // No <thead>? Use first row as header if all its cells are <th>.
        if (theadRows.Count == 0 && tbodyRows.Count > 0 && AllThCells(tbodyRows[0]))
        {
            theadRows.Add(tbodyRows[0]);
            tbodyRows.RemoveAt(0);
        }

        bool complex = ShouldFallBackToHtml(table, theadRows, tbodyRows);
        if (complex)
        {
            EmitRawTable(table);
            return;
        }
        if (theadRows.Count == 0 && tbodyRows.Count == 0) return;

        // Build the WHATWG slot grid for header + body rows. Track downward-growing
        // cells so a rowspan in row R fills (R, C) and blanks the slot in rows below.
        var allRows = new List<IElement>(theadRows.Count + tbodyRows.Count);
        allRows.AddRange(theadRows);
        allRows.AddRange(tbodyRows);
        var (grid, alignByCol) = BuildSlotGrid(allRows);
        if (grid.Count == 0 || grid[0].Count == 0) return;

        int headerRowCount = theadRows.Count == 0 ? 1 : theadRows.Count;
        // We already guaranteed a single header row via ShouldFallBackToHtml's multi-row
        // thead check; clamp defensively.
        if (headerRowCount > 1) headerRowCount = 1;

        // Caption renders as a bold paragraph above the table (Pandoc / Joplin convention).
        var caption = table.QuerySelector("caption");
        if (caption is not null)
        {
            var capText = caption.TextContent.Trim();
            if (capText.Length > 0)
            {
                _out.Append("**").Append(EscapeInline(capText)).Append("**");
                _out.Append('\n').Append('\n');
            }
        }

        int cols = grid[0].Count;

        void WriteRow(List<string> cells)
        {
            _out.Append('|');
            for (int i = 0; i < cols; i++)
            {
                _out.Append(' ');
                _out.Append(i < cells.Count ? cells[i] : "");
                _out.Append(" |");
            }
            _out.Append('\n');
        }

        // Header row.
        var header = grid[0];
        WriteRow(header);
        // Separator with alignment markers.
        _out.Append('|');
        for (int i = 0; i < cols; i++)
        {
            var a = i < alignByCol.Count ? alignByCol[i] : ColAlign.Default;
            _out.Append(a switch
            {
                ColAlign.Left => " :--- |",
                ColAlign.Center => " :---: |",
                ColAlign.Right => " ---: |",
                _ => " --- |"
            });
        }
        _out.Append('\n');
        for (int r = headerRowCount; r < grid.Count; r++)
        {
            WriteRow(grid[r]);
        }
        EnsureBlankLine();
    }

    private enum ColAlign { Default, Left, Center, Right }

    private static List<IElement> CollectRows(IElement? section)
    {
        var rows = new List<IElement>();
        if (section is null) return rows;
        foreach (var child in section.Children)
        {
            if (string.Equals(child.TagName, "tr", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(child);
            }
        }
        return rows;
    }

    private static bool AllThCells(IElement tr)
    {
        bool hadAny = false;
        foreach (var c in tr.Children)
        {
            var tag = c.TagName.ToLowerInvariant();
            if (tag == "td") return false;
            if (tag == "th") hadAny = true;
        }
        return hadAny;
    }

    // Joplin's tableShouldBeHtml is the gold standard: any structural feature GFM
    // cannot express triggers a raw-HTML fallback rather than a lossy projection.
    // We extend that with the "multi-row thead" case (GFM allows exactly one header
    // row, cmark-gfm refuses anything else) and the "no rows at all" case.
    private bool ShouldFallBackToHtml(IElement table, List<IElement> theadRows, List<IElement> tbodyRows)
    {
        if (theadRows.Count > 1) return true;
        if (theadRows.Count == 0 && tbodyRows.Count == 0) return false;

        // Nested table at any depth is always complex.
        if (table.QuerySelectorAll("table").Length > 1) return true;

        var allRows = new List<IElement>(theadRows.Count + tbodyRows.Count);
        allRows.AddRange(theadRows);
        allRows.AddRange(tbodyRows);
        foreach (var tr in allRows)
        {
            foreach (var cell in tr.Children)
            {
                var ct = cell.TagName.ToLowerInvariant();
                if (ct != "td" && ct != "th") continue;
                if (CellHasBlockContent(cell)) return true;
                // rowspan > 1 ON a cell whose content is text-only is okay (we anchor it
                // in the first row and blank the rest). It's the COMBINATION with block
                // content that defeats GFM, and that's already caught above.
            }
        }
        return false;
    }

    private static bool CellHasBlockContent(IElement cell)
    {
        if (cell.QuerySelector("h1, h2, h3, h4, h5, h6, ul, ol, pre, blockquote, hr, table") is not null)
            return true;
        // Multiple <p> children also count: a single <p> wrapping the cell text is harmless
        // (we strip the wrapper); two or more produces a paragraph break GFM can't express.
        int pCount = 0;
        foreach (var _ in cell.QuerySelectorAll("p")) { if (++pCount >= 2) return true; }
        return false;
    }

    private void EmitRawTable(IElement table)
    {
        // CommonMark §4.6 passes raw HTML blocks through unchanged when separated by
        // blank lines. We emit the table's OuterHtml verbatim. Downstream RAG ingest
        // and markdown viewers (cmark-gfm, github.com, Obsidian, etc.) render it.
        var html = (table.OuterHtml ?? string.Empty).Trim();
        if (html.Length == 0) return;
        _out.Append(html).Append('\n');
        EnsureBlankLine();
    }

    private (List<List<string>> Grid, List<ColAlign> AlignByCol) BuildSlotGrid(List<IElement> rows)
    {
        // Apply the WHATWG "forming a table" algorithm. cells[y][x] is the rendered
        // content for slot (x, y). When a cell has rowspan>1 we fill in the
        // grow-downward positions with empty strings (anchoring the content in the
        // first row). colspan>1 repeats the content across columns to preserve visual
        // fidelity in viewers that lack merged-cell support.
        const int MaxSpan = 1000; // WHATWG cap.
        var grid = new List<List<string>>();
        var alignVotes = new List<Dictionary<ColAlign, int>>();

        ColAlign[] pendingAlign = Array.Empty<ColAlign>();
        bool[] pendingOccupied = Array.Empty<bool>();
        // pendingOccupied[c] = true means slot (currentRow, c) is already taken by a
        // rowspan from a previous row.
        // A side-grid tracks remaining rowspan per (col) for the running rows.

        var rowspanRemaining = new List<int>(); // per column
        var rowspanContent = new List<string>(); // per column (filled while remaining > 0)

        foreach (var tr in rows)
        {
            // Snapshot the carry-over occupancy for this row.
            var currentRow = new List<string>();
            int writeCol = 0;

            // First, fill any column slots that a previous row's rowspan continues to cover.
            void FillCarry()
            {
                while (writeCol < rowspanRemaining.Count && rowspanRemaining[writeCol] > 0)
                {
                    while (currentRow.Count <= writeCol) currentRow.Add("");
                    currentRow[writeCol] = ""; // anchored above; leave blank below
                    rowspanRemaining[writeCol]--;
                    writeCol++;
                }
            }

            foreach (var cell in tr.Children)
            {
                var ct = cell.TagName.ToLowerInvariant();
                if (ct != "td" && ct != "th") continue;

                FillCarry();

                int colspan = Math.Clamp(ParseSpan(cell, "colspan", 1), 1, MaxSpan);
                int rowspan = Math.Clamp(ParseSpan(cell, "rowspan", 1), 1, MaxSpan);

                var content = RenderCellInline(cell);
                var colAlign = DetectCellAlign(cell);

                for (int s = 0; s < colspan; s++)
                {
                    while (currentRow.Count <= writeCol) currentRow.Add("");
                    currentRow[writeCol] = content;

                    while (rowspanRemaining.Count <= writeCol)
                    {
                        rowspanRemaining.Add(0);
                        rowspanContent.Add("");
                    }
                    if (rowspan > 1)
                    {
                        rowspanRemaining[writeCol] = rowspan - 1;
                        rowspanContent[writeCol] = content;
                    }

                    while (alignVotes.Count <= writeCol) alignVotes.Add(new Dictionary<ColAlign, int>());
                    if (colAlign != ColAlign.Default)
                    {
                        alignVotes[writeCol][colAlign] = alignVotes[writeCol].GetValueOrDefault(colAlign) + 1;
                    }

                    writeCol++;
                }
            }

            // Fill any trailing carry-over rowspan slots after the last explicit cell.
            FillCarry();
            grid.Add(currentRow);
        }

        // Normalise: every row to the same width using the widest row as canonical.
        int width = 0;
        foreach (var r in grid) width = Math.Max(width, r.Count);
        foreach (var r in grid)
        {
            while (r.Count < width) r.Add("");
        }

        // Majority vote per column (ties → default).
        var alignByCol = new List<ColAlign>();
        for (int c = 0; c < width; c++)
        {
            if (c >= alignVotes.Count || alignVotes[c].Count == 0)
            {
                alignByCol.Add(ColAlign.Default);
                continue;
            }
            ColAlign winner = ColAlign.Default;
            int best = 0;
            bool tie = false;
            foreach (var kv in alignVotes[c])
            {
                if (kv.Value > best) { winner = kv.Key; best = kv.Value; tie = false; }
                else if (kv.Value == best) tie = true;
            }
            alignByCol.Add(tie ? ColAlign.Default : winner);
        }
        return (grid, alignByCol);
    }

    private static int ParseSpan(IElement cell, string attr, int fallback)
    {
        var raw = cell.GetAttribute(attr);
        if (string.IsNullOrEmpty(raw)) return fallback;
        // HTML5 says rowspan=0 grows to end of row group; we treat it as fallback (cap)
        // because GFM cannot represent "grow to end" cleanly and the table-shouldbeHtml
        // path catches the heavy cases.
        return int.TryParse(raw, out var v) && v > 0 ? v : fallback;
    }

    private static ColAlign DetectCellAlign(IElement cell)
    {
        var align = (cell.GetAttribute("align") ?? "").ToLowerInvariant();
        var fromAttr = align switch
        {
            "left" => ColAlign.Left,
            "center" => ColAlign.Center,
            "right" => ColAlign.Right,
            _ => ColAlign.Default
        };
        if (fromAttr != ColAlign.Default) return fromAttr;
        var style = (cell.GetAttribute("style") ?? "").ToLowerInvariant();
        if (style.Contains("text-align: left") || style.Contains("text-align:left")) return ColAlign.Left;
        if (style.Contains("text-align: center") || style.Contains("text-align:center")) return ColAlign.Center;
        if (style.Contains("text-align: right") || style.Contains("text-align:right")) return ColAlign.Right;
        return ColAlign.Default;
    }

    private string RenderCellInline(IElement cell)
    {
        var sub = new DomMarkdownWalker();
        sub.WriteInlineChildren(cell);
        // GFM cells cannot hold real newlines; replace with <br> (the one block-construct
        // GFM permits inside a cell per the GFM extension spec) and escape pipes. Strip
        // the two-space prefix that the inline <br> emits as a hard-break: in a cell,
        // we want `a<br>b`, not `a  <br>b`. Trailing whitespace on each segment also
        // collapses to keep the cell content tight.
        var raw = sub._out.ToString().Replace("\r\n", "\n");
        var sbCell = new StringBuilder(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c == '\n')
            {
                // Walk back through trailing whitespace on the current line.
                while (sbCell.Length > 0 && (sbCell[^1] == ' ' || sbCell[^1] == '\t')) sbCell.Length--;
                sbCell.Append("<br>");
                i++;
                continue;
            }
            if (c == '|') { sbCell.Append("\\|"); i++; continue; }
            sbCell.Append(c);
            i++;
        }
        return sbCell.ToString().Trim();
    }

    private void WriteFigure(IElement fig)
    {
        EnsureBlankLine();
        var img = fig.QuerySelector("img");
        if (img is not null) WriteInlineImage(img);
        var cap = fig.QuerySelector("figcaption");
        if (cap is not null)
        {
            _out.Append('\n');
            WriteInlineChildren(cap);
        }
        EnsureBlankLine();
    }

    private void WriteInlineImage(IElement img)
    {
        var src = img.GetAttribute("src") ?? "";
        if (src.Length == 0) return;
        var alt = img.GetAttribute("alt") ?? "";
        _out.Append("![").Append(EscapeBracket(alt)).Append("](").Append(src).Append(')');
    }

    // ----- Inline rendering -----

    private void WriteInlineChildren(IElement el)
    {
        foreach (var child in el.ChildNodes) WriteInlineNode(child);
    }

    private void WriteInlineNode(INode node)
    {
        if (node is IText t)
        {
            _out.Append(EscapeInline(t.TextContent));
            return;
        }
        if (node is not IElement el) return;
        var tag = el.TagName.ToLowerInvariant();
        switch (tag)
        {
            case "br":
                _out.Append("  \n");
                return;
            case "a":
                var href = el.GetAttribute("href") ?? "";
                if (href.Length == 0) { WriteInlineChildren(el); return; }
                _out.Append('[');
                WriteInlineChildren(el);
                _out.Append("](").Append(href).Append(')');
                return;
            case "em":
            case "i":
                _out.Append('*');
                WriteInlineChildren(el);
                _out.Append('*');
                return;
            case "strong":
            case "b":
                _out.Append("**");
                WriteInlineChildren(el);
                _out.Append("**");
                return;
            case "code":
                _out.Append('`');
                _out.Append(el.TextContent);
                _out.Append('`');
                return;
            case "img":
                WriteInlineImage(el);
                return;
            case "script":
            case "style":
            case "noscript":
                return;
            default:
                WriteInlineChildren(el);
                return;
        }
    }

    private void EnsureBlankLine()
    {
        if (_out.Length == 0) return;
        // Trim trailing spaces on the current line, then ensure exactly two newlines.
        while (_out.Length > 0 && (_out[^1] == ' ' || _out[^1] == '\t')) _out.Length--;
        int trailingNewlines = 0;
        for (int i = _out.Length - 1; i >= 0 && _out[i] == '\n'; i--) trailingNewlines++;
        for (int i = trailingNewlines; i < 2; i++) _out.Append('\n');
    }

    private static string EscapeInline(string s)
    {
        // Conservative: don't escape underscores/asterisks/backticks here. They occur inside
        // running prose more often than they introduce real markdown syntax, and the spec
        // explicitly excludes smart-quote / em-dash normalisation as out of scope.
        // Only HTML-significant whitespace collapse is applied: runs of internal whitespace
        // collapse to single space; leading/trailing whitespace is preserved so that adjacent
        // inline elements (e.g. " <strong>word</strong>") keep their gap.
        if (s.Length == 0) return s;
        var sb = new StringBuilder(s.Length);
        bool prevWs = false;
        foreach (var c in s)
        {
            if (c == '\n' || c == '\r' || c == '\t')
            {
                if (!prevWs) { sb.Append(' '); prevWs = true; }
                continue;
            }
            if (c == ' ')
            {
                if (!prevWs) { sb.Append(' '); prevWs = true; }
                continue;
            }
            sb.Append(c);
            prevWs = false;
        }
        return sb.ToString();
    }

    private static string EscapeBracket(string s) => s.Replace("[", "\\[").Replace("]", "\\]");
}
