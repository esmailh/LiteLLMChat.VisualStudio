using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LiteLLM.Core;
using Newtonsoft.Json.Linq;

namespace LiteLLM.VisualStudio;

/// <summary>Fills a prompt's arguments and returns the text the server composed. The result is inserted into the
/// composer rather than sent: a prompt is a starting point the user is meant to edit, and sending it straight away
/// would take that chance away.</summary>
public partial class McpPromptDialog : Window
{
    private readonly McpPromptInfo _prompt;
    private readonly McpClient _client;
    private readonly List<ArgumentBox> _boxes = new List<ArgumentBox>();

    internal McpPromptDialog(McpPromptInfo prompt, McpClient client)
    {
        _prompt = prompt;
        _client = client;
        InitializeComponent();
        try
        {
            VsTheme.ApplyToDialog(this);
            PromptTitle.Text = prompt.DisplayName;
            PromptDescription.Text = string.IsNullOrWhiteSpace(prompt.Description) ? prompt.Name : prompt.Description!;
            foreach (var argument in prompt.Arguments)
            {
                var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                stack.Children.Add(new TextBlock
                {
                    Text = argument.Name + (argument.Required ? " *" : ""),
                    FontWeight = FontWeights.SemiBold
                });
                if (!string.IsNullOrWhiteSpace(argument.Description))
                    stack.Children.Add(new TextBlock
                    {
                        Text = argument.Description,
                        Style = (Style)FindResource("Caption"),
                        TextWrapping = TextWrapping.Wrap
                    });
                var box = new TextBox { Margin = new Thickness(0, 3, 0, 0) };
                stack.Children.Add(box);
                ArgumentsPanel.Children.Add(stack);
                _boxes.Add(new ArgumentBox(argument, box));
            }
            if (_boxes.Count == 0)
                ArgumentsPanel.Children.Add(new TextBlock
                {
                    Text = "This prompt takes no arguments.",
                    Style = (Style)FindResource("Caption")
                });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("McpPromptDialog init: " + ex); }
    }

    /// <summary>The text the server returned. Only meaningful after the dialog closes with OK.</summary>
    internal string Text { get; private set; } = "";

    private void Insert_Click(object sender, RoutedEventArgs e) => _ = InsertAsync();

    private async Task InsertAsync()
    {
        var arguments = new JObject();
        foreach (var entry in _boxes)
        {
            var value = entry.Box.Text.Trim();
            if (value.Length == 0)
            {
                if (entry.Argument.Required)
                {
                    ErrorText.Text = entry.Argument.Name + " is required.";
                    entry.Box.Focus();
                    return;
                }
                continue;
            }
            arguments[entry.Argument.Name] = value;
        }

        InsertButton.IsEnabled = false;
        ErrorText.Text = "Reading the prompt…";
        try
        {
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            {
                var result = await _client.GetPromptAsync(_prompt.Server.Id, _prompt.Name, arguments,
                    cancellation.Token);
                Text = string.Join(Environment.NewLine + Environment.NewLine,
                    result.Messages.Select(message => message.Text).Where(text => text.Length > 0));
                if (Text.Length == 0)
                {
                    ErrorText.Text = "The server returned no text for this prompt.";
                    return;
                }
                DialogResult = true;
            }
        }
        catch (OperationCanceledException) { ErrorText.Text = "The prompt did not arrive within two minutes."; }
        catch (Exception ex) { ErrorText.Text = McpLog.Redact(ex.Message); }
        finally { InsertButton.IsEnabled = true; }
    }

    private sealed class ArgumentBox
    {
        internal ArgumentBox(McpPromptArgument argument, TextBox box)
        {
            Argument = argument;
            Box = box;
        }

        internal McpPromptArgument Argument { get; }
        internal TextBox Box { get; }
    }
}
