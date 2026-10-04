using System;
using System.Windows;
using LiteLLM.Core;

namespace LiteLLM.VisualStudio;
public partial class ServerDialog : Window
{
    private readonly ServerConfiguration _server; private readonly ApiKeyProtector _protector = new ApiKeyProtector();
    internal ServerDialog(ServerConfiguration server)
    {
        _server = server;
        InitializeComponent();
        // Nothing below may throw out of the constructor: a dialog that fails while loading its fields is opened from a
        // Visual Studio tool window, and an exception there takes the whole IDE down with it.
        try
        {
            VsTheme.ApplyToDialog(this);
            NameBox.Text = server.Name;
            UrlBox.Text = server.BaseUrl;
            EnabledBox.IsChecked = server.Enabled;
            DeploymentBox.Text = server.Deployment;
            ApiVersionBox.Text = server.ApiVersion;
            // Matched by label rather than by SelectedValue: the items are ComboBoxItem objects, so a string value never
            // equals one and the box would silently fall back to the first provider.
            ProviderBox.SelectedIndex = 0;
            for (var i = 0; i < ProviderBox.Items.Count; i++)
                if (string.Equals((ProviderBox.Items[i] as System.Windows.Controls.ComboBoxItem)?.Content?.ToString(), server.Provider, StringComparison.OrdinalIgnoreCase)) { ProviderBox.SelectedIndex = i; break; }
            UpdateAzureFields();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("ServerDialog init: " + ex); }
    }
    private void Provider_Changed(object sender, RoutedEventArgs e) { UpdateAzureFields(); }
    private void UpdateAzureFields() { var azure = (ProviderBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() == "Azure OpenAI"; DeploymentLabel.Visibility = ApiVersionLabel.Visibility = DeploymentBox.Visibility = ApiVersionBox.Visibility = azure ? Visibility.Visible : Visibility.Collapsed; }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || !Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) { MessageBox.Show(this, "Enter a name and an absolute HTTP or HTTPS URL.", "LiteLLM", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try
        {
            // The key is protected first: if the machine refuses to protect it the connection is left exactly as it was
            // rather than half-written, which is the state that used to leave a server pointing at a key of "".
            var protectedKey = KeyBox.Password.Length > 0 ? _protector.Protect(KeyBox.Password) : _server.ProtectedApiKey;
            _server.Name = NameBox.Text.Trim(); _server.BaseUrl = UrlBox.Text.Trim().TrimEnd('/'); _server.Provider = (ProviderBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "OpenAI Compatible"; _server.Deployment = DeploymentBox.Text.Trim(); _server.ApiVersion = ApiVersionBox.Text.Trim(); _server.Enabled = EnabledBox.IsChecked == true; _server.ProtectedApiKey = protectedKey;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The connection could not be saved.\n\n" + ex.Message, "AI provider", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        DialogResult = true;
    }
}
