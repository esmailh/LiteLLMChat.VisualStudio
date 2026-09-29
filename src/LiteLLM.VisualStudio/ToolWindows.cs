using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace LiteLLM.VisualStudio;
[Guid("90EF6471-C051-4B25-B36D-A84E85D7F5ED")]
public sealed class ChatToolWindow : ToolWindowPane { public ChatToolWindow() : base(null) { Caption = "LiteLLM Chat"; Content = new ChatControl(); } }
[Guid("0C17B6BF-26E8-4477-A29D-B1C37CE1F651")]
public sealed class DashboardToolWindow : ToolWindowPane { public DashboardToolWindow() : base(null) { Caption = "LiteLLM Dashboard"; Content = new DashboardControl(); } }
