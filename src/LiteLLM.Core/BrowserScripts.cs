using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

/// <summary>The JavaScript the tool sends into the page, and the two shapes it has to recognise coming back.
///
/// It is written here, as text, because the alternative is a second browser-automation library inside a Visual Studio
/// process that cannot afford one. Everything that can be got wrong in C# — which element a target means, what a
/// result is allowed to be, where a body ends and an expression begins — is decided outside the string, so a test can
/// check it without a browser.
///
/// The strings themselves follow one rule: they answer with an object whose <c>ok</c> says whether the page did what
/// was asked, and whose <c>error</c> is a sentence the model can act on. A thrown exception would come back as a
/// DevTools protocol error about a script, which tells the model nothing about the page.</summary>
public static class BrowserScripts
{
    /// <summary>How much of a page's visible text one call brings back. A dashboard or a mailbox is otherwise the
    /// whole prompt, and the model needs the headline, not the page.</summary>
    public const int MaxTextChars = 4000;

    /// <summary>How much of a script's own answer is kept. A result is not a page: past a few thousand characters the
    /// model is reading a dump rather than a value.</summary>
    public const int MaxResultChars = 4000;

    /// <summary>What a page says about itself: where it is after any redirect, its title, and its visible text. The
    /// slice happens in the page because sending a megabyte back to be cut here is the expensive way round.</summary>
    public static readonly string Read =
        "(function(){var b=document.body;var t=b?b.innerText:'';return{url:String(location.href),title:String(document.title)," +
        "text:t.slice(0," + MaxTextChars.ToString() + "),clipped:t.length>" + MaxTextChars.ToString() + "}})()";

    /// <summary>Clicks something. The target is a CSS selector when the page can read it as one, and otherwise the
    /// visible text of a link or button — a person does not think in selectors, and a model that has just read the page
    /// usually has the label and not the markup. Which of the two matched is reported back, so a typo in a selector
    /// that happened to match a link's text is visible rather than silent.</summary>
    public static string Click(string target) => ClickTemplate.Replace("__TARGET__", Literal(target));

    /// <summary>Fills a field the way a person would, so the page's own listener runs: setting <c>value</c> directly
    /// leaves a framework's state object saying the field is empty, and the form then submits nothing. The native
    /// setter is the only way to make React and its kind notice.</summary>
    public static string Fill(string target, string text) =>
        FillTemplate.Replace("__TARGET__", Literal(target)).Replace("__VALUE__", Literal(text));

    /// <summary>Runs the model's own JavaScript in the page. This is what makes a signed-in session usable rather than
    /// merely visible: <c>fetch('/api/…')</c> from inside the page carries the session's cookies even when they are
    /// HttpOnly and no tool could read them, and the page's origin means CORS never applies.
    ///
    /// A single expression returns its value — a promise is awaited, so <c>await fetch(…).then(r =&gt; r.text())</c>
    /// works as written. A block of statements is a body, and has to say what to keep with <c>return</c>.</summary>
    public static string Script(string code)
    {
        var text = (code ?? "").Trim();
        var body = LooksLikeBody(text)
            ? "(async function(){\n" + text + "\n})()"
            : "(async function(){return (" + text + ");})()";
        return "(async function(){" +
               "function pack(v){if(v===undefined||v===null)return null;var t=typeof v;" +
               "if(t==='string')return v.slice(0," + MaxResultChars.ToString() + ");" +
               "if(t==='number'||t==='boolean')return v;" +
               "try{var s=JSON.stringify(v);if(s===undefined)return null;" +
               "return s.length>" + MaxResultChars.ToString() + "?s.slice(0," + MaxResultChars.ToString() + ")+'…':s}catch(e){return String(v)}}" +
               "try{var v=await " + body + ";return{ok:true,value:pack(v)}}" +
               "catch(e){return{ok:false,error:String((e&&e.stack)||e)}}}" +
               ")()";
    }

    /// <summary>Whether the code is a body rather than one expression. A body is a block containing a semicolon or a
    /// line that starts with <c>return</c>; anything else is evaluated as an expression, which is what makes the
    /// short form — <c>document.title</c>, <c>await fetch(…).then(r =&gt; r.text())</c> — work without a wrapper the
    /// model has to remember.</summary>
    public static bool LooksLikeBody(string code)
    {
        var text = (code ?? "").Trim();
        if (text.Length == 0) return false;
        if (text.IndexOf(';') >= 0) return true;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("return", StringComparison.Ordinal)) continue;
            // `returnValue` is a name, not the keyword.
            if (trimmed.Length == 6 || !char.IsLetterOrDigit(trimmed[6])) return true;
        }
        return false;
    }

    /// <summary>A C# string as a JavaScript string literal. JSON escaping is valid JavaScript for every character that
    /// matters here, and doing it this way means a quote or a newline in a model's target cannot end the literal it is
    /// pasted into.</summary>
    public static string Literal(string value) =>
        JsonConvert.ToString(value ?? "");

    /// <summary>The sentence inside a DevTools <c>exceptionDetails</c>, for a script that failed before it could answer.
    /// The first line is the message and the rest is a stack of the page's own frames, which the model cannot use.</summary>
    public static string Describe(JToken? details)
    {
        if (details is not JObject entry) return "";
        if (entry["exception"] is JObject exception)
        {
            var description = Text(exception["description"]);
            if (description.Length > 0) return FirstLine(description);
            var value = Text(exception["value"]);
            if (value.Length > 0) return value;
        }
        var text = Text(entry["text"]);
        return text.Length > 0 ? FirstLine(text) : "";
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOf('\n');
        var line = (end >= 0 ? text.Substring(0, end) : text).Trim();
        return line.Length > 400 ? line.Substring(0, 400) + "…" : line;
    }

    private static string Text(JToken? token) =>
        token != null && token.Type == JTokenType.String ? token.Value<string>() ?? "" : "";

    private const string ClickTemplate =
        "(function(){" +
        "var q=__TARGET__;" +
        "function depth(el){var d=0;while(el){d++;el=el.parentElement}return d}" +
        "function shown(el){var r=el.getBoundingClientRect();if(r.width<=0&&r.height<=0)return false;" +
        "var s=getComputedStyle(el);return s.visibility!=='hidden'&&s.display!=='none'}" +
        "function label(el){var t=el.innerText;if(typeof t!=='string'||!t)t=el.value;" +
        "return String(t||'').replace(/\\s+/g,' ').trim()}" +
        "var how='selector',el=null;" +
        "try{el=document.querySelector(q)}catch(e){el=null;how=''}" +
        "if(!el){how='text';var want=String(q||'').replace(/\\s+/g,' ').trim().toLowerCase();" +
        "if(!want)return{ok:false,error:'The target was empty.'};" +
        "var all=document.querySelectorAll('a,button,input,label,summary,li,span,div,td,th,p,h1,h2,h3,h4,h5,h6," +
        "[role=button],[role=link],[role=tab],[onclick]');" +
        "var exact=null,partial=null;" +
        "for(var i=0;i<all.length;i++){var e=all[i];if(!shown(e))continue;" +
        "var t=label(e).toLowerCase();if(!t)continue;" +
        "if(t===want){if(!exact||depth(e)>depth(exact))exact=e}" +
        "else if(t.indexOf(want)>=0){if(!partial||depth(e)>depth(partial))partial=e}}" +
        "el=exact||partial;if(!exact)how='text-fragment'}" +
        "if(!el)return{ok:false,error:'No visible element on the page matched that target, and nothing on the page has " +
        "that text.'};" +
        "try{el.scrollIntoView({block:'center'})}catch(e){}" +
        "try{el.click()}catch(e){return{ok:false,error:'The element was found but the click failed: '+String((e&&e.message)||e)}}" +
        "return{ok:true,how:how,what:(el.tagName||'').toLowerCase()+' \"'+label(el).slice(0,80)+'\"'}" +
        "})()";

    private const string FillTemplate =
        "(function(){" +
        "var el=null;try{el=document.querySelector(__TARGET__)}catch(e){el=null}" +
        "if(!el)return{ok:false,error:'No element matched that selector, so nothing was filled. Pass the field\'s CSS selector.'};" +
        "var v=__VALUE__;" +
        "try{el.scrollIntoView({block:'center'});el.focus()}catch(e){}" +
        "try{if(el.tagName==='INPUT'||el.tagName==='TEXTAREA'){" +
        "var proto=el.tagName==='TEXTAREA'?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;" +
        "var d=Object.getOwnPropertyDescriptor(proto,'value');" +
        "if(d&&d.set)d.set.call(el,v);else el.value=v}" +
        "else if(el.isContentEditable){el.textContent=v}else{el.value=v}}" +
        "catch(e){return{ok:false,error:'The field was found but could not be filled: '+String((e&&e.message)||e)}}" +
        "try{el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}))}catch(e){}" +
        "return{ok:true,what:(el.tagName||'').toLowerCase()+' filled with '+v.length+' characters'}" +
        "})()";
}
