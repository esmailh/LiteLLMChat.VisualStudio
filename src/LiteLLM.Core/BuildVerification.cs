namespace LiteLLM.Core;

public enum BuildVerificationStatus { Succeeded, Failed, Cancelled, Unavailable, TimedOut }

public sealed class BuildVerification
{
    public BuildVerificationStatus Status { get; set; } = BuildVerificationStatus.Unavailable;
    public string Diagnostics { get; set; } = "";
    public string Problem { get; set; } = "";
    public bool CanRunTests => Status == BuildVerificationStatus.Succeeded && BuildDiagnostics.Summarize(Diagnostics).Errors == 0;

    public BuildReport? Report()
    {
        if (Status != BuildVerificationStatus.Succeeded && Status != BuildVerificationStatus.Failed) return null;
        var report = BuildDiagnostics.Summarize(Diagnostics);
        if (Status == BuildVerificationStatus.Failed && report.Errors == 0)
        {
            report.Errors = 1;
            report.Summary = "Build · failed (no error details available)";
            report.Detail = "Visual Studio reported a failed build. " + report.Detail;
        }
        return report;
    }
}
