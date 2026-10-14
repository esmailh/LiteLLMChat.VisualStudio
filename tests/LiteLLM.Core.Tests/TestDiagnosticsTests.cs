using System;
using LiteLLM.Core;
using Xunit;

namespace LiteLLM.Core.Tests;

/// <summary>Reading a test run's result out of the runner's console output. The samples below are captured
/// from a real <c>vstest.console.exe</c> run against this repository's own test project — one clean, one with
/// a failing and a skipped test — because the shape is the part that has to be right: a parser written
/// against a remembered format agrees with itself and with nothing else.</summary>
public sealed class TestDiagnosticsTests
{
    /// <summary>A real passing run at the verbosity the extension asks for.</summary>
    private const string Passing =
        "VSTest version 18.11.0 (x64)\n" +
        "\n" +
        "Starting test execution, please wait...\n" +
        "A total of 1 test files matched the specified pattern.\n" +
        "\n" +
        "Passed!  - Failed:     0, Passed:   207, Skipped:     0, Total:   207, Duration: 562 ms - LiteLLM.Core.Tests.dll (net48)\n";

    /// <summary>A real failing run, same verbosity, with one failure of each kind and one skipped test.</summary>
    private const string Failing =
        "VSTest version 18.11.0 (x64)\n" +
        "\n" +
        "Starting test execution, please wait...\n" +
        "A total of 1 test files matched the specified pattern.\n" +
        "[xUnit.net 00:00:00.52]     LiteLLM.Core.Tests.TempPhase6ProbeTests.Probe_IsSkippedOnPurpose [SKIP]\n" +
        "[xUnit.net 00:00:00.58]     LiteLLM.Core.Tests.TempPhase6ProbeTests.Probe_ThrowsOnPurpose [FAIL]\n" +
        "  Skipped LiteLLM.Core.Tests.TempPhase6ProbeTests.Probe_IsSkippedOnPurpose [1 ms]\n" +
        "  Failed LiteLLM.Core.Tests.TempPhase6ProbeTests.Probe_ThrowsOnPurpose [7 ms]\n" +
        "  Error Message:\n" +
        "   System.InvalidOperationException : a deliberate exception message\n" +
        "  Stack Trace:\n" +
        "     at LiteLLM.Core.Tests.TempPhase6ProbeTests.Probe_ThrowsOnPurpose() in F:\\repo\\tests\\LiteLLM.Core.Tests\\TempPhase6ProbeTests.cs:line 18\n" +
        "[xUnit.net 00:00:00.74]     LiteLLM.Core.Tests.TempPhase6ProbeTests.Probe_FailsOnPurposeWithAMultiLineAssertion [FAIL]\n" +
        "  Failed LiteLLM.Core.Tests.TempPhase6ProbeTests.Probe_FailsOnPurposeWithAMultiLineAssertion [13 ms]\n" +
        "  Error Message:\n" +
        "   Assert.Equal() Failure: Strings differ\n" +
        "           \u2193 (pos 0)\n" +
        "Expected: \"the expected value\"\n" +
        "Actual:   \"something else entirely\"\n" +
        "           \u2191 (pos 0)\n" +
        "  Stack Trace:\n" +
        "     at LiteLLM.Core.Tests.TempPhase6ProbeTests.Probe_FailsOnPurposeWithAMultiLineAssertion() in F:\\repo\\tests\\LiteLLM.Core.Tests\\TempPhase6ProbeTests.cs:line 12\n" +
        "\n" +
        "Failed!  - Failed:     2, Passed:   207, Skipped:     1, Total:   210, Duration: 618 ms - LiteLLM.Core.Tests.dll (net48)\n";

    /// <summary>The other shape: one key per line, which is what the runner prints when it is not asked for the
    /// pair line. Supported because it is real output too, not because anything here depends on it.</summary>
    private const string Block =
        "Microsoft (R) Test Execution Command Line Tool Version 17.14.1 (x64)\n" +
        "Starting test execution, please wait...\n" +
        "\n" +
        "Test Run Failed.\n" +
        "Total tests: 210\n" +
        "     Passed: 207\n" +
        "     Failed: 2\n" +
        "    Skipped: 1\n" +
        " Total time: 1.1658 Seconds\n";

    [Fact]
    public void TestDiagnostics_ReadsACleanRun()
    {
        var report = TestDiagnostics.Summarize(Passing);

        Assert.NotNull(report);
        Assert.Equal(0, report!.Errors);
        Assert.Equal(0, report.Warnings);
        Assert.Equal("Tests · all 207 passed", report.Summary);
        Assert.Equal("", report.Detail);
    }

    [Fact]
    public void TestDiagnostics_ReadsAFailingRunAndKeepsTheFailureText()
    {
        var report = TestDiagnostics.Summarize(Failing);

        Assert.NotNull(report);
        Assert.Equal(2, report!.Errors);
        Assert.Equal(1, report.Warnings);
        Assert.Equal("Tests · 2 failed, 207 passed, 1 skipped", report.Summary);
        // What failed and why is the whole of what the model is given, so both failures have to be in it.
        Assert.Contains("Probe_ThrowsOnPurpose", report.Detail, StringComparison.Ordinal);
        Assert.Contains("a deliberate exception message", report.Detail, StringComparison.Ordinal);
        Assert.Contains("Probe_FailsOnPurposeWithAMultiLineAssertion", report.Detail, StringComparison.Ordinal);
        // The runner's own echo of a failure duplicates the block below it, and a stack trace is not worth
        // carrying twice.
        Assert.DoesNotContain("[xUnit.net", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Skipped LiteLLM", report.Detail, StringComparison.Ordinal);
        // The result line follows the last failure with nothing between them. Caught by running this parser
        // over a real failing run: the counts landed at the bottom of the model's failure text as if they were
        // part of the last stack trace.
        Assert.DoesNotContain("Failed!  -", report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Test Run Failed.", TestDiagnostics.Summarize(Block)!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TestDiagnostics_NeverClaimsMoreFailuresThanItShows()
    {
        // Five failing tests with long messages, against a cap that only fits a few: the note has to say how
        // many it left out, because a list that silently stops reads as the whole of the failures.
        var output = new System.Text.StringBuilder("Failed!  - Failed:    40, Passed:     1, Skipped:     0, Total:    41, Duration: 1 s - a.dll (net48)\n");
        for (var i = 0; i < 40; i++)
        {
            output.Append("  Failed Some.Test.Number").Append(i).Append(" [4 ms]\n");
            output.Append("  Error Message:\n   ").Append(new string('x', 120)).Append('\n');
            output.Append("  Stack Trace:\n     at Some.Test.Number").Append(i).Append("()\n");
        }

        var report = TestDiagnostics.Summarize(output.ToString(), 2000);
        Assert.NotNull(report);
        Assert.Equal(40, report!.Errors);

        var shown = 0;
        foreach (var line in report.Detail.Split('\n'))
            if (line.TrimStart().StartsWith("Failed Some.Test.Number", StringComparison.Ordinal)) shown++;
        Assert.True(shown > 0, "no failure was shown at all");
        Assert.True(shown <= TestDiagnostics.MaxFailures, "more failures were shown than the cap allows");
        Assert.Contains(shown + " of 40 failing tests shown", report.Detail, StringComparison.Ordinal);
        Assert.True(report.Detail.Length <= 2000 + 40, "the detail was not bounded: " + report.Detail.Length);
    }

    [Fact]
    public void TestDiagnostics_ReadsTheOtherResultShapeToo()
    {
        var report = TestDiagnostics.Summarize(Block);

        Assert.NotNull(report);
        Assert.Equal(2, report!.Errors);
        Assert.Equal(1, report.Warnings);
        Assert.Equal("Tests · 2 failed, 207 passed, 1 skipped", report.Summary);
    }

    [Fact]
    public void TestDiagnostics_SaysNothingWhenTheRunReportedNoResult()
    {
        // A run that never started, timed out, or was handed a source that does not exist: reporting "no
        // failures" for any of these would be the one claim this must never make without knowing it.
        Assert.Null(TestDiagnostics.Summarize("vstest.console.exe : The test source file \"F:\\nope\\nothing.dll\" provided was not found.\n"));
        Assert.Null(TestDiagnostics.Summarize(""));
        Assert.Null(TestDiagnostics.Summarize(null));
    }

    [Fact]
    public void TestDiagnostics_ReadsTheResultRatherThanSomethingThatMerelyMentionsIt()
    {
        // A test named after a failure count must not be read as the count: the pair line is the only line
        // carrying all three keys, and this is what keeps that decision true.
        var output = "  Passed Some.Tests.Failed_Passed_Total_AreNotCounts [2 ms]\n" + Passing;
        var report = TestDiagnostics.Summarize(output);

        Assert.NotNull(report);
        Assert.Equal(207, Sum(report!.Summary));
    }

    [Fact]
    public void TestDiagnostics_WritesANoteThatNamesTheFailuresAndForbidsEditingTheTest()
    {
        var report = TestDiagnostics.Summarize(Failing)!;
        var note = TestDiagnostics.ToModelNote(report);

        Assert.Contains("the solution's tests were run for you", note, StringComparison.Ordinal);
        Assert.Contains("2 failing tests", note, StringComparison.Ordinal);
        Assert.Contains("1 skipped test", note, StringComparison.Ordinal);
        Assert.Contains("a deliberate exception message", note, StringComparison.Ordinal);
        // The one way this check can be satisfied without the code being fixed.
        Assert.Contains("Do not delete, skip or weaken a test", note, StringComparison.Ordinal);

        var clean = TestDiagnostics.ToModelNote(TestDiagnostics.Summarize(Passing)!);
        Assert.Contains("no failures", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void TestDiagnostics_ReportsAnUnverifiedRunAsOneLine()
    {
        Assert.Equal("Tests · not run: vstest.console.exe was not found, so the tests could not be run",
            TestDiagnostics.ToUnverifiedLine("vstest.console.exe was not found, so the tests could not be run"));
        // The row is one line, so a reason carrying a line break is folded into one.
        Assert.DoesNotContain("\n", TestDiagnostics.ToUnverifiedLine("first\nsecond"));
    }

    /// <summary>The number in a label, so a test can say which total it expected without restating the whole
    /// line.</summary>
    private static int Sum(string line)
    {
        var digits = "";
        foreach (var character in line)
        {
            if (char.IsDigit(character)) { digits += character; continue; }
            if (digits.Length > 0) break;
        }
        return digits.Length == 0 ? -1 : int.Parse(digits);
    }
}
