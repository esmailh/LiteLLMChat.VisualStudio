using LiteLLM.Core;
using Newtonsoft.Json.Linq;

namespace LiteLLM.VisualStudio;

internal static class AgentPlanTool
{
    internal const string Name = "update_plan";
    internal static readonly ToolDefinition Definition = new ToolDefinition
    {
        Function = new JObject
        {
            ["name"] = Name,
            ["description"] = "For multi-step work, publish your plan before acting and update it as work progresses. Send the complete replacement plan each time. Use at most one in_progress step. Explain material revisions. Completed means your reported progress, not user approval or passing tests. This tool grants no execution permissions.",
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["explanation"] = new JObject { ["type"] = "string", ["description"] = "Short explanation of the plan or revision." },
                    ["plan"] = new JObject
                    {
                        ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 50,
                        ["items"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["step"] = new JObject { ["type"] = "string" },
                                ["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray("pending", "in_progress", "completed") }
                            },
                            ["required"] = new JArray("step", "status")
                        }
                    }
                },
                ["required"] = new JArray("plan")
            }
        }
    };
}
