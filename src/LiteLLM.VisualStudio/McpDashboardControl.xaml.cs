using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EnvDTE;
using LiteLLM.Core;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;

namespace LiteLLM.VisualStudio;

/// <summary>The MCP page of the Dashboard: what is configured, what it is doing, and the one place a server is
/// added, imported or signed in to.
///
/// The list reads its status from <see cref="McpClient"/> rather than connecting anything itself. Only the buttons
/// that say they connect do so, because a page that opens a connection just by being looked at would make the
/// server list depend on every server answering.</summary>
public partial class McpDashboardControl : UserControl
{
    private bool _subscribed;
    private string? _selectedId;
    /// <summary>The reason the last manual attempt failed. <see cref="McpClient.StatusOf"/> keeps only successful
    /// connections, and a failure that vanished from the page the moment the user looked away would be the one thing
    /// this dashboard is meant not to do.</summary>
    private readonly Dictionary<string, string> _lastError = new Dictionary<string, string>(StringComparer.Ordinal);

    public McpDashboardControl()
    {
        InitializeComponent();
        VsTheme.Apply(this);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private ExtensionServices Services => LiteLlmPackage.Instance.Services;

    private McpServerConfiguration? Selected => (Servers.SelectedItem as ServerRow)?.Server;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            Services.Mcp.ToolsChanged += OnToolsChanged;
            Services.Mcp.ListsChanged += OnListsChanged;
            Services.Mcp.Logged += OnLogged;
            Services.Mcp.ServerClosed += OnServerClosed;
            _subscribed = true;
        }
        Reload();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        Services.Mcp.ToolsChanged -= OnToolsChanged;
        Services.Mcp.ListsChanged -= OnListsChanged;
        Services.Mcp.Logged -= OnLogged;
        Services.Mcp.ServerClosed -= OnServerClosed;
        _subscribed = false;
    }

    private void OnToolsChanged(object? sender, EventArgs e) => Rebuild(() => ReloadDetail());
    private void OnListsChanged(object? sender, EventArgs e) => Rebuild(() => ReloadDetail());
    private void OnServerClosed(McpServerConfiguration server, string reason)
    {
        // Only a session that ended by itself is reported as an error. A disconnect the page asked for closes a
        // session the client no longer owns, and the client does not raise this event for it. The map and the
        // controls are both touched on the UI thread: the event itself arrives on the connection's thread.
        if (!server.Enabled) return;
        Rebuild(() =>
        {
            _lastError[server.Id] = "The connection ended: " + reason;
            ReloadDetail();
        });
    }

    private void OnLogged(McpServerConfiguration server, McpLogEntry entry)
    {
        // Only a selected server's log tab is rebuilt. A chatty stdio server writes on its own thread, and rebuilding
        // every list for each line would spend the UI thread on rows nobody is looking at. The selection and the tab
        // are checked inside the dispatch: reading them here would be a cross-thread call on the controls.
        Rebuild(() =>
        {
            if (Selected == null || !string.Equals(Selected!.Id, server.Id, StringComparison.Ordinal)) return;
            if (DetailTabs == null || DetailTabs.SelectedIndex != 3) return;
            ReloadLogs();
        });
    }

    private void Rebuild(Action action)
    {
        // The client raises its events on a receive loop, so the update has to cross to the UI thread — including
        // the IsLoaded check, which belongs to the thread that owns the control. There is
        // nothing for the caller to await: the refresh is the last thing that happens, and its failure is a control
        // left as it was rather than a lost answer.
#pragma warning disable VSSDK007 // Fire-and-forget UI refresh; the joinable task is deliberately not joined.
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (IsLoaded) action();
        }).FileAndForget("LiteLLM/MCP");
#pragma warning restore VSSDK007
    }

    internal void Reload()
    {
        _selectedId = Selected?.Id ?? _selectedId;
        var rows = (Services.Settings.McpServers ?? new List<McpServerConfiguration>())
            .Where(server => server != null)
            .Select(server => new ServerRow(server, Services.Mcp.StatusOf(server.Id),
                _lastError.TryGetValue(server.Id, out var error) ? error : null))
            .ToList();
        Servers.ItemsSource = rows;
        if (_selectedId != null)
            Servers.SelectedItem = rows.FirstOrDefault(row =>
                string.Equals(row.Server.Id, _selectedId, StringComparison.Ordinal));
        ReloadDetail();
    }

    private void ReloadDetail()
    {
        var server = Selected;
        if (server == null)
        {
            DetailTitle.Text = "Select a server";
            DetailStatus.Text = (Services.Settings.McpServers?.Count ?? 0) == 0
                ? "No server is configured yet. Add one, or import a Claude or Cursor mcpServers file."
                : "Nothing is selected.";
            DetailNote.Text = "";
            DetailActions.Visibility = Visibility.Collapsed;
            Tools.ItemsSource = ResourceItems.ItemsSource = Prompts.ItemsSource = Logs.ItemsSource = null;
            return;
        }

        _selectedId = server.Id;
        DetailActions.Visibility = Visibility.Visible;
        DetailTitle.Text = server.Name;
        var status = Services.Mcp.StatusOf(server.Id);
        var transport = server.Kind == McpTransportKind.Stdio ? "stdio" : server.Kind == McpTransportKind.Sse ? "sse" : "http";
        if (!server.Enabled)
        {
            DetailStatus.Text = "Disabled · " + transport;
        }
        else if (status != null && status.Connected)
        {
            DetailStatus.Text = "Connected · " + transport + " · " + status.Era + " " + status.Version +
                                " · " + status.Tools + (status.Tools == 1 ? " tool" : " tools");
        }
        else
        {
            DetailStatus.Text = "Not connected · " + transport;
        }
        DetailStatus.Text += server.ExposeToClaude ? " · Claude: exposed" : " · Claude: not exposed";

        var notes = new List<string>();
        if (!server.Enabled)
            notes.Add("This server is disabled: it is not connected and its tools are not offered. Tick it in the list to enable it.");
        if (!Services.Settings.EnableMcp)
            notes.Add("MCP is turned off in Tools ▸ Options ▸ LiteLLM, so no server connects or offers tools.");
        var problem = server.ValidationError();
        if (problem != null) notes.Add(problem);
        if (_lastError.TryGetValue(server.Id, out var error)) notes.Add("Last attempt: " + error);
        if (server.UsesUnencryptedRemoteHttp())
            notes.Add("This URL uses plain HTTP, so headers and any token travel unencrypted. Use HTTPS unless the server is on this machine.");
        if (server.UseOAuth && !server.ExposeToClaude)
            notes.Add("Sign-in is not shared with the Claude backend, which starts its own connection.");
        DetailNote.Text = string.Join("\n", notes);

        var connected = status != null && status.Connected;
        BrowseButton.IsEnabled = server.Enabled && (connected || server.ValidationError() == null);
        DisconnectButton.IsEnabled = connected;
        SignInButton.Visibility = server.UseOAuth && server.Kind != McpTransportKind.Stdio
            ? Visibility.Visible
            : Visibility.Collapsed;
        SignInButton.IsEnabled = server.Enabled && server.ValidationError() == null;
        SignOutButton.Visibility = SignInButton.Visibility;
        SignOutButton.IsEnabled = server.UseOAuth && Services.Mcp.HasSignIn(server);

        Tools.ItemsSource = Services.Mcp.Tools
            .Where(tool => string.Equals(tool.Server.Id, server.Id, StringComparison.Ordinal))
            .Select(tool => new Row(tool.DisplayName, DescribeTool(tool)))
            .ToList();
        ResourceItems.ItemsSource = Services.Mcp.Resources
            .Where(resource => string.Equals(resource.Server.Id, server.Id, StringComparison.Ordinal))
            .Select(resource => new Row(resource.DisplayName,
                resource.Uri + (string.IsNullOrEmpty(resource.MimeType) ? "" : " · " + resource.MimeType)))
            .ToList();
        Prompts.ItemsSource = Services.Mcp.Prompts
            .Where(prompt => string.Equals(prompt.Server.Id, server.Id, StringComparison.Ordinal))
            .Select(prompt => new Row(prompt.DisplayName, prompt.Description ?? prompt.Name))
            .ToList();
        ReloadLogs();

        if (!connected && Tools.Items.Count == 0 && ResourceItems.Items.Count == 0 && Prompts.Items.Count == 0)
        {
            Tools.ItemsSource = new List<Row>
            {
                new Row(server.Enabled ? "Not connected" : "Disabled",
                    server.Enabled
                        ? "Press Connect to read this server's tools, resources and prompts."
                        : "Tick the server in the list to enable it; it then connects by itself.")
            };
        }
    }

    private void ReloadLogs()
    {
        var server = Selected;
        var log = server == null ? null : Services.Mcp.LogOf(server.Id);
        Logs.ItemsSource = log == null
            ? new List<Row> { new Row("No log", "The server has not been connected in this session.") }
            : log.Snapshot().Select(entry => new Row(
                entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss") + " · " + entry.Level, entry.Message)).ToList();
    }

    private static string DescribeTool(McpToolInfo tool)
    {
        var badges = new List<string>();
        if (tool.ReadOnlyHint) badges.Add("read-only");
        if (tool.DestructiveHint) badges.Add("destructive");
        if (tool.OpenWorldHint) badges.Add("open-world");
        var description = string.IsNullOrWhiteSpace(tool.Description) ? "" : tool.Description!.Trim();
        if (description.Length > 200) description = description.Substring(0, 200) + "…";
        var suffix = badges.Count == 0 ? "" : " · " + string.Join(" · ", badges);
        return (description.Length == 0 ? tool.ExposedName : description) + suffix;
    }

    private void Servers_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReloadDetail();

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var server = new McpServerConfiguration { Name = "New MCP server" };
            var dialog = new McpServerDialog(server, Services.Mcp, Services.Settings) { Owner = System.Windows.Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            Services.Settings.McpServers.Add(dialog.Server);
            Services.Save();
            _selectedId = dialog.Server.Id;
            Reload();
        }
        catch (Exception ex)
        {
            Services.Log("Add MCP server failed: " + ex);
            MessageBox.Show("Could not add the server.\n\n" + ex.Message, "MCP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var server = Selected;
        if (server == null) return;
        try
        {
            var dialog = new McpServerDialog(server, Services.Mcp, Services.Settings) { Owner = System.Windows.Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            // Editing in place keeps every reference that already points at this entry — the tool snapshot's server,
            // the log, "always allow" entries — aimed at the object that is saved.
            dialog.ApplyTo(server);
            // The dialog can uncheck Enabled. A session started under the old settings must not outlive the choice
            // the row now shows.
            if (!server.Enabled) Services.Mcp.Disconnect(server.Id);
            _lastError.Remove(server.Id);
            Services.Save();
            _selectedId = server.Id;
            Reload();
        }
        catch (Exception ex)
        {
            Services.Log("Edit MCP server failed: " + ex);
            MessageBox.Show("Could not save the server.\n\n" + ex.Message, "MCP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var server = Selected;
        if (server == null) return;
        if (MessageBox.Show("Remove the MCP server " + server.Name + "?", "MCP",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Services.Mcp.Disconnect(server.Id);
        Services.Mcp.ForgetSignIn(server);
        Services.Settings.McpServers.Remove(server);
        _lastError.Remove(server.Id);
        _selectedId = null;
        Services.Save();
        Reload();
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => _ = ConnectServerAsync(Selected, true);

    private void Test_Click(object sender, RoutedEventArgs e) => _ = ConnectServerAsync(Selected, true);

    private void ServerEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (!(sender is CheckBox box) || !(box.DataContext is ServerRow row)) return;
        var server = row.Server;
        server.Enabled = box.IsChecked == true;
        Services.Save();
        _lastError.Remove(server.Id);
        if (!server.Enabled)
        {
            Services.Mcp.Disconnect(server.Id);
            ScheduleReload();
            return;
        }

        // The tick is the user saying this server should be running, so it starts now rather than at the next turn.
        // The row's own state is the feedback; a failure is recorded in _lastError, which the row draws.
        ScheduleReload();
        _ = EnableAndSettleAsync(server);
    }

    /// <summary>The list is rebuilt after the click, not during it: the checkbox that raised the event belongs to the
    /// item container the rebuild is about to throw away, and replacing the items from inside its own Click is how a
    /// row ends up half-torn-down. A queued reload runs on the same dispatcher once the event is done.</summary>
    private void ScheduleReload() =>
        _ = Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) Reload(); }));

    private async Task EnableAndSettleAsync(McpServerConfiguration server)
    {
        await ConnectServerAsync(server, announce: false);
        // The tick can be undone, or the feature turned off, while the connection is being made. A process must not
        // outlive the choice the row now shows.
        if (!server.Enabled || !Services.Settings.EnableMcp) Services.Mcp.Disconnect(server.Id);
        Reload();
    }

    private void ServerRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (!(sender is Button button) || !(button.DataContext is ServerRow row)) return;
        _ = RefreshServerAsync(row.Server);
    }

    /// <summary>Restarts one server: its session is dropped and started again, so new tools, resources and prompts are
    /// read and a server that was broken can be retried after its command is fixed. The row and the detail pane are
    /// the feedback; there is no dialog for a row button.</summary>
    private async Task RefreshServerAsync(McpServerConfiguration server)
    {
        var refusal = ConnectionRefusal(server);
        if (refusal != null)
        {
            _lastError[server.Id] = refusal;
            Reload();
            return;
        }
        var problem = server.ValidationError();
        if (problem != null)
        {
            _lastError[server.Id] = problem;
            Reload();
            return;
        }

        _lastError.Remove(server.Id);
        if (ReferenceEquals(Selected, server)) DetailStatus.Text = "Reconnecting…";
        try
        {
            var root = await SolutionRootAsync();
            var status = await Services.Mcp.ReconnectAsync(server, Services.Settings, root, CancellationToken.None);
            if (status.Connected)
            {
                _lastError.Remove(server.Id);
                Services.Log("MCP · reconnected to " + server.Name);
            }
            else
            {
                _lastError[server.Id] = status.Error ?? "The connection failed without a reason.";
            }
        }
        catch (Exception ex)
        {
            _lastError[server.Id] = McpLog.Redact(ex.Message);
            Services.Log("MCP refresh failed for " + server.Name + ": " + ex);
        }
        Reload();
    }

    /// <summary>The reasons a server is not started on request, as one sentence the page can show. The row controls
    /// are the user's own switches, so pressing one anyway should answer with the setting that is in the way.</summary>
    private string? ConnectionRefusal(McpServerConfiguration server)
    {
        if (!Services.Settings.EnableMcp)
            return "MCP is turned off in Tools ▸ Options ▸ LiteLLM. Turn it on to connect a server.";
        return server.Enabled ? null : "This server is disabled. Tick it in the list to enable it.";
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var server = Selected;
        if (server == null) return;
        Services.Mcp.Disconnect(server.Id);
        _lastError.Remove(server.Id);
        Reload();
    }

    private async Task ConnectServerAsync(McpServerConfiguration? server, bool announce)
    {
        if (server == null) return;
        var refusal = ConnectionRefusal(server);
        if (refusal != null)
        {
            _lastError[server.Id] = refusal;
            if (announce) MessageBox.Show(refusal, "MCP", MessageBoxButton.OK, MessageBoxImage.Warning);
            Reload();
            return;
        }
        var problem = server.ValidationError();
        if (problem != null)
        {
            _lastError[server.Id] = problem;
            if (announce) MessageBox.Show(problem, "MCP", MessageBoxButton.OK, MessageBoxImage.Warning);
            Reload();
            return;
        }

        if (ReferenceEquals(Selected, server)) DetailStatus.Text = "Connecting…";
        try
        {
            var root = await SolutionRootAsync();
            var status = await Services.Mcp.ConnectAsync(server, Services.Settings, root, CancellationToken.None);
            if (status.Connected)
            {
                _lastError.Remove(server.Id);
                if (announce)
                    MessageBox.Show("Connected. " + status.Tools + (status.Tools == 1 ? " tool" : " tools") +
                                    " offered.", "MCP");
            }
            else
            {
                _lastError[server.Id] = status.Error ?? "The connection failed without a reason.";
                if (announce)
                    MessageBox.Show(_lastError[server.Id], "MCP connection", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _lastError[server.Id] = McpLog.Redact(ex.Message);
            if (announce)
                MessageBox.Show(_lastError[server.Id], "MCP connection", MessageBoxButton.OK, MessageBoxImage.Warning);
            Services.Log("MCP connection failed for " + server.Name + ": " + ex);
        }
        Reload();
    }

    private void Browse_Click(object sender, RoutedEventArgs e) => _ = BrowseAsync();

    private async Task BrowseAsync()
    {
        var server = Selected;
        if (server == null) return;
        try
        {
            if (Services.Mcp.StatusOf(server.Id) == null) await ConnectServerAsync(server, false);
            if (Services.Mcp.StatusOf(server.Id) == null) return;
            var dialog = new McpBrowserDialog(Services.Mcp, server) { Owner = System.Windows.Window.GetWindow(this) };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Services.Log("MCP browser failed: " + ex);
            MessageBox.Show("Could not open the browser.\n\n" + ex.Message, "MCP", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SignIn_Click(object sender, RoutedEventArgs e) => _ = SignInAsync();

    private async Task SignInAsync()
    {
        var server = Selected;
        if (server == null) return;
        if (server.Kind == McpTransportKind.Stdio || string.IsNullOrWhiteSpace(server.Url))
        {
            MessageBox.Show("Only HTTP and SSE servers can sign in.", "MCP sign-in", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            DetailNote.Text = "Preparing sign-in…";
            using (var oauth = new McpOAuthClient())
            using (var loopback = new McpLoopback())
            {
                var discovery = await oauth.DiscoverAsync(server, null, CancellationToken.None);
                var authorization = await oauth.BeginAsync(discovery, loopback.RedirectUri, null,
                    CancellationToken.None);
                System.Diagnostics.Process.Start(new ProcessStartInfo(authorization.AuthorizationUrl.AbsoluteUri)
                {
                    UseShellExecute = true
                });
                DetailNote.Text = "Waiting for the browser to finish signing in…";
                var callback = await loopback.WaitAsync(authorization.State, TimeSpan.FromMinutes(5),
                    CancellationToken.None);
                if (!callback.IsSuccess)
                {
                    var reason = callback.ErrorDescription ?? callback.Error ?? "the browser did not return a code";
                    DetailNote.Text = "Sign-in was not completed: " + reason;
                    MessageBox.Show("Sign-in was not completed: " + reason, "MCP sign-in", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var token = await oauth.ExchangeAsync(authorization, callback.Code!, CancellationToken.None);
                Services.Mcp.StoreSignIn(server, token);
                _lastError.Remove(server.Id);
                Services.Log("MCP · signed in to " + server.Name);
                Reload();
                MessageBox.Show("Signed in.", "MCP");
            }

            // A session that was already running holds the headers it connected with, so it would keep presenting
            // the credential the sign-in just replaced. Restart it now instead of leaving a row that says connected
            // for a connection the server will reject. A disabled server (or globally off MCP) is left alone: the
            // next enable connects with the new grant.
            if (server.Enabled && Services.Settings.EnableMcp)
            {
                await RefreshServerAsync(server);
            }
        }
        catch (Exception ex)
        {
            var message = McpLog.Redact(ex.Message);
            DetailNote.Text = "Sign-in failed: " + message;
            Services.Log("MCP sign-in failed for " + server.Name + ": " + ex);
            MessageBox.Show(message, "MCP sign-in", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        var server = Selected;
        if (server == null) return;
        Services.Mcp.ForgetSignIn(server);
        Services.Mcp.Disconnect(server.Id);
        _lastError.Remove(server.Id);
        Services.Log("MCP · signed out of " + server.Name);
        Reload();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import MCP servers",
                Filter = "MCP configuration (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            var result = McpConfigJson.Import(File.ReadAllText(dialog.FileName));
            if (result.Servers.Count == 0)
            {
                MessageBox.Show(result.Warnings.Count == 0 ? "The file declared no servers." :
                    string.Join(Environment.NewLine, result.Warnings), "MCP import", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // An imported file is reviewed before anything runs. Enabled servers now connect when the chat opens, so an
            // entry enabled at import would start a process at the next chat load from a file nobody has looked at yet.
            foreach (var server in result.Servers) { server.Enabled = false; Services.Settings.McpServers.Add(server); }
            Services.Save();
            _selectedId = result.Servers[result.Servers.Count - 1].Id;
            Reload();
            MessageBox.Show("Imported " + result.Servers.Count +
                            (result.Servers.Count == 1 ? " server." : " servers.") +
                            " Nothing was connected or launched; review each entry and enable it.",
                "MCP import");
            if (result.Warnings.Count > 0)
                MessageBox.Show(string.Join(Environment.NewLine, result.Warnings), "MCP import warnings",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Services.Log("MCP import failed: " + ex);
            MessageBox.Show("Could not import the file.\n\n" + ex.Message, "MCP import", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export MCP servers",
                FileName = "mcp.json",
                Filter = "MCP configuration (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;
            // Secrets are left out: an exported file is pasted into a ticket or another client, and the whole point
            // of the DPAPI store is that the value does not leave this account.
            File.WriteAllText(dialog.FileName,
                McpConfigJson.Export(Services.Settings.McpServers, includeSecrets: false),
                new UTF8Encoding(false));
            MessageBox.Show("Exported " + Services.Settings.McpServers.Count + " server(s) without secrets.",
                "MCP export");
        }
        catch (Exception ex)
        {
            Services.Log("MCP export failed: " + ex);
            MessageBox.Show("Could not export the servers.\n\n" + ex.Message, "MCP export", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static async Task<string?> SolutionRootAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = (DTE?)await LiteLlmPackage.Instance.GetServiceAsync(typeof(DTE));
            var fullName = dte?.Solution?.FullName;
            if (string.IsNullOrWhiteSpace(fullName)) return null;
            var directory = Path.GetDirectoryName(fullName);
            return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) ? null : directory;
        }
        catch (Exception) { return null; }
    }

    private sealed class ServerRow
    {
        internal ServerRow(McpServerConfiguration server, McpServerStatus? status, string? error)
        {
            Server = server;
            Transport = server.Kind == McpTransportKind.Stdio ? "stdio" : server.Kind == McpTransportKind.Sse ? "sse" : "http";
            State = !server.Enabled
                ? "Disabled"
                : status != null && status.Connected
                    ? "Connected · " + status.Era + " " + status.Version + " · " + status.Tools +
                      (status.Tools == 1 ? " tool" : " tools")
                    : error != null
                        ? "Not connected · " + error
                        : server.ValidationError() ?? "Not connected";
            Claude = server.ExposeToClaude ? "Claude: exposed" : "Claude: not exposed";
        }

        internal McpServerConfiguration Server { get; }
        public string Name => Server.Name;
        public string Transport { get; }
        public string State { get; }
        public string Claude { get; }
        /// <summary>Read by the row's checkbox. The configuration is the source of truth, so the row reads it rather
        /// than keeping a copy that a re-render could put back out of step.</summary>
        public bool Enabled => Server.Enabled;
        /// <summary>A disabled server has nothing to restart; the checkbox is what starts it.</summary>
        public bool CanRefresh => Server.Enabled;
    }

    private sealed class Row
    {
        internal Row(string name, string description) { Name = name; Description = description; }
        public string Name { get; }
        public string Description { get; }
    }
}
