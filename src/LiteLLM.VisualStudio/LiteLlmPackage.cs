using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace LiteLLM.VisualStudio;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("LiteLLM Chat", "Native LiteLLM chat for Visual Studio", "1.0")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(ChatToolWindow))]
[ProvideToolWindow(typeof(DashboardToolWindow))]
[ProvideOptionPage(typeof(LiteLlmOptionsPage), "LiteLLM", "General", 0, 0, true)]
[Guid("7A065EF0-9E0D-4BBC-9C0F-06D899FDBBBB")]
public sealed class LiteLlmPackage : AsyncPackage
{
    internal static LiteLlmPackage Instance { get; private set; } = null!;
    internal ExtensionServices Services { get; private set; } = null!;
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        Instance = this;
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // ExtensionServices reads the DialogPage through AsyncPackage.GetDialogPage.
        // Visual Studio shell services and DialogPage creation are UI-thread-affine.
        Services = new ExtensionServices(this);
        Services.ApplyOptions();
        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
        {
            Add(commands, 0x0100, async () => await ShowAsync(typeof(ChatToolWindow)));
            Add(commands, 0x0101, async () => await ShowAsync(typeof(DashboardToolWindow)));
            Add(commands, 0x0102, async () => await Services.RefreshAsync(CancellationToken.None));
            Add(commands, 0x0103, async () => { var server = Services.Settings.Servers.Find(s => s.Enabled); if (server != null) await Services.TestAsync(server, CancellationToken.None); });
            Add(commands, 0x0104, async () => { await ShowAsync(typeof(ChatToolWindow)); ChatControl.Current?.Clear(); });
            Add(commands, 0x0105, async () => { var text = await Services.GetSelectionAsync(); await ShowAsync(typeof(ChatToolWindow)); ChatControl.Current?.InsertContext(text); });
        }
    }
    private static void Add(OleMenuCommandService service, int id, Func<Task> action) => service.AddCommand(new MenuCommand((s, e) => ThreadHelper.JoinableTaskFactory.RunAsync(action), new CommandID(new Guid("38BB4A85-6C8E-489E-9CA4-E08258489996"), id)));
    private async Task ShowAsync(Type type) { var pane = await ShowToolWindowAsync(type, 0, true, DisposalToken); if (pane?.Frame == null) throw new NotSupportedException("Cannot create LiteLLM tool window."); }
    protected override void Dispose(bool disposing) { if (disposing && Services != null) Services.Dispose(); base.Dispose(disposing); }
}
