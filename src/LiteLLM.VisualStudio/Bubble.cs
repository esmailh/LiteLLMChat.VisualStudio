using System.Windows.Controls;
using LiteLLM.Core;

namespace LiteLLM.VisualStudio;

/// <summary>One rendered message: the card, its content panel, and — while the turn runs — the live status line at
/// its foot. It lives beside the view interface rather than inside the chat window because the agent loop holds
/// one between rounds and must be able to name it without knowing what draws it.</summary>
internal sealed class Bubble
{
    internal Border Border = null!;
    internal StackPanel Body = null!;
    internal StackPanel? Live;
    internal TextBlock? Status;
    internal ProgressBar? Bar;
    internal ChatMessage? Message;
    internal HttpExchange? Exchange;
}
