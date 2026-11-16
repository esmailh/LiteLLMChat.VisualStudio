using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LiteLLM.Core;

namespace LiteLLM.VisualStudio;

public partial class ChatControl
{
    private void RefreshPlanSection()
    {
        var session = Services.ActiveSession;
        var panel = new StackPanel { Margin = new Thickness(12) };
        PlanSection.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var agentTurn = session.Turns.LastOrDefault(t => t.AgentPlan != null);
        if (agentTurn?.AgentPlan != null)
        {
            var agentPlan = agentTurn.AgentPlan;
            panel.Children.Add(new TextBlock { Text = "Agent plan · " + agentTurn.Status + " · model-reported, not verified", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (agentPlan.Explanation.Length > 0)
            {
                var explanation = PlanText(agentPlan.Explanation, false);
                explanation.Margin = new Thickness(0, 4, 0, 6);
                panel.Children.Add(explanation);
            }
            foreach (var step in agentPlan.Steps)
                panel.Children.Add(PlanStepRow(step.Step,
                    step.Status == "completed" ? "✓" : step.Status == "in_progress" ? "▶" : "○",
                    step.Status == "in_progress"));
            panel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10) });
        }
        panel.Children.Add(new TextBlock { Text = "Your manual plan", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "One step per line (maximum 50). Save edits, then explicitly execute a selected step. Finishing a turn is not proof a step succeeded: review its evidence and accept it yourself.", TextWrapping = TextWrapping.Wrap });
        var editor = new TextBox { Text = string.Join("\n", session.Plan.Select(s => s.Instruction)), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 8) }; panel.Children.Add(editor);
        editor.FlowDirection = TextDirection.For(editor.Text);
        editor.TextChanged += (s, e) => editor.FlowDirection = TextDirection.For(editor.Text);
        var list = new ListBox { Height = 190, HorizontalContentAlignment = HorizontalAlignment.Stretch }; panel.Children.Add(list);
        void Refresh()
        {
            list.ItemsSource = session.Plan.Select((step, i) => PlanStepRow(step.Instruction,
                (step.Accepted ? "✓ " : step.TurnId.HasValue ? "Review " : "○ ") + (i + 1), false, selectable: false)).ToList();
            PlanSection.Header = agentTurn?.AgentPlan != null
                ? "Agent plan · " + agentTurn.AgentPlan.Steps.Count(s => s.Status == "completed") + "/" + agentTurn.AgentPlan.Steps.Count + " · " + agentTurn.Status
                : "Plan · " + session.Plan.Count(s => s.Accepted) + "/" + session.Plan.Count + " steps";
        }
        var actions = new WrapPanel(); panel.Children.Add(actions);
        void Add(string title, Action action) { var b = new Button { Content = title, Margin = new Thickness(3) }; b.Click += (s, args) => { if (_generation != null || !ReferenceEquals(session, Services.ActiveSession)) { MessageBox.Show("Wait for the active turn and keep this conversation selected."); return; } try { action(); Refresh(); SaveSession(); } catch (Exception ex) { MessageBox.Show(ex.Message, "Plan"); } }; actions.Children.Add(b); }
        Add("Save / approve revised plan", () => { if (MessageBox.Show("Approve this plan? Replacing it resets step review links.", "Plan", MessageBoxButton.YesNo) == MessageBoxResult.Yes) session.Plan = ExecutionPlan.Parse(editor.Text); });
        Add("Execute selected step", () =>
        {
            var index = list.SelectedIndex; if (index < 0) return;
            if (Composer.Text.Length > 0) throw new InvalidOperationException("Keep your composer draft before executing a plan step.");
            var step = session.Plan[index];
            Composer.Text = ExecutionPlan.Prompt(session.Plan, index);
            _ = ExecutePlanStepAsync(session, step);
        });
        Add("Accept reviewed step", () => { if (list.SelectedIndex < 0) return; var step = session.Plan[list.SelectedIndex]; if (!step.TurnId.HasValue) throw new InvalidOperationException("Execute the step before accepting it."); if (MessageBox.Show("Have you reviewed the linked turn's tools, changes and verification?", "Accept step", MessageBoxButton.YesNo) == MessageBoxResult.Yes) step.Accepted = true; });
        Add("Open linked turn", () => { if (list.SelectedIndex < 0) return; var id = session.Plan[list.SelectedIndex].TurnId; var turn = session.Turns.FirstOrDefault(t => t.Id == id); if (turn != null) ShowTurnEvidence(turn); });
        Refresh();
        PlanSection.Content = new ScrollViewer
        {
            Content = panel, MaxHeight = 330,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }
    private static FrameworkElement PlanStepRow(string text, string marker, bool active, bool selectable = true)
    {
        // The row is laid out in the step's own direction, so the marker sits where the step starts and
        // the text stays right-aligned the way a reply is. The marker is a cell of its own, outside the
        // text's bidi scope, so a filename or a bracket at either end cannot drag it across the row.
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Stretch, FlowDirection = TextDirection.For(text) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var glyph = new TextBlock { Text = marker, Margin = new Thickness(6, 0, 6, 0), FlowDirection = FlowDirection.LeftToRight };
        var body = PlanText(text, active);
        // A row in the manual plan's list is a target to pick, not a passage to copy: a selectable box
        // would take the click that selects the row.
        if (!selectable) body.IsHitTestVisible = false;
        Grid.SetColumn(glyph, 0); Grid.SetColumn(body, 1);
        row.Children.Add(glyph); row.Children.Add(body);
        return row;
    }
    private static FrameworkElement PlanText(string text, bool active)
    {
        // A plan step is drawn by the transcript's renderer, so it wraps, aligns and can be copied the
        // way a reply is. The element takes the step's direction as a whole, and each paragraph keeps
        // its own, so a line written in English does not follow a Persian one to the right.
        var body = MarkdownRenderer.Render(text);
        body.FlowDirection = TextDirection.For(text);
        if (active) SetWeight(body, FontWeights.SemiBold);
        return body;
    }
    /// <summary>The step in progress is the semibold one. A rendered reply carries its text properties on
    /// the selectable boxes rather than on the panel, so the weight is set where the text actually is.</summary>
    private static void SetWeight(FrameworkElement element, FontWeight weight)
    {
        if (element is Control control) control.FontWeight = weight;
        if (element is Panel panel) foreach (FrameworkElement child in panel.Children) SetWeight(child, weight);
    }
    private async System.Threading.Tasks.Task ExecutePlanStepAsync(ChatSessionState session, PlanStep step)
    {
        var before = session.Turns.Count;
        await SendAsync();
        if (session.Turns.Count > before) step.TurnId = session.Turns[before].Id;
        SaveSession();
        RefreshPlanSection();
    }
    private void ShowTurnEvidence(ChatTurnState turn)
    {
        var text = string.Join("\n\n", turn.Messages.Select(m => m.Role + (m.ToolName.Length > 0 ? " · " + m.ToolName : "") + "\n" + m.Content));
        if (turn.Build != null) text += "\n\nBuild: " + turn.Build.Summary + "\n" + turn.Build.Detail;
        if (turn.Tests != null) text += "\n\nTests: " + turn.Tests.Summary + "\n" + turn.Tests.Detail;
        Messages.Children.Add(new Expander { Header = "Step evidence · " + turn.Id, Content = new TextBox { Text = text, FlowDirection = TextDirection.For(text), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, IsExpanded = true });
    }
}
