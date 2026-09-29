using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LiteLLM.Core;

namespace LiteLLM.VisualStudio;
public partial class DashboardControl : UserControl
{
    private bool _narrow;
    private ExtensionServices Services => LiteLlmPackage.Instance.Services;
    internal DashboardControl() { InitializeComponent(); VsTheme.Apply(this); Loaded += (s, e) => { Services.ModelsChanged += Changed; Reload(); }; SizeChanged += OnSizeChanged; }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width < 620);
    private void ApplyLayout(bool narrow)
    {
        if (_narrow == narrow) return; _narrow = narrow;
        Grid.SetRow(HeaderActions, narrow ? 1 : 0); Grid.SetColumn(HeaderActions, narrow ? 0 : 1); Grid.SetColumnSpan(HeaderActions, narrow ? 2 : 1);
        HeaderActions.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        HeaderText.Margin = narrow ? new Thickness(0) : new Thickness(0, 0, 12, 0);
        Grid.SetRow(ModelFilters, narrow ? 1 : 0); Grid.SetColumn(ModelFilters, narrow ? 0 : 1); Grid.SetColumnSpan(ModelFilters, narrow ? 2 : 1);
        ModelFilters.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        ModelsHeaderText.Margin = narrow ? new Thickness(0) : new Thickness(0, 0, 12, 0);
        ServerFilter.Width = narrow ? double.NaN : 190; ServerFilter.MaxWidth = narrow ? 240 : double.PositiveInfinity; ServerFilter.HorizontalAlignment = HorizontalAlignment.Stretch;
        Search.Width = narrow ? double.NaN : 240; Search.MaxWidth = narrow ? 340 : double.PositiveInfinity; Search.HorizontalAlignment = HorizontalAlignment.Stretch;
    }
    private void Changed(object sender, EventArgs e) => Dispatcher.Invoke(Reload);
    private void Reload() { Servers.ItemsSource = null; Servers.ItemsSource = Services.Settings.Servers; ServerFilter.ItemsSource = new[] { "All servers" }.Concat(Services.Settings.Servers.Select(s => s.Name)); ServerFilter.SelectedIndex = Math.Max(0, ServerFilter.SelectedIndex); ReloadModels(); }
    private void ReloadModels()
    {
        if (Models == null || Search == null || ServerFilter == null) return; var query = Search.Text ?? ""; var server = ServerFilter.SelectedItem as string;
        Models.ItemsSource = Services.Catalog.GetAll(Services.Settings.Servers.Where(s => s.Enabled).Select(s => s.Id)).Where(m => (string.IsNullOrEmpty(query) || m.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) && (string.IsNullOrEmpty(server) || server == "All servers" || m.ServerName == server)).Select(m => new ModelRow(m, !Services.Settings.DisabledModelKeys.Contains(m.Key))).ToList();
    }
    private void Add_Click(object sender, RoutedEventArgs e) { var server = new ServerConfiguration(); if (new ServerDialog(server) { Owner = Window.GetWindow(this) }.ShowDialog() == true) { Services.Settings.Servers.Add(server); Services.Save(); Reload(); } }
    private void Edit_Click(object sender, RoutedEventArgs e) { if (Servers.SelectedItem is ServerConfiguration server && new ServerDialog(server) { Owner = Window.GetWindow(this) }.ShowDialog() == true) { Services.Save(); Reload(); } }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (Servers.SelectedItem is ServerConfiguration server && MessageBox.Show("Remove " + server.Name + "?", "LiteLLM", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { Services.Settings.Servers.Remove(server); Services.Save(); Reload(); } }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { await Services.RefreshAsync(CancellationToken.None); }
    private async void Test_Click(object sender, RoutedEventArgs e) { if (Servers.SelectedItem is ServerConfiguration server) try { await Services.TestAsync(server, CancellationToken.None); MessageBox.Show("Connected. Models were discovered successfully.", "LiteLLM"); } catch (Exception ex) { Services.Log(ex.ToString()); MessageBox.Show(ex.Message, "LiteLLM connection", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    private void ModelEnabled_Changed(object sender, RoutedEventArgs e) { if ((sender as CheckBox)?.DataContext is ModelRow row) { if (row.Enabled) Services.Settings.DisabledModelKeys.Remove(row.Model.Key); else if (!Services.Settings.DisabledModelKeys.Contains(row.Model.Key)) Services.Settings.DisabledModelKeys.Add(row.Model.Key); Services.Save(); } }
    private void Filter_Changed(object sender, EventArgs e) => ReloadModels(); private void Servers_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private sealed class ModelRow { internal ModelRow(ModelInfo model, bool enabled) { Model = model; Enabled = enabled; } internal ModelInfo Model { get; } public string Name => Model.Id; public string Server => Model.ServerName; public bool Enabled { get; set; } }
}
