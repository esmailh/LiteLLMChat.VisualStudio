using System.Windows;

namespace LiteLLM.VisualStudio;

/// <summary>Picks a reading direction from the script a piece of text is actually written in.</summary>
internal static class TextDirection
{
    internal static FlowDirection For(string? text) =>
        IsRightToLeft(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    internal static bool IsRightToLeft(string? text)
    {
        if (text == null || text.Length == 0) return false;
        var rtl = 0; var ltr = 0;
        foreach (var c in text)
            if (IsRightToLeftLetter(c)) rtl++;
            else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) ltr++;
        return rtl > 0 && rtl >= ltr;   // a tie means the sentence is carried by its RTL words, e.g. "LiteLLM را باز کن"
    }

    private static bool IsRightToLeftLetter(char c)
    {
        if (c >= '٠' && c <= '٩') return false;   // Arabic-Indic digits
        if (c >= '۰' && c <= '۹') return false;   // Persian digits ۰-۹
        return (c >= '֐' && c <= '׿') ||          // Hebrew
               (c >= '؀' && c <= 'ۿ') ||          // Arabic, Persian
               (c >= '܀' && c <= 'ݏ') ||          // Syriac
               (c >= 'ݐ' && c <= 'ݿ') ||          // Arabic Supplement
               (c >= 'ހ' && c <= '޿') ||          // Thaana
               (c >= 'ࢠ' && c <= 'ࣿ') ||          // Arabic Extended-A
               (c >= 'יִ' && c <= '﷿') ||          // Hebrew / Arabic presentation forms
               (c >= 'ﹰ' && c <= '﻿');            // Arabic presentation forms-B
    }
}
