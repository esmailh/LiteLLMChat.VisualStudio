using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LiteLLM.Core;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiteLLM.VisualStudio;

/// <summary>The hand-operated side of MCP: list what one connected server offers, call a tool through a form built
/// from its own input schema, read a resource, and read a prompt. Everything here is text the server wrote, shown as
/// text; a tool that changes something is run only because the user pressed Run on that tool.</summary>
public partial class McpBrowserDialog : Window
{
    private readonly McpClient _client;
    private readonly McpServerConfiguration _server;
    private readonly List<ArgumentField> _toolFields = new List<ArgumentField>();
    private readonly List<ArgumentField> _promptFields = new List<ArgumentField>();
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);
    /// <summary>The UI context, captured when the dialog opens, so a log line from the server's thread reaches the
    /// list on the thread that owns it.</summary>
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    internal McpBrowserDialog(McpClient client, McpServerConfiguration server)
    {
        _client = client;
        _server = server;
        InitializeComponent();
        try
        {
            VsTheme.ApplyToDialog(this);
            TitleText.Text = "MCP · " + server.Name;
            ToolList.ItemsSource = client.Tools
                .Where(tool => string.Equals(tool.Server.Id, server.Id, StringComparison.Ordinal))
                .Select(tool => new ToolRow(tool)).ToList();
            ResourceList.ItemsSource = client.Resources
                .Where(resource => string.Equals(resource.Server.Id, server.Id, StringComparison.Ordinal))
                .Select(resource => new ResourceRow(resource)).ToList();
            PromptList.ItemsSource = client.Prompts
                .Where(prompt => string.Equals(prompt.Server.Id, server.Id, StringComparison.Ordinal))
                .Select(prompt => new PromptRow(prompt)).ToList();
            RefreshLogs();
            client.Logged += OnLogged;
            Closed += (s, e) => client.Logged -= OnLogged;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("McpBrowserDialog init: " + ex); }
    }

    private void OnLogged(McpServerConfiguration server, McpLogEntry entry)
    {
        if (!string.Equals(server.Id, _server.Id, StringComparison.Ordinal)) return;
        if (ReferenceEquals(_ui, SynchronizationContext.Current))
        {
            RefreshLogs();
            return;
        }
        // Same as the Dashboard: the line arrived on the server's receive loop and has nothing to await.
#pragma warning disable VSSDK007 // Fire-and-forget UI refresh; the joinable task is deliberately not joined.
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            RefreshLogs();
        }).FileAndForget("LiteLLM/MCP");
#pragma warning restore VSSDK007
    }

    private void RefreshLogs_Click(object sender, RoutedEventArgs e) => RefreshLogs();

    private void RefreshLogs()
    {
        var log = _client.LogOf(_server.Id);
        LogList.ItemsSource = log == null
            ? new List<LogRow>()
            : log.Snapshot().Select(entry => new LogRow(entry)).ToList();
        if (LogList.Items.Count == 0)
            LogList.ItemsSource = new List<LogRow>
            {
                new LogRow("info", "No log entries. The server writes to this list when it reports something.")
            };
    }

    private void Tool_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!(ToolList.SelectedItem is ToolRow row))
        {
            ToolTitle.Text = "Select a tool";
            ToolDescription.Text = "";
            ArgumentsPanel.Children.Clear();
            _toolFields.Clear();
            return;
        }
        ToolTitle.Text = row.Tool.DisplayName;
        ToolDescription.Text = row.Tool.Description ?? "";
        ToolResult.Text = "";
        BuildFields(ArgumentsPanel, row.Tool.InputSchema, _toolFields);
    }

    private void Prompt_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PromptResult.Text = "";
        if (!(PromptList.SelectedItem is PromptRow row))
        {
            PromptArgumentsPanel.Children.Clear();
            _promptFields.Clear();
            return;
        }
        var properties = new JObject();
        var required = new JArray();
        foreach (var argument in row.Prompt.Arguments)
        {
            properties[argument.Name] = new JObject
            {
                ["type"] = "string",
                ["description"] = argument.Description ?? ""
            };
            if (argument.Required) required.Add(argument.Name);
        }
        BuildFields(PromptArgumentsPanel, new JObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        }, _promptFields);
    }

    private void RunTool_Click(object sender, RoutedEventArgs e) => _ = RunToolAsync();

    private async Task RunToolAsync()
    {
        if (!(ToolList.SelectedItem is ToolRow row)) return;
        var arguments = new JObject();
        var problem = ReadFields(_toolFields, arguments);
        if (problem != null) { ToolResult.Text = problem; return; }

        // A second click while the first call is in flight would run a mutating tool twice; changing the selection
        // meanwhile would write the answer under a different tool. Both controls wait for the call to finish.
        RunToolButton.IsEnabled = false;
        ToolList.IsEnabled = false;
        ToolResult.Text = "Running…";
        try
        {
            using (var cancellation = new CancellationTokenSource(CallTimeout))
            {
                var result = await _client.CallToolAsync(row.Tool, arguments, null, cancellation.Token);
                ToolResult.Text = (result.Ok ? "" : "FAILED · ") + result.Summary + Environment.NewLine +
                                  Environment.NewLine + result.Content;
            }
        }
        catch (OperationCanceledException) { ToolResult.Text = "The call did not finish within two minutes."; }
        catch (Exception ex) { ToolResult.Text = McpLog.Redact(ex.Message); }
        finally
        {
            RunToolButton.IsEnabled = true;
            ToolList.IsEnabled = true;
        }
    }

    private void ReadResource_Click(object sender, RoutedEventArgs e) => _ = ReadResourceAsync();

    private async Task ReadResourceAsync()
    {
        if (!(ResourceList.SelectedItem is ResourceRow row)) return;
        ResourceResult.Text = "Reading…";
        try
        {
            using (var cancellation = new CancellationTokenSource(CallTimeout))
            {
                var read = await _client.ReadResourceAsync(_server.Id, row.Resource.Uri, cancellation.Token);
                ResourceResult.Text = read.Contents.Count == 0
                    ? "The server returned no content for " + row.Resource.Uri + "."
                    : string.Join(Environment.NewLine + Environment.NewLine,
                        read.Contents.Select(content => content.ToText(null)));
            }
        }
        catch (OperationCanceledException) { ResourceResult.Text = "The read did not finish within two minutes."; }
        catch (Exception ex) { ResourceResult.Text = McpLog.Redact(ex.Message); }
    }

    private void GetPrompt_Click(object sender, RoutedEventArgs e) => _ = GetPromptAsync();

    private async Task GetPromptAsync()
    {
        if (!(PromptList.SelectedItem is PromptRow row)) return;
        var arguments = new JObject();
        var problem = ReadFields(_promptFields, arguments);
        if (problem != null) { PromptResult.Text = problem; return; }

        PromptResult.Text = "Reading…";
        try
        {
            using (var cancellation = new CancellationTokenSource(CallTimeout))
            {
                var prompt = await _client.GetPromptAsync(_server.Id, row.Prompt.Name, arguments,
                    cancellation.Token);
                PromptResult.Text = prompt.Messages.Count == 0
                    ? "The server returned no messages for this prompt."
                    : string.Join(Environment.NewLine + Environment.NewLine,
                        prompt.Messages.Select(message => "[" + message.Role + "]" + Environment.NewLine + message.Text));
            }
        }
        catch (OperationCanceledException) { PromptResult.Text = "The prompt did not arrive within two minutes."; }
        catch (Exception ex) { PromptResult.Text = McpLog.Redact(ex.Message); }
    }

    /// <summary>One control per property the schema declares. A form is generated at all because the alternative —
    /// asking the user to type JSON — is a test of the editor rather than of the server.</summary>
    private void BuildFields(Panel panel, JObject? schema, List<ArgumentField> fields)
    {
        panel.Children.Clear();
        fields.Clear();
        var properties = schema?["properties"] as JObject;
        if (properties == null || properties.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "This takes no arguments.",
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(0, 0, 0, 4)
            });
            return;
        }

        var required = new HashSet<string>(
            (schema?["required"] as JArray)?.OfType<JValue>().Select(value => value.ToString()) ??
            Enumerable.Empty<string>(), StringComparer.Ordinal);
        foreach (var property in properties.Properties())
        {
            var spec = property.Value as JObject ?? new JObject();
            var type = spec.Value<string>("type") ?? "string";
            var isRequired = required.Contains(property.Name);
            var description = spec.Value<string>("description");

            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            stack.Children.Add(new TextBlock
            {
                Text = property.Name + (isRequired ? " *" : ""),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!string.IsNullOrWhiteSpace(description))
                stack.Children.Add(new TextBlock
                {
                    Text = description,
                    Style = (Style)FindResource("Caption"),
                    TextWrapping = TextWrapping.Wrap
                });

            var values = spec["enum"] as JArray;
            FrameworkElement control;
            if (string.Equals(type, "boolean", StringComparison.OrdinalIgnoreCase))
            {
                control = new CheckBox
                {
                    Content = "true",
                    IsChecked = spec["default"]?.Type == JTokenType.Boolean && spec.Value<bool>("default")
                };
            }
            else if (values != null)
            {
                var box = new ComboBox { IsEditable = false };
                box.Items.Add("");
                foreach (var value in values)
                    if (value.Type == JTokenType.String) box.Items.Add(value.Value<string>());
                var fallback = spec["default"]?.ToString();
                box.SelectedItem = fallback != null && box.Items.Contains(fallback) ? fallback : "";
                control = box;
            }
            else
            {
                var box = new TextBox { Text = DefaultText(spec["default"]) };
                stack.Children.Add(box);
                panel.Children.Add(stack);
                fields.Add(new ArgumentField(property.Name, type, isRequired, box));
                continue;
            }

            stack.Children.Add(control);
            panel.Children.Add(stack);
            fields.Add(new ArgumentField(property.Name, type, isRequired, control));
        }
    }

    /// <summary>Reads the form into the arguments object. Returns the sentence to show instead when a required field
    /// is empty or a value cannot be read — a malformed call is reported here rather than sent to the server.</summary>
    private static string? ReadFields(IEnumerable<ArgumentField> fields, JObject arguments)
    {
        foreach (var field in fields)
        {
            string text;
            switch (field.Control)
            {
                case CheckBox check:
                    // The checkbox is the whole form for a boolean: unchecked means false. Omitting it when it is
                    // optional made a schema default of true impossible to turn off — the server would receive
                    // nothing and apply the default, the opposite of what the form showed.
                    arguments[field.Name] = check.IsChecked == true;
                    continue;
                case ComboBox combo:
                    text = combo.SelectedItem as string ?? "";
                    if (text.Length == 0)
                    {
                        if (field.Required) return field.Name + " is required.";
                        continue;
                    }
                    arguments[field.Name] = text;
                    continue;
                default:
                    text = (field.Control as TextBox)?.Text ?? "";
                    break;
            }

            text = text.Trim();
            if (text.Length == 0)
            {
                if (field.Required) return field.Name + " is required.";
                continue;
            }

            switch (field.Type.ToLowerInvariant())
            {
                case "integer":
                    if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                        return field.Name + " must be a whole number.";
                    arguments[field.Name] = integer;
                    break;
                case "number":
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        return field.Name + " must be a number.";
                    arguments[field.Name] = number;
                    break;
                case "array":
                case "object":
                    try { arguments[field.Name] = JToken.Parse(text); }
                    catch (JsonException) { return field.Name + " must be valid JSON."; }
                    break;
                default:
                    arguments[field.Name] = text;
                    break;
            }
        }
        return null;
    }

    /// <summary>A schema default is a JSON value; a string default belongs in the box without its quotes.</summary>
    private static string DefaultText(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return "";
        return token.Type == JTokenType.String
            ? token.Value<string>() ?? ""
            : token.ToString(Formatting.None);
    }
    private sealed class ArgumentField
    {
        internal ArgumentField(string name, string type, bool required, FrameworkElement control)
        {
            Name = name;
            Type = type;
            Required = required;
            Control = control;
        }

        public string Name { get; }
        public string Type { get; }
        public bool Required { get; }
        public FrameworkElement Control { get; }
    }

    private sealed class ToolRow
    {
        internal ToolRow(McpToolInfo tool)
        {
            Tool = tool;
            var badges = new List<string>();
            if (tool.ReadOnlyHint) badges.Add("read-only");
            if (tool.DestructiveHint) badges.Add("destructive");
            if (tool.OpenWorldHint) badges.Add("open-world");
            var description = string.IsNullOrWhiteSpace(tool.Description) ? "" : tool.Description!.Trim();
            if (description.Length > 160) description = description.Substring(0, 160) + "…";
            Description = description + (badges.Count == 0 ? "" : (description.Length == 0 ? "" : " · ") +
                                                                  string.Join(" · ", badges));
        }

        public McpToolInfo Tool { get; }
        public string Name => Tool.Name;
        public string Description { get; }
    }

    private sealed class ResourceRow
    {
        internal ResourceRow(McpResourceInfo resource)
        {
            Resource = resource;
            Description = resource.Uri + (string.IsNullOrEmpty(resource.MimeType) ? "" : " · " + resource.MimeType);
        }

        public McpResourceInfo Resource { get; }
        public string Name => Resource.DisplayName;
        public string Description { get; }
    }

    private sealed class PromptRow
    {
        internal PromptRow(McpPromptInfo prompt)
        {
            Prompt = prompt;
            Description = prompt.Description ?? prompt.Name;
        }

        public McpPromptInfo Prompt { get; }
        public string Name => Prompt.DisplayName;
        public string Description { get; }
    }

    private sealed class LogRow
    {
        internal LogRow(McpLogEntry entry) : this(entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss") + " · " + entry.Level, entry.Message) { }
        internal LogRow(string name, string description) { Name = name; Description = description; }
        public string Name { get; }
        public string Description { get; }
    }
}
