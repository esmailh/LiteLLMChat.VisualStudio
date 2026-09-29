using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace LiteLLM.VisualStudio;
internal static class MarkdownRenderer
{
    /// <summary>Visual Studio's editor font stack: Cascadia ships with recent versions, Consolas with older ones.</summary>
    private static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas, Courier New");
    internal static FrameworkElement Render(string markdown)
    {
        var panel = new StackPanel(); var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n'); var text = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("```")) { Flush(panel, text); var language = lines[i].Substring(3).Trim(); var code = new System.Text.StringBuilder(); while (++i < lines.Length && !lines[i].StartsWith("```")) code.AppendLine(lines[i]); panel.Children.Add(CodeBlock(code.ToString().TrimEnd(), language)); }
            else text.AppendLine(lines[i]);
        }
        Flush(panel, text); return panel;
    }
    private static void Flush(Panel panel, System.Text.StringBuilder value)
    {
        if (value.Length == 0) return; var paragraph = new Paragraph { Margin = new Thickness(1, 2, 1, 2) };
        // Line by line rather than one string with newlines in it: a FlowDocument paragraph has no idea what a "\n"
        // means, so a hard break has to be a LineBreak element or every line would run into the next one.
        var lines = value.ToString().TrimEnd().Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            foreach (var part in Regex.Split(lines[i], "(`[^`]+`|\\*\\*[^*]+\\*\\*)"))
if (part.StartsWith("`") && part.EndsWith("`") && part.Length >= 2)
                    {
                        // SetResourceReference is the code equivalent of {DynamicResource}: it resolves the key against the
                        // shell's theme dictionary and re-resolves when Visual Studio changes theme. A captured brush would
                        // not — and the key has to be one of the shell's own tokens, because that is the only dictionary
                        // this can reach. A key of ours resolves to nothing, and a reference that resolves to nothing does
                        // not fall back to what it would have inherited; it leaves the property at WPF's default.
                        var run = new Run(part.Trim('`')) { FontFamily = MonoFont };
                        run.SetResourceReference(TextElement.BackgroundProperty, CommonControlsColors.TextBoxBackgroundBrushKey);
                        paragraph.Inlines.Add(run);
                    }
                else if (part.StartsWith("**") && part.EndsWith("**") && part.Length >= 4) paragraph.Inlines.Add(new Bold(new Run(part.Substring(2, part.Length - 4)))); else paragraph.Inlines.Add(new Run(part));
        }
        panel.Children.Add(Selectable(paragraph)); value.Clear();
    }
    private static FrameworkElement CodeBlock(string code, string language)
    {
        var copy = new Button { Content = "Copy code", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(6, 2, 6, 2), ToolTip = "Copy code to clipboard" }; copy.Click += (s, e) => Clipboard.SetText(code);
        var body = Selectable(new Paragraph(new Run(code) { FontFamily = MonoFont })); body.Margin = new Thickness(6);
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
