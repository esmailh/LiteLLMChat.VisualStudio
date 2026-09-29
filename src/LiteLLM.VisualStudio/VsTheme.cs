using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace LiteLLM.VisualStudio;
/// <summary>Makes the Visual Studio theme the single source of truth for every colour in the tool windows and dialogs.
///
/// The XAML names no colours of ours: it references Visual Studio's own brush tokens directly —
/// <c>{DynamicResource {x:Static ui:EnvironmentColors.ToolWindowBackgroundBrushKey}}</c> — so a theme change is the
/// shell swapping its own brushes and WPF re-resolving ours out of them. There is no handler in that path, no lag,
/// and nothing that can go stale.
///
/// What is left for this class is the short list of colours Visual Studio publishes no token for (the accent wash,
/// the selection highlight and the text on it, the error colour) and two safety nets: standing in for a token the
/// running shell does not answer, and adopting Visual Studio's own control styles where the shell publishes them.
///
/// This deliberately does not use <c>Color="{DynamicResource {x:Static ...ColorKey}}"</c>: the <c>...ColorKey</c>
/// members are not registered as resources, so that key never resolves, and a SolidColorBrush whose Color never
/// resolves is white — which is how the chat first ended up painting white text on a white page. The
/// <c>...BrushKey</c> members are the ones the shell registers.</summary>
internal static class VsTheme
{
    private const double MinimumContrast = 2.5;
    // Only reached when there is no theme to read — the XAML designer, or a test run. Inside Visual Studio the theme
    // service always answers. Dark mid-tones, so a fallback page still carries the resolved text colours.
    private static readonly Color NoPage = Color.FromRgb(0x1E, 0x1E, 0x1E);
    private static readonly Color NoSurface = Color.FromRgb(0x2D, 0x2D, 0x30);
    private static readonly Color NoRaised = Color.FromRgb(0x25, 0x25, 0x26);
    private static readonly Color NoBorder = Color.FromRgb(0x54, 0x54, 0x58);
    private static readonly Color NoAccent = Color.FromRgb(0x4F, 0x6B, 0xED);
    private static readonly Color NoDanger = Color.FromRgb(0xF4, 0x87, 0x71);
    private static readonly Color NoCode = Color.FromRgb(0x1B, 0x1B, 0x1C);
    private static readonly List<Root> Live = new List<Root>();
    // The brushes this class had to stand in for. Kept so the next pass can tell its own placeholder apart from a
    // brush the shell supplied, and take the placeholder back out once the shell is answering again.
    private static readonly HashSet<Brush> Placeholders = new HashSet<Brush>();
    // The tokens the shell did not answer, so the next pass knows which placeholders to take back out — and so
    // Describe() can report them instead of leaving the question to guesswork. Counted, never listed: a
    // ThemeResourceKey's own text is not its property name, so a list of them would be noise.
    private static readonly HashSet<ThemeResourceKey> Missing = new HashSet<ThemeResourceKey>();
    private static readonly HashSet<ThemeResourceKey> Seen = new HashSet<ThemeResourceKey>();
    private static bool _watching;

    /// <summary>The Visual Studio styles the shell publishes under a string key. Applying the shell's own style is a
    /// closer match than any template of ours, so it wins wherever it is there; the styles in ThemeResources.xaml and
    /// VsControlStyles.xaml stay as the answer for a shell that does not publish one.
    ///
    /// These keys cannot be confirmed to be registered without running inside Visual Studio, which is why this is an
    /// upgrade path rather than the mechanism — Describe() below reports which ones were actually found, in the
    /// LiteLLM output pane.</summary>
    private static readonly (object Key, Type Target)[] ShellStyles =
    {
        (VsResourceKeys.CheckBoxStyleKey, typeof(CheckBox)),
        (VsResourceKeys.ComboBoxStyleKey, typeof(ComboBox)),
        (VsResourceKeys.ComboBoxItemStyleKey, typeof(ComboBoxItem)),
        (VsResourceKeys.TextBoxStyleKey, typeof(TextBox)),
        (VsResourceKeys.ProgressBarStyleKey, typeof(ProgressBar)),
        (VsResourceKeys.ContextMenuStyleKey, typeof(ContextMenu))
    };

    /// <summary>Installs the theme into a control. Call after InitializeComponent, so the XAML being merged in is
    /// already there — the styles in it reference these keys by DynamicResource and pick the brushes up as soon as
    /// they appear.</summary>
    internal static void Apply(FrameworkElement root) => Attach(root, true);

    /// <summary>The same, for one of the two dialogs. On top of Apply it hands the window Visual Studio's own dialog
    /// styles, which is what makes the buttons, text boxes and check boxes inside it — the default button in
    /// particular — look like the shell's dialogs instead of like ours. Merged last, so the shell's styles win inside
    /// this window while the styles in XAML stay the answer for a shell that publishes none.
    ///
    /// The shell's tool-window control styles are deliberately not adopted here: an entry in a window's own dictionary
    /// is searched before its merged dictionaries, so adopting them would put our copy in front of the dialog styles
    /// that were just merged in.</summary>
    internal static void ApplyToDialog(Window dialog)
    {
        Attach(dialog, false);
        // Visual Studio ships its dialog styles behind an attached property rather than a call: setting it is what
        // makes the loader merge them in. The default button inside the window is the visible half of that — Visual
        // Studio paints it from its own accent pair, which no style of ours can imitate.
        try { ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(dialog, true); }
        catch { /* a shell without the dialog styles leaves the styles in XAML in charge, which are themed as well */ }
    }

    private static void Attach(FrameworkElement root, bool shellControlStyles)
    {
        if (root == null) return;
        Fill(root);
        // Almost no text element here sets a Foreground of its own — the bubbles, the header, the empty state — so
        // they all used to fall back to WPF's default black. That only read because the panels were light system
        // greys; against a genuinely dark theme it is black on black. Handing the colour down from the root is what
        // makes every one of them themed at once. Styles that want something else override it as usual.
        root.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        root.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        if (shellControlStyles) AdoptShellStyles(root);
        Live.Add(new Root(root, shellControlStyles));
        if (_watching) return;
        _watching = true;
        VSColorTheme.ThemeChanged += OnThemeChanged;
    }

    private static void OnThemeChanged(ThemeChangedEventArgs e)
    {
        for (var i = Live.Count - 1; i >= 0; i--)
        {
            if (!Live[i].Element.TryGetTarget(out var root)) { Live.RemoveAt(i); continue; }
            var shellStyles = Live[i].ShellStyles;   // read now: the lambda outlives this iteration's i
            // Brushes belong to the window that owns them; a theme change should arrive on that thread, but painting
            // from the wrong one takes Visual Studio down with the exception. The shell's dialog styles are already
            // merged into a dialog and reference the brushes by DynamicResource, so they need nothing here.
            if (root.Dispatcher.CheckAccess()) Refresh(root, shellStyles);
            else root.Dispatcher.BeginInvoke((Action)(() => Refresh(root, shellStyles)));
        }
    }

    private static void Refresh(FrameworkElement root, bool shellControlStyles)
    {
        Fill(root);
        if (shellControlStyles) AdoptShellStyles(root);
    }

    /// <summary>One themed element, and whether the shell's tool-window control styles apply to it.</summary>
    private sealed class Root
    {
        internal readonly WeakReference<FrameworkElement> Element;
        internal readonly bool ShellStyles;
        internal Root(FrameworkElement element, bool shellStyles) { Element = new WeakReference<FrameworkElement>(element); ShellStyles = shellStyles; }
    }

    /// <summary>The colours Visual Studio has no token for, derived here, plus a placeholder under every one of the
    /// shell's own tokens that the shell did not answer.</summary>
    private static void Fill(FrameworkElement root)
    {
        var page = Read(EnvironmentColors.ToolWindowBackgroundColorKey, NoPage);
        var surface = Read(EnvironmentColors.EnvironmentBackgroundColorKey, NoSurface);
        var raised = Read(CommonDocumentColors.ListItemBackgroundColorKey, NoRaised);
        var accent = Read(EnvironmentColors.AccentMediumColorKey, NoAccent);
        var code = Read(EnvironmentColors.ToolWindowCodeBlockBackgroundColorKey, NoCode);
        var text = Read(EnvironmentColors.ToolWindowTextColorKey, Colors.White);
        var muted = Read(CommonDocumentColors.SupportTextColorKey, Colors.Silver);
        var border = Read(CommonControlsColors.ButtonBorderColorKey, NoBorder);

        var target = root.Resources;
        // Derived, because the shell publishes no token for them. Text is picked against the surfaces it sits on,
        // and a theme whose accent is close to its own background gets a wash pushed away from it until it separates.
        target["DangerBrush"] = Frozen(Readable(Read(CommonDocumentColors.StatusBannerErrorTextColorKey, NoDanger), page, surface, raised));
        var wash = Wash(accent, page, surface, raised, code);
        target["SelectionBrush"] = Frozen(wash);
        // The glyphs under the wash have to survive it, and the system's own highlight text colour is white whatever
        // theme is running — which is unreadable on a light wash.
        target["SelectionTextBrush"] = Frozen(Readable(text, wash));

        // Every remaining colour is the shell's own. The values here are only ever used to stand in for a token the
        // shell does not answer — the XAML asks for the shell's key, not for these.
        var wanted = new Dictionary<ThemeResourceKey, Color>();
        void Role(Color colour, params ThemeResourceKey[] keys) { foreach (var key in keys) wanted[key] = colour; }

        Role(page,
            EnvironmentColors.ToolWindowBackgroundBrushKey, EnvironmentColors.ComboBoxBackgroundBrushKey,
            CommonControlsColors.ComboBoxBackgroundHoverBrushKey, CommonControlsColors.ComboBoxBackgroundFocusedBrushKey,
            CommonControlsColors.ComboBoxBackgroundPressedBrushKey, CommonControlsColors.TextBoxBackgroundBrushKey,
            EnvironmentColors.ScrollBarBackgroundBrushKey);
        Role(surface, EnvironmentColors.EnvironmentBackgroundBrushKey, EnvironmentColors.ScrollBarThumbBackgroundBrushKey);
        Role(raised,
            CommonDocumentColors.ListItemBackgroundBrushKey, CommonDocumentColors.ListItemBackgroundHoverBrushKey,
            CommonDocumentColors.ListItemBackgroundPressedBrushKey, CommonDocumentColors.ListItemBackgroundFocusedBrushKey,
            CommonDocumentColors.ListItemBackgroundSelectedBrushKey, CommonControlsColors.ComboBoxListBackgroundBrushKey,
            CommonControlsColors.ComboBoxListItemBackgroundHoverBrushKey, EnvironmentColors.ComboBoxItemMouseOverBackgroundBrushKey,
            CommonControlsColors.ButtonBrushKey, CommonControlsColors.ButtonHoverBrushKey,
            CommonControlsColors.ButtonPressedBrushKey, CommonControlsColors.ButtonDisabledBrushKey,
            CommonControlsColors.CheckBoxBackgroundBrushKey, CommonControlsColors.CheckBoxBackgroundHoverBrushKey,
            CommonControlsColors.CheckBoxBackgroundPressedBrushKey, CommonControlsColors.CheckBoxBackgroundFocusedBrushKey,
            CommonControlsColors.CheckBoxBackgroundDisabledBrushKey, EnvironmentColors.ToolTipBrushKey,
            ProgressBarColors.BackgroundBrushKey);
        Role(text,
            EnvironmentColors.ToolWindowTextBrushKey, CommonDocumentColors.ListItemTextBrushKey,
            CommonDocumentColors.ListItemTextHoverBrushKey, CommonDocumentColors.ListItemTextSelectedBrushKey,
            CommonControlsColors.ComboBoxTextBrushKey, CommonControlsColors.ComboBoxTextHoverBrushKey,
            CommonControlsColors.ComboBoxTextFocusedBrushKey, EnvironmentColors.ComboBoxItemTextBrushKey,
            EnvironmentColors.ComboBoxItemMouseOverTextBrushKey, CommonControlsColors.ComboBoxListItemTextBrushKey,
            CommonControlsColors.ComboBoxListItemTextHoverBrushKey, CommonControlsColors.ButtonTextBrushKey,
            CommonControlsColors.ButtonHoverTextBrushKey, CommonControlsColors.ButtonPressedTextBrushKey,
            CommonControlsColors.ButtonDisabledTextBrushKey, CommonControlsColors.CheckBoxTextBrushKey,
            CommonControlsColors.CheckBoxGlyphBrushKey, CommonControlsColors.TextBoxTextBrushKey);
        Role(muted,
            CommonDocumentColors.SupportTextBrushKey, CommonControlsColors.ComboBoxGlyphBrushKey,
            CommonControlsColors.ComboBoxGlyphHoverBrushKey, EnvironmentColors.ComboBoxItemTextInactiveBrushKey,
            CommonControlsColors.TextBoxWatermarkBrushKey, EnvironmentColors.CommandBarTextInactiveBrushKey);
        Role(border,
            CommonControlsColors.ButtonBorderBrushKey, CommonControlsColors.ButtonBorderHoverBrushKey,
            CommonControlsColors.ButtonBorderPressedBrushKey, CommonControlsColors.ButtonBorderDisabledBrushKey,
            CommonControlsColors.ComboBoxBorderBrushKey, CommonControlsColors.ComboBoxBorderHoverBrushKey,
            CommonControlsColors.ComboBoxListBorderBrushKey, CommonControlsColors.ComboBoxListItemBorderHoverBrushKey,
            EnvironmentColors.ComboBoxPopupBorderBrushKey, CommonControlsColors.ComboBoxSeparatorBrushKey,
            CommonControlsColors.TextBoxBorderBrushKey, CommonControlsColors.CheckBoxBorderBrushKey,
            CommonControlsColors.CheckBoxBorderHoverBrushKey, CommonControlsColors.CheckBoxBorderPressedBrushKey,
            CommonControlsColors.CheckBoxBorderDisabledBrushKey, CommonDocumentColors.ListItemBorderBrushKey,
            EnvironmentColors.ToolWindowBorderBrushKey, EnvironmentColors.ToolTipBorderBrushKey,
            EnvironmentColors.ScrollBarBorderBrushKey, EnvironmentColors.ScrollBarThumbBorderBrushKey);
        Role(accent,
            EnvironmentColors.AccentMediumBrushKey, CommonControlsColors.ButtonDefaultBrushKey,
            CommonControlsColors.TextBoxBorderFocusedBrushKey, CommonControlsColors.ComboBoxBorderFocusedBrushKey,
            EnvironmentColors.ComboBoxFocusBoxBrushKey, EnvironmentColors.ComboBoxFocusedBorderBrushKey,
            EnvironmentColors.ComboBoxItemMouseOverBorderBrushKey, CommonControlsColors.CheckBoxBorderFocusedBrushKey,
            CommonControlsColors.ButtonBorderFocusedBrushKey, ProgressBarColors.IndicatorFillBrushKey,
            EnvironmentColors.ScrollBarThumbMouseOverBackgroundBrushKey, EnvironmentColors.ScrollBarThumbPressedBackgroundBrushKey);

        // The default button's label is picked against the default button's own fill, because Visual Studio's pair for
        // it is one of the tokens a shell might not answer — and an accent-coloured face with a fixed white label is
        // exactly the unreadable combination this class exists to prevent.
        wanted[CommonControlsColors.ButtonDefaultTextBrushKey] = Readable(text, accent);
        wanted[EnvironmentColors.ToolWindowCodeBlockBackgroundBrushKey] = code;
        wanted[ProgressBarColors.IndicatorFillCriticalBrushKey] = Readable(Read(CommonDocumentColors.StatusBannerErrorTextColorKey, NoDanger), page, surface, raised);

        StandIn(root, wanted);

        // The stock WPF control templates paint from system brushes whatever theme is running. The styles in
        // VsControlStyles.xaml and ThemeResources.xaml cover the controls this UI uses; these stay as the answer for
        // any stock template that slips past them, and are re-derived on every theme change so they cannot go stale.
        //
        // The menus are the case that needs them: the stock ContextMenu and MenuItem templates take the popup surface
        // and the highlight band straight from SystemColors, and Visual Studio publishes no menu brush of its own — so
        // the shell's list-row colours stand in for the band and its surfaces for the popup, rather than leaving the
        // band Windows-blue on a dark IDE.
        var hover = Read(CommonDocumentColors.ListItemBackgroundHoverColorKey, raised);
        var hoverText = Read(CommonDocumentColors.ListItemTextHoverColorKey, text);
        target[SystemColors.WindowBrushKey] = Frozen(raised);
        target[SystemColors.WindowFrameBrushKey] = Frozen(border);
        target[SystemColors.WindowTextBrushKey] = Frozen(text);
        target[SystemColors.ControlBrushKey] = Frozen(raised);
        target[SystemColors.ControlTextBrushKey] = Frozen(text);
        target[SystemColors.MenuBrushKey] = Frozen(raised);
        target[SystemColors.MenuTextBrushKey] = Frozen(text);
        target[SystemColors.MenuBarBrushKey] = Frozen(surface);
        target[SystemColors.HighlightBrushKey] = Frozen(hover);
        target[SystemColors.HighlightTextBrushKey] = Frozen(hoverText);
        target[SystemColors.InactiveSelectionHighlightBrushKey] = Frozen(hover);
        target[SystemColors.InactiveSelectionHighlightTextBrushKey] = Frozen(hoverText);
        target[SystemColors.GrayTextBrushKey] = Frozen(muted);
    }

    /// <summary>Fills in a shell token the running shell does not answer, and steps back out of the way when it does.
    ///
    /// This control's own dictionary sits in front of the shell's in the lookup chain, so an entry here would win for
    /// good and freeze the theme — the placeholder is therefore only ever written for a key nothing else answers, and
    /// withdrawn as soon as something does. A key that resolves to nothing is what leaves a brush at WPF's white
    /// default, which is the failure this guards against.</summary>
    private static void StandIn(FrameworkElement root, Dictionary<ThemeResourceKey, Color> wanted)
    {
        foreach (var pair in wanted)
        {
            Seen.Add(pair.Key);
            var found = root.TryFindResource(pair.Key) as Brush;
            if (found != null && !Placeholders.Contains(found))
            {
                if (root.Resources.Contains(pair.Key)) root.Resources.Remove(pair.Key);   // the shell answers: keep out of its way
                Missing.Remove(pair.Key);
                continue;
            }
            if (found is SolidColorBrush solid && solid.Color == pair.Value) continue;    // our placeholder, still current
            if (found != null) Placeholders.Remove(found);
            var brush = Frozen(pair.Value);
            Placeholders.Add(brush);
            Missing.Add(pair.Key);
            root.Resources[pair.Key] = brush;
        }
    }

    /// <summary>Hands the control the shell's own style for a control type, where the shell publishes one. Wrapped
    /// because a shell that does not know the key should leave the styles in XAML in charge, not fail the load.</summary>
    private static void AdoptShellStyles(FrameworkElement root)
    {
        var application = Application.Current;
        foreach (var (key, type) in ShellStyles)
        {
            try
            {
                if (application != null && application.TryFindResource(key) is Style style) root.Resources[type] = style;
            }
            catch { /* an unknown style key is not worth a broken tool window */ }
        }
    }

    /// <summary>What the running shell actually answered, for the LiteLLM output pane. Whether Visual Studio registers
    /// its style keys cannot be known from outside the process, so the tool window reports it instead of assuming: a
    /// name under "applied" is the shell's own style really standing in for one of ours, and "0 stood in for" is the
    /// shell owning every colour with nothing of ours left in the chain.</summary>
    internal static string Describe()
    {
        var application = Application.Current;
        var found = new List<string>();
        var missing = new List<string>();
        // The keys are typed object by the shell, but what they hold is the name — which is what makes this line
        // readable rather than a list of type names.
        foreach (var (key, type) in ShellStyles) (application != null && application.TryFindResource(key) is Style ? found : missing).Add(Convert.ToString(key));
        return "Visual Studio control styles — applied: " + (found.Count == 0 ? "none" : string.Join(", ", found))
             + "; not published here: " + (missing.Count == 0 ? "none" : string.Join(", ", missing))
             + ". Theme tokens Visual Studio did not answer, stood in for here: " + Missing.Count + " of " + Seen.Count + ".";
    }

    /// <summary>A transparent answer counts as no answer: it would paint the same invisible text the default white does.</summary>
    private static Color Read(ThemeResourceKey key, Color fallback)
    {
        try
        {
            var themed = VSColorTheme.GetThemedColor(key);   // System.Drawing.Color — Visual Studio reports theme colours as Win32 RGB
            return themed.A == 0 ? fallback : Color.FromArgb(themed.A, themed.R, themed.G, themed.B);
        }
        catch { return fallback; }
    }

    /// <summary>Text is picked against the surfaces it sits on. If a theme ever hands back a colour too close to the
    /// page or a card, plain black or plain white — whichever survives both — is used instead of unreadable text.</summary>
    private static Color Readable(Color text, params Color[] surfaces)
    {
        if (Worst(text, surfaces) >= MinimumContrast) return text;
        return Worst(Colors.Black, surfaces) >= Worst(Colors.White, surfaces) ? Colors.Black : Colors.White;
    }
    /// <summary>A highlight that keeps the accent's hue but is pulled toward whichever end of the scale the surfaces are
    /// not, until it stands off every one of them. The step is small on purpose: the accent is left alone in the themes
    /// where it already reads, and only moved as far as it has to be.</summary>
    private static Color Wash(Color accent, params Color[] surfaces)
    {
        var away = Worst(Colors.Black, surfaces) >= Worst(Colors.White, surfaces) ? Colors.Black : Colors.White;
        for (var amount = 0.0; amount < 0.7; amount += 0.1)
        {
            var candidate = Blend(accent, away, amount);
            if (Worst(candidate, surfaces) >= MinimumContrast) return candidate;
        }
        return away;
    }
    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount), (byte)(from.G + (to.G - from.G) * amount), (byte)(from.B + (to.B - from.B) * amount));
    private static double Worst(Color text, Color[] surfaces) { var worst = double.MaxValue; foreach (var surface in surfaces) worst = Math.Min(worst, Contrast(text, surface)); return worst; }
    private static double Contrast(Color a, Color b) { var x = Luminance(a); var y = Luminance(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05); }
    private static double Luminance(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
    private static double Channel(byte value) { var s = value / 255.0; return s <= .03928 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
    private static SolidColorBrush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
}
