using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteLLM.Core;

public sealed class PlanStep
{
    public string Instruction { get; set; } = "";
    public Guid? TurnId { get; set; }
    public bool Accepted { get; set; }
}

public static class ExecutionPlan
{
    public static List<PlanStep> Parse(string text) => (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim()).Where(line => line.Length > 0).Take(50)
        .Select(line => new PlanStep { Instruction = line }).ToList();

    public static string Prompt(IReadOnlyList<PlanStep> steps, int index)
    {
        if (index < 0 || index >= steps.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (steps[index].Accepted) throw new InvalidOperationException("This step has already been accepted.");
        return "Execute only the selected step of this user-approved plan. Other steps are context, not authorization. " +
            "If a material change of plan is needed, explain it and stop for the user. Existing tool approvals still apply.\n\n" +
            string.Join("\n", steps.Select((s, i) => (i + 1) + ". " + s.Instruction)) +
            "\n\nSelected step " + (index + 1) + ": " + steps[index].Instruction;
    }
}
