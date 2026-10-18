using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using LiteLLM.Core;
using Microsoft.VisualStudio.PlatformUI;

namespace LiteLLM.VisualStudio;
internal static class MarkdownRenderer
{
    /// <summary>Visual Studio's editor font stack: Cascadia ships with recent versions, Consolas with older ones.</summary>
    private static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas, Courier New");
    /// <param name="resolveSymbol">Turns a name the reply mentioned into the declaration this solution has for it,
    /// or null when it has none. Nothing is linked unless it resolves, so a word that only looks like a symbol stays
    /// ordinary text.</param>
    internal static FrameworkElement Render(string markdown, Action<FileReference>? openFile = null, Func<string, SymbolLocation?>? resolveSymbol = null, Action<SymbolLocation>? openSymbol = null)
    {
        var panel = new StackPanel(); var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n'); var text = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("```")) { Flush(panel, text, openFile, resolveSymbol, openSymbol); var language = lines[i].Substring(3).Trim(); var code = new System.Text.StringBuilder(); while (++i < lines.Length && !lines[i].StartsWith("```")) code.AppendLine(lines[i]); panel.Children.Add(CodeBlock(code.ToString().TrimEnd(), language)); }
            else text.AppendLine(lines[i]);
        }
        Flush(panel, text, openFile, resolveSymbol, openSymbol); return panel;
    }
    private static void Flush(Panel panel, System.Text.StringBuilder value, Action<FileReference>? openFile, Func<string, SymbolLocation?>? resolveSymbol, Action<SymbolLocation>? openSymbol)
    {
        if (value.Length == 0) return;
        var content = value.ToString().TrimEnd();
        var lines = content.Split('\n');
        var blocks = new List<Block>();
        Paragraph? paragraph = null;
        FlowDirection? direction = null;
        // One paragraph has one base direction for every line in it. A line that switches language therefore starts
        // its own paragraph: an English-only line — a path, a file name, a sentence copied from code — stays at the
        // left of the bubble instead of following the Persian text around it to the right. A blank line keeps the
        // direction of the line before it so it does not split a block in two by itself.
        for (var i = 0; i < lines.Length; i++)
        {
            var lineDirection = lines[i].Trim().Length == 0 && direction != null ? direction.Value : TextDirection.For(lines[i]);
            if (paragraph == null || lineDirection != direction)
            {
                paragraph = new Paragraph
                {
                    // The outer margins belong to the first and last paragraph only; consecutive blocks have to sit
                    // as close together as the LineBreaks they replace did.
                    Margin = new Thickness(1, blocks.Count == 0 ? 2 : 0, 1, 0),
                    FlowDirection = lineDirection
                };
                blocks.Add(paragraph);
                direction = lineDirection;
            }
            else paragraph.Inlines.Add(new LineBreak());
            AddLine(paragraph, lines[i], openFile, resolveSymbol, openSymbol);
        }
        if (paragraph != null) paragraph.Margin = new Thickness(1, paragraph.Margin.Top, 1, 2);
        panel.Children.Add(Selectable(blocks)); value.Clear();
    }

    /// <summary>Adds one line's bold, inline code and file links to the paragraph that owns its direction.</summary>
    private static void AddLine(Paragraph paragraph, string line, Action<FileReference>? openFile, Func<string, SymbolLocation?>? resolveSymbol, Action<SymbolLocation>? openSymbol)
    {
        foreach (var part in Regex.Split(line, "(`[^`]+`|\\*\\*[^*]+\\*\\*|\\[[^\\]]+\\]\\([^)]+\\))"))
            if (part.StartsWith("`") && part.EndsWith("`") && part.Length >= 2)
                AddReferences(paragraph.Inlines, part.Substring(1, part.Length - 2), openFile, resolveSymbol, openSymbol, true);
            else if (part.StartsWith("**") && part.EndsWith("**") && part.Length >= 4)
                paragraph.Inlines.Add(new Bold(new Run(part.Substring(2, part.Length - 4))));
            else
            {
                var link = Regex.Match(part, @"^\[([^\]]+)\]\(([^)]+)\)$");
                FileReference target;
                if (link.Success && FileReferenceParser.TryParse(link.Groups[2].Value, out target) && openFile != null)
                    paragraph.Inlines.Add(CreateLink(link.Groups[1].Value, target, openFile, false));
                else AddReferences(paragraph.Inlines, part, openFile, resolveSymbol, openSymbol, false);
            }
    }
    private static void AddReferences(InlineCollection inlines, string text, Action<FileReference>? openFile, Func<string, SymbolLocation?>? resolveSymbol, Action<SymbolLocation>? openSymbol, bool code)
    {
        var matches = FileReferenceParser.Find(text); var offset = 0;
        foreach (var match in matches)
        {
            if (match.Index > offset) AddSymbols(inlines, text.Substring(offset, match.Index - offset), resolveSymbol, openSymbol, code);
            inlines.Add(openFile == null ? (Inline)StyledRun(text.Substring(match.Index, match.Length), code) : CreateLink(text.Substring(match.Index, match.Length), match.Reference, openFile, code));
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) AddSymbols(inlines, text.Substring(offset), resolveSymbol, openSymbol, code);
    }
    private static void AddSymbols(InlineCollection inlines, string text, Func<string, SymbolLocation?>? resolveSymbol, Action<SymbolLocation>? openSymbol, bool code)
    {
        if (text.Length == 0) return;
        if (resolveSymbol == null || openSymbol == null) { inlines.Add(StyledRun(text, code)); return; }
        var matches = SymbolReferenceParser.Find(text, code); var offset = 0;
        foreach (var match in matches)
        {
            var location = resolveSymbol(match.Name);
            // A candidate the solution does not declare stays text. The offset is left where it was so the
            // unresolved name is not lost when a later candidate does resolve.
            if (location == null) continue;
            if (match.Index > offset) inlines.Add(StyledRun(text.Substring(offset, match.Index - offset), code));
            inlines.Add(CreateSymbolLink(text.Substring(match.Index, match.Length), location, openSymbol, code));
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) inlines.Add(StyledRun(text.Substring(offset), code));
    }
    private static Run StyledRun(string text, bool code)
    {
        var run = new Run(text); if (!code) return run;
        // Code remains readable when embedded in an RTL Persian paragraph.
        run.FlowDirection = FlowDirection.LeftToRight;
        run.FontFamily = MonoFont;
        run.SetResourceReference(TextElement.BackgroundProperty, CommonControlsColors.TextBoxBackgroundBrushKey);
        return run;
    }
    private static Hyperlink CreateLink(string text, FileReference reference, Action<FileReference> openFile, bool code)
    {
        var hyperlink = new Hyperlink(StyledRun(text, code)) { ToolTip = "Open " + reference.Path, Cursor = System.Windows.Input.Cursors.Hand };
        // A displayed path is inherently LTR even when its containing sentence is RTL. A
        // human-readable Markdown label keeps the paragraph's direction.
        FileReference displayedReference;
        if (code || FileReferenceParser.TryParse(text, out displayedReference))
            hyperlink.FlowDirection = FlowDirection.LeftToRight;
        hyperlink.Click += (s, e) => { e.Handled = true; openFile(reference); };
        return hyperlink;
    }
    /// <summary>A class or method named in the reply, linked to the line that declares it. Unlike a file link there is
    /// no display form to preserve — the reply's own spelling is what the user sees — and the declaration's location
    /// is what the tooltip adds, so the link can be judged before it is clicked.</summary>
    private static Hyperlink CreateSymbolLink(string text, SymbolLocation location, Action<SymbolLocation> openSymbol, bool code)
    {
        var hyperlink = new Hyperlink(StyledRun(text, code))
        {
            ToolTip = "Go to " + location.Symbol + " · " + System.IO.Path.GetFileName(location.Path) + ":" + location.Line,
            Cursor = System.Windows.Input.Cursors.Hand,
            // The same rule as a path: a code identifier keeps its LTR shape inside an RTL sentence.
            FlowDirection = FlowDirection.LeftToRight
        };
        hyperlink.Click += (s, e) => { e.Handled = true; openSymbol(location); };
        return hyperlink;
    }
    private static FrameworkElement CodeBlock(string code, string language)
    {
        var copy = new Button { Content = "Copy code", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(6, 2, 6, 2), ToolTip = "Copy code to clipboard" }; copy.Click += (s, e) => Clipboard.SetText(code);
        // A code block is LTR and left-aligned whatever the answer around it is, including when the snippet itself
        // contains a Persian comment: the direction is the block's, not the sentence's.
        var body = Selectable(new Paragraph(new Run(code) { FontFamily = MonoFont })
        {
            FlowDirection = FlowDirection.LeftToRight,
            TextAlignment = TextAlignment.Left
        });
        body.FlowDirection = FlowDirection.LeftToRight;
        body.Margin = new Thickness(6);
        var dock = new DockPanel(); DockPanel.SetDock(copy, Dock.Top); dock.Children.Add(copy); dock.Children.Add(body);
        var surface = new Border { Child = dock, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 5, 0, 5), FlowDirection = FlowDirection.LeftToRight };
        // Visual Studio's own code-block surface, so a block reads as a block on every theme instead of as one more
        // paragraph of the answer.
        surface.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowCodeBlockBackgroundBrushKey);
        return surface;
    }

    /// <summary>A read-only editor holding one block of the reply.
    ///
    /// A TextBlock renders the same but cannot be selected at all — WPF has no IsTextSelectionEnabled — which is why
    /// part of an answer could not be picked up and copied. A RichTextBox can be, it takes the same Runs (so bold and
    /// inline code survive), and it holds the theme foreground the way every other text element here does. The
    /// scrollbars are off rather than hidden so the box measures to its content instead of clipping it to one line.
    ///
    /// A flushed chunk shares one box even when its lines split into several direction-specific paragraphs, so a
    /// selection still runs across the whole chunk; the code blocks and the message's own right-click menu ("Copy
    /// message", "Retry response") are what cover the rest of the reply.</summary>
    private static RichTextBox Selectable(Block block) => Selectable(new[] { block });

    private static RichTextBox Selectable(IReadOnlyList<Block> blocks)
    {
        // One box per flushed chunk, however many direction-specific paragraphs it holds: a selection still runs
        // across the whole chunk, and a code block gets a box of its own.
        var document = new FlowDocument { PagePadding = new Thickness(0) };
        foreach (var block in blocks) document.Blocks.Add(block);
        var view = new RichTextBox
        {
            IsReadOnly = true,
            // A RichTextBox treats an embedded Hyperlink as inert unless this is set: the same click that should open
            // the file would otherwise only place the caret. The box is still read-only, so selecting and copying an
            // answer keep working; it is the links inside it that this line turns back on.
            IsDocumentEnabled = true,
            IsReadOnlyCaretVisible = false,
            IsTabStop = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            // Cleared so the editor's own Cut/Copy/Paste menu does not swallow the right-click: the bubble's menu is
            // the one carrying Copy message and Retry, and it is already themed.
            ContextMenu = null,
            SelectionOpacity = .6,
            // Without this WPF paints the highlight only while the box holds keyboard focus, so a selection vanished the
            // moment the user looked at it — which is exactly when they want to see what they are about to copy.
            IsInactiveSelectionHighlightEnabled = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Document = document
        };
        // A click gives the box keyboard focus, and WPF then asks the chat's ScrollViewer to bring the focused
        // element into view. The box is as tall as the whole reply, so that request scrolled the transcript to the
        // top of the message whenever a file or symbol link was clicked — the jump that made following a link feel
        // like losing one's place. The box measures to its content and never scrolls on its own, so the request is
        // answered here instead of being passed up; selection and Ctrl+C are untouched, they do not need scrolling.
        view.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler((s, e) => e.Handled = true), true);
        // Each block sets its own direction from its text; the box takes the first one only so an empty document
        // still has a defined direction. Code blocks override both to LTR.
        view.FlowDirection = blocks.Count > 0 ? blocks[0].FlowDirection : FlowDirection.LeftToRight;
        // The shell's own tool-window text colour, by its own key. This is what the answer itself is drawn in, so a key
        // the theme dictionary does not answer leaves every reply in WPF's default black — which is invisible on a dark
        // theme, and was.
        view.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        // The wash and the text over it come from the theme, resolved so the highlight stands off the page and the code
        // block rather than depending on an accent that may sit close to both.
        view.SetResourceReference(TextBoxBase.SelectionBrushProperty, "SelectionBrush");
        view.SetResourceReference(TextBoxBase.SelectionTextBrushProperty, "SelectionTextBrush");
        return view;
    }
}
