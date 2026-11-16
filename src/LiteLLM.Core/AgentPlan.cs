using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace LiteLLM.Core;

public sealed class AgentPlanStep
{
    public string Step { get; set; } = "";
    public string Status { get; set; } = "pending";
}

/// <summary>Model-reported progress, never a verification result or a permission grant.</summary>
public sealed class AgentPlan
{
    public string Explanation { get; set; } = "";
    public List<AgentPlanStep> Steps { get; set; } = new List<AgentPlanStep>();

    public static bool TryRead(string json, out AgentPlan plan, out string error)
    {
        plan = new AgentPlan();
        if (!ToolArguments.TryParse(json, out var args, out error) ||
            !ToolArguments.TryText(args, "explanation", out var explanation, out error)) return false;
        if (explanation.Length > 4000) { error = "explanation exceeds 4000 characters."; return false; }
        if (!(args["plan"] is JArray steps) || steps.Count == 0 || steps.Count > 50)
        { error = "plan must contain between 1 and 50 steps."; return false; }
        var active = 0;
        foreach (var item in steps)
        {
            if (!(item is JObject step) || !ToolArguments.TryText(step, "step", out var text, out error) ||
                !ToolArguments.TryText(step, "status", out var status, out error))
            { if (error.Length == 0) error = "Every step must be an object."; return false; }
            if (string.IsNullOrWhiteSpace(text) || text.Length > 4000)
            { error = "Each step requires 1–4000 characters."; return false; }
            if (status != "pending" && status != "in_progress" && status != "completed")
            { error = "status must be pending, in_progress or completed."; return false; }
            if (status == "in_progress" && ++active > 1)
            { error = "Only one step can be in_progress."; return false; }
            plan.Steps.Add(new AgentPlanStep { Step = text, Status = status });
        }
        plan.Explanation = explanation;
        return true;
    }
}
