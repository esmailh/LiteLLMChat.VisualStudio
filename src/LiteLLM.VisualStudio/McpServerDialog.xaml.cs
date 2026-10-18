using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LiteLLM.Core;

namespace LiteLLM.VisualStudio;

/// <summary>The editor for one MCP server. It edits a copy and writes it back only when Save succeeds, so Cancel —
/// or a validation that fails on the last field — leaves the configured server exactly as it was.
///
/// The tool lists are filled by the Test button rather than by a background probe when the dialog opens: connecting
/// to every server merely to populate a dropdown would launch processes the user has not asked for yet.</summary>
public partial class McpServerDialog : Window
{
    private readonly McpServerConfiguration _server;
    private readonly McpClient _client;
    private readonly UserSettings _settings;
    private readonly string? _solutionRoot;
    private readonly ObservableCollection<KeyValueRow> _environment = new ObservableCollection<KeyValueRow>();
    private readonly ObservableCollection<KeyValueRow> _headers = new ObservableCollection<KeyValueRow>();

    internal McpServerDialog(McpServerConfiguration source, McpClient client, UserSettings settings,
        string? solutionRoot = null)
    {
        _server = source.Clone();
        _client = client;
        _settings = settings;
        _solutionRoot = solutionRoot;
        InitializeComponent();
        try
        {
            VsTheme.ApplyToDialog(this);
            NameBox.Text = _server.Name;
            EnabledBox.IsChecked = _server.Enabled;
            ExposeBox.IsChecked = _server.ExposeToClaude;
            AutoApproveBox.IsChecked = _server.AutoApproveReadOnly;
            CommandBox.Text = _server.Command;
            ArgsBox.Text = string.Join(Environment.NewLine, _server.Args);
            WorkingDirBox.Text = _server.WorkingDirectory;
            UrlBox.Text = _server.Url;
            OAuthBox.IsChecked = _server.UseOAuth;
            ScopesBox.Text = string.Join(" ", _server.Scopes);
            ConnectTimeoutBox.Text = _server.ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            ToolTimeoutBox.Text = _server.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            SolutionRootBox.Text = _server.SolutionRoot;
            AllowedToolsBox.Text = string.Join(Environment.NewLine, _server.AllowedTools);
            DisabledToolsBox.Text = string.Join(Environment.NewLine, _server.DisabledTools);

            foreach (var pair in _server.Environment)
                _environment.Add(new KeyValueRow { Key = pair.Key, Value = pair.Value });
            foreach (var pair in _server.Headers) _headers.Add(new KeyValueRow { Key = pair.Key, Value = pair.Value });
            EnvironmentRows.ItemsSource = _environment;
            HeaderRows.ItemsSource = _headers;

            SelectByTag(TransportBox, _server.Kind == McpTransportKind.Stdio ? "stdio" :
                _server.Kind == McpTransportKind.Sse ? "sse" : "http");
            SelectByTag(ProtocolBox, string.IsNullOrWhiteSpace(_server.ProtocolVersion) ? "auto" : _server.ProtocolVersion);
            UrlBox.TextChanged += (s, e) => UpdateWarning();
            UpdateTransport();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("McpServerDialog init: " + ex); }
    }

    /// <summary>The edited copy. Only meaningful after a successful Save.</summary>
    internal McpServerConfiguration Server => _server;

    /// <summary>Writes the saved copy into the configured entry, so every reference that already points at that
    /// entry — the live session, the tool snapshot, the allow lists — keeps pointing at it.</summary>
    internal void ApplyTo(McpServerConfiguration target)
    {
        target.Name = _server.Name;
        target.Enabled = _server.Enabled;
        target.ExposeToClaude = _server.ExposeToClaude;
        target.Transport = _server.Transport;
        target.Command = _server.Command;
        target.Args = new List<string>(_server.Args);
        target.WorkingDirectory = _server.WorkingDirectory;
        target.Environment = new Dictionary<string, string>(_server.Environment, StringComparer.OrdinalIgnoreCase);
        target.Url = _server.Url;
        target.Headers = new Dictionary<string, string>(_server.Headers, StringComparer.OrdinalIgnoreCase);
        target.ProtectedAccessToken = _server.ProtectedAccessToken;
        target.UseOAuth = _server.UseOAuth;
        target.Scopes = new List<string>(_server.Scopes);
        target.ProtocolVersion = _server.ProtocolVersion;
        target.AllowedTools = new List<string>(_server.AllowedTools);
        target.DisabledTools = new List<string>(_server.DisabledTools);
        target.AutoApproveReadOnly = _server.AutoApproveReadOnly;
        target.TimeoutSeconds = _server.TimeoutSeconds;
        target.ConnectTimeoutSeconds = _server.ConnectTimeoutSeconds;
        target.SolutionRoot = _server.SolutionRoot;
    }

    private void Transport_Changed(object sender, SelectionChangedEventArgs e) => UpdateTransport();

    private void UpdateTransport()
    {
        var tag = (TransportBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "stdio";
        if (StdioPanel == null || HttpPanel == null) return;
        StdioPanel.Visibility = tag == "stdio" ? Visibility.Visible : Visibility.Collapsed;
        HttpPanel.Visibility = tag == "stdio" ? Visibility.Collapsed : Visibility.Visible;
        UpdateWarning();
    }

    private void UpdateWarning()
    {
        if (HttpWarning == null || UrlBox == null) return;
        var insecure = Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var uri) &&
                       uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback;
        HttpWarning.Visibility = insecure ? Visibility.Visible : Visibility.Collapsed;
        HttpWarning.Text = "This URL is plain HTTP and not loopback, so headers and any token travel unencrypted. " +
                           "The connection is still allowed; HTTPS is what protects it.";
    }

    private void AddEnvironment_Click(object sender, RoutedEventArgs e) => AddRow(_environment, NewEnvKey, NewEnvValue);
    private void RemoveEnvironment_Click(object sender, RoutedEventArgs e) => RemoveRow(EnvironmentRows, _environment);
    private void AddHeader_Click(object sender, RoutedEventArgs e) => AddRow(_headers, NewHeaderKey, NewHeaderValue);
    private void RemoveHeader_Click(object sender, RoutedEventArgs e) => RemoveRow(HeaderRows, _headers);

    private static void AddRow(ObservableCollection<KeyValueRow> rows, TextBox key, TextBox value)
    {
        if (string.IsNullOrWhiteSpace(key.Text)) { key.Focus(); return; }
        rows.Add(new KeyValueRow { Key = key.Text.Trim(), Value = value.Text });
        key.Clear();
        value.Clear();
        key.Focus();
    }

    private static void RemoveRow(ListBox list, ObservableCollection<KeyValueRow> rows)
    {
        if (list.SelectedItem is KeyValueRow row) rows.Remove(row);
    }

    private void Test_Click(object sender, RoutedEventArgs e) => _ = TestAsync();

    private async Task TestAsync()
    {
        ReadFields();
        var problem = _server.ValidationError();
        if (problem != null) { TestStatus.Text = problem; return; }

        TestStatus.Text = "Connecting…";
        try
        {
            var status = await _client.ConnectAsync(_server, _settings, _solutionRoot, CancellationToken.None);
            if (!status.Connected)
            {
                TestStatus.Text = status.Error ?? "The connection failed without a reason.";
                return;
            }

            var tools = _client.Tools
                .Where(tool => string.Equals(tool.Server.Id, _server.Id, StringComparison.Ordinal))
                .Select(tool => new ToolRow(tool))
                .ToList();
            AvailableTools.ItemsSource = tools;
            TestStatus.Text = "Connected · " + status.Era + " " + status.Version + " · " + tools.Count +
                              (tools.Count == 1 ? " tool" : " tools");
        }
        catch (Exception ex)
        {
            TestStatus.Text = McpLog.Redact(ex.Message);
        }
    }

    private void AlwaysAllow_Click(object sender, RoutedEventArgs e) => Append(AllowedToolsBox, AvailableTools);
    private void NeverAllow_Click(object sender, RoutedEventArgs e) => Append(DisabledToolsBox, AvailableTools);

    private static void Append(TextBox box, ListBox tools)
    {
        if (!(tools.SelectedItem is ToolRow row) || string.IsNullOrWhiteSpace(row.Name)) return;
        var lines = Lines(box.Text);
        if (!lines.Any(line => string.Equals(line, row.Name, StringComparison.Ordinal))) lines.Add(row.Name);
        box.Text = string.Join(Environment.NewLine, lines);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ReadFields();
        var problem = _server.ValidationError();
        if (problem != null) { ErrorText.Text = problem; return; }
        ErrorText.Text = "";
        DialogResult = true;
    }

    private void ReadFields()
    {
        _server.Name = NameBox.Text.Trim();
        var transport = (TransportBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "stdio";
        _server.Transport = transport;
        _server.Enabled = EnabledBox.IsChecked == true;
        _server.ExposeToClaude = ExposeBox.IsChecked == true;
        _server.AutoApproveReadOnly = AutoApproveBox.IsChecked == true;
        _server.Command = CommandBox.Text.Trim();
        _server.Args = Lines(ArgsBox.Text);
        _server.WorkingDirectory = WorkingDirBox.Text.Trim();
        _server.Environment = Map(_environment);
        _server.Url = UrlBox.Text.Trim();
        _server.Headers = Map(_headers);
        // Blank keeps whatever the store already holds: clearing the box would otherwise be the only way to type a
        // new token, and "blank" is what every other masked field in this extension means.
        if (TokenBox.Password.Length > 0) _server.ProtectedAccessToken = McpSecrets.Protect(TokenBox.Password);
        _server.UseOAuth = OAuthBox.IsChecked == true;
        _server.Scopes = Split(ScopesBox.Text);
        _server.ProtocolVersion = (ProtocolBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        _server.ConnectTimeoutSeconds = Number(ConnectTimeoutBox.Text);
        _server.TimeoutSeconds = Number(ToolTimeoutBox.Text);
        _server.SolutionRoot = SolutionRootBox.Text.Trim();
        _server.AllowedTools = Lines(AllowedToolsBox.Text);
        _server.DisabledTools = Lines(DisabledToolsBox.Text);
    }

    private static Dictionary<string, string> Map(IEnumerable<KeyValueRow> rows)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = (row.Key ?? "").Trim();
            if (key.Length > 0) result[key] = row.Value ?? "";
        }
        return result;
    }

    private static List<string> Lines(string? text)
    {
        var result = new List<string>();
        foreach (var raw in (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length > 0 && !result.Contains(line)) result.Add(line);
        }
        return result;
    }

    private static List<string> Split(string? text) =>
        (text ?? "").Split(new[] { ' ', '\t', ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToList();

    private static int Number(string? text) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0, value)
            : 0;

    private static void SelectByTag(ComboBox box, string tag)
    {
        for (var index = 0; index < box.Items.Count; index++)
        {
            if (box.Items[index] is ComboBoxItem item &&
                string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = index;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private sealed class KeyValueRow : INotifyPropertyChanged
    {
        private string _key = "";
        private string _value = "";
        public string Key { get => _key; set { if (_key == value) return; _key = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Key))); } }
        public string Value { get => _value; set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class ToolRow
    {
        internal ToolRow(McpToolInfo tool)
        {
            Name = tool.Name;
            var badges = new List<string>();
            if (tool.ReadOnlyHint) badges.Add("read-only");
            if (tool.DestructiveHint) badges.Add("destructive");
            if (tool.OpenWorldHint) badges.Add("open-world");
            var description = string.IsNullOrWhiteSpace(tool.Description) ? "" : tool.Description!.Trim();
            if (description.Length > 160) description = description.Substring(0, 160) + "…";
            Description = description + (badges.Count == 0 ? "" : (description.Length == 0 ? "" : " · ") + string.Join(" · ", badges));
        }

        public string Name { get; }
        public string Description { get; }
    }
}
