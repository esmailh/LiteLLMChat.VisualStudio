using System;
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
    internal static FrameworkElement Render(string markdown, Action<FileReference>? openFile = null)
    {
        var panel = new StackPanel(); var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n'); var text = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("```")) { Flush(panel, text, openFile); var language = lines[i].Substring(3).Trim(); var code = new System.Text.StringBuilder(); while (++i < lines.Length && !lines[i].StartsWith("```")) code.AppendLine(lines[i]); panel.Children.Add(CodeBlock(code.ToString().TrimEnd(), language)); }
            else text.AppendLine(lines[i]);
        }
        Flush(panel, text, openFile); return panel;
    }
    private static void Flush(Panel panel, System.Text.StringBuilder value, Action<FileReference>? openFile)
    {
        if (value.Length == 0) return;
        var content = value.ToString().TrimEnd();
        var paragraph = new Paragraph { Margin = new Thickness(1, 2, 1, 2), FlowDirection = TextDirection.For(content) };
        // Line by line rather than one string with newlines in it: a FlowDocument paragraph has no idea what a "\n"
        // means, so a hard break has to be a LineBreak element or every line would run into the next one.
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            foreach (var part in Regex.Split(lines[i], "(`[^`]+`|\\*\\*[^*]+\\*\\*|\\[[^\\]]+\\]\\([^)]+\\))"))
                if (part.StartsWith("`") && part.EndsWith("`") && part.Length >= 2)
                    AddReferences(paragraph.Inlines, part.Substring(1, part.Length - 2), openFile, true);
                else if (part.StartsWith("**") && part.EndsWith("**") && part.Length >= 4)
                    paragraph.Inlines.Add(new Bold(new Run(part.Substring(2, part.Length - 4))));
                else
                {
                    var link = Regex.Match(part, @"^\[([^\]]+)\]\(([^)]+)\)$");
                    FileReference target;
                    if (link.Success && FileReferenceParser.TryParse(link.Groups[2].Value, out target) && openFile != null)
                        paragraph.Inlines.Add(CreateLink(link.Groups[1].Value, target, openFile, false));
                    else AddReferences(paragraph.Inlines, part, openFile, false);
                }
        }
        panel.Children.Add(Selectable(paragraph)); value.Clear();
    }
    private static void AddReferences(InlineCollection inlines, string text, Action<FileReference>? openFile, bool code)
    {
        var matches = FileReferenceParser.Find(text); var offset = 0;
        foreach (var match in matches)
        {
            if (match.Index > offset) inlines.Add(StyledRun(text.Substring(offset, match.Index - offset), code));
            inlines.Add(openFile == null ? (Inline)StyledRun(text.Substring(match.Index, match.Length), code) : CreateLink(text.Substring(match.Index, match.Length), match.Reference, openFile, code));
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
        var hyperlink = new Hyperlink(StyledRun(text, code)) { ToolTip = "Open " + reference.Path };
        // A displayed path is inherently LTR even when its containing sentence is RTL. A
        // human-readable Markdown label keeps the paragraph's direction.
        FileReference displayedReference;
        if (code || FileReferenceParser.TryParse(text, out displayedReference))
            hyperlink.FlowDirection = FlowDirection.LeftToRight;
        hyperlink.Click += (s, e) => { e.Handled = true; openFile(reference); };
        return hyperlink;
    }
    private static FrameworkElement CodeBlock(string code, string language)
    {
        var copy = new Button { Content = "Copy code", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(6, 2, 6, 2), ToolTip = "Copy code to clipboard" }; copy.Click += (s, e) => Clipboard.SetText(code);
        var body = Selectable(new Paragraph(new Run(code) { FontFamily = MonoFont }));
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
    /// Each block gets its own box, so a selection cannot run across a paragraph boundary; the message's own right-click
    /// menu ("Copy message", "Retry response") is what covers the whole reply.</summary>
    private static RichTextBox Selectable(Block block)
    {
        var view = new RichTextBox
        {
            IsReadOnly = true,
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
            Document = new FlowDocument(block) { PagePadding = new Thickness(0) }
        };
        // The block determines its direction from its text. Code blocks override this to LTR.
        view.FlowDirection = block.FlowDirection;
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
