using System;
using Xunit;

namespace LiteLLM.Core.Tests;

public sealed class WorkspaceFeatureTests
{
    [Theory]
    [InlineData(true, "after", true, "after", true)]
    [InlineData(true, "after", true, "user edit", false)]
    [InlineData(true, "after", false, "", false)]
    [InlineData(false, "", true, "new user file", false)]
    [InlineData(false, "", false, "", true)]
    public void CheckpointsDoNotOverwriteUntrackedWork(bool expectedExists, string expected, bool exists, string current, bool allowed) =>
        Assert.Equal(allowed, CheckpointGuard.CanRestore(expectedExists, expected, exists, current));

    [Fact]
    public void PlanExecutionIsExplicitAndCompletedStepsCannotRunAgain()
    {
        var plan = ExecutionPlan.Parse("Read files\n\nFix bug\nRun tests");
        Assert.Equal(3, plan.Count);
        Assert.Contains("Selected step 2: Fix bug", ExecutionPlan.Prompt(plan, 1));
        Assert.Contains("Other steps are context, not authorization", ExecutionPlan.Prompt(plan, 1));
        plan[1].Accepted = true;
        Assert.Throws<InvalidOperationException>(() => ExecutionPlan.Prompt(plan, 1));
    }
}
