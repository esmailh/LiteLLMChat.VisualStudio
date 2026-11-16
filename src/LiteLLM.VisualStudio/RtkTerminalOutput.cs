using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LiteLLM.Core;

namespace LiteLLM.VisualStudio;

internal static class RtkTerminalOutput
{
    internal static Task<RtkOutput> CompressAsync(string command, CommandExecutionResult result, CancellationToken token) =>
        RtkOutput.CompressAsync(command, result, LiteLlmPackage.Instance.Services.Settings.RtkOutputMode,
            RtkExecutable.Resolve,
            (exe, filter, input, ct) => CommandExecutor.FilterAsync(exe, filter, input, Path.GetTempPath(), ct), token);
}
