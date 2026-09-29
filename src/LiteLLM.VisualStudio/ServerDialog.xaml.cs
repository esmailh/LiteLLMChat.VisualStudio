using System;
using System.Windows;
using LiteLLM.Core;

namespace LiteLLM.VisualStudio;
public partial class ServerDialog : Window
{
    private readonly ServerConfiguration _server; private readonly ApiKeyProtector _protector = new ApiKeyProtector();
    internal ServerDialog(ServerConfiguration server) { InitializeComponent(); VsTheme.ApplyToDialog(this); _server = server; NameBox.Text = server.Name; UrlBox.Text = server.BaseUrl; EnabledBox.IsChecked = server.Enabled; }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || !Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) { MessageBox.Show(this, "Enter a name and an absolute HTTP or HTTPS URL.", "LiteLLM", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _server.Name = NameBox.Text.Trim(); _server.BaseUrl = UrlBox.Text.Trim().TrimEnd('/'); _server.Enabled = EnabledBox.IsChecked == true; if (KeyBox.Password.Length > 0) _server.ProtectedApiKey = _protector.Protect(KeyBox.Password); DialogResult = true;
    }
}
