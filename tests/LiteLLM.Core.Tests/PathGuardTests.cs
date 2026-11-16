using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class PathGuardTests
{
    [Fact]
    public void RefusesJunctionForExistingAndNewFiles()
    {
        var temp = Path.Combine(Path.GetTempPath(), "litellm-path-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temp, "root");
        var outside = Path.Combine(temp, "outside");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "existing.txt"), "keep");
        try
        {
            using (var process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + outside + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            {
                process!.WaitForExit();
                Assert.Equal(0, process.ExitCode);
            }
            Assert.Throws<ArgumentException>(() => PathGuard.Resolve(root, "link/existing.txt"));
            Assert.Throws<ArgumentException>(() => PathGuard.Resolve(root, "link/new.txt"));
            Assert.Equal(Path.Combine(root, "new.txt"), PathGuard.Resolve(root, "new.txt"));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(temp, true);
        }
    }
}
