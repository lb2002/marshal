#nullable enable
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>What one line of a checklist is: a step, or a fold for the steps it does not show.</summary>
    public enum PlanLineKind { Step, FoldBefore, FoldAfter }

    /// <summary>One line of an agent's checklist as it is drawn.</summary>
    public readonly struct PlanLine
    {
        public readonly PlanLineKind Kind;
        /// <summary>The step's text (untrusted, plain), or the fold's words ("3 erledigt", "2 weitere").</summary>
        public readonly string Text;
        /// <summary>The step's state (a fold: Pending).</summary>
        public readonly PlanStatus Status;
        /// <summary>The step's index in the plan; -1 for a fold.</summary>
        public readonly int Index;

        public PlanLine(PlanLineKind kind, string text, PlanStatus status, int index)
        {
            Kind = kind;
            Text = text;
            Status = status;
            Index = index;
        }

        public override string ToString() => Kind == PlanLineKind.Step ? Status + ":" + Text : Text;
    }

    /// <summary>
    /// An agent's plan as the overview and the conversation show it (U6): the done count of all steps (the ones the bridge
    /// cut with `more` count as not done), the current step (the one in progress, else the next pending one), and the
    /// checklist window: at most 6 lines; with more steps, the window around the current step, the rest folded into one
    /// line each, "3 erledigt" above and "2 weitere" below. Pure.
    /// </summary>
    public static class PlanLines
    {
        public const int MaxLines = 6;

        public static int Done(AgentPlan plan)
        {
            var n = 0;
            foreach (var s in plan.Steps)
                if (s.Status == PlanStatus.Done) n++;
            return n;
        }

        /// <summary>All steps, the cut ones (`more`) included.</summary>
        public static int Total(AgentPlan plan) => plan.Steps.Count + plan.More;

        /// <summary>The step in progress, else the first pending one; -1 when every listed step is done.</summary>
        public static int Current(AgentPlan plan)
        {
            for (var i = 0; i < plan.Steps.Count; i++)
                if (plan.Steps[i].Status == PlanStatus.InProgress) return i;
            for (var i = 0; i < plan.Steps.Count; i++)
                if (plan.Steps[i].Status == PlanStatus.Pending) return i;
            return -1;
        }

        /// <summary>"3 / 5" (the overview's count).</summary>
        public static string Count(AgentPlan plan) => Done(plan) + " / " + Total(plan);

        /// <summary>"3/5" (the list row's count).</summary>
        public static string ShortCount(AgentPlan plan) => Done(plan) + "/" + Total(plan);

        public const string AllDone = "all done";

        /// <summary>"Plan 3 / 5: Alte Seite löschen": the current step; "Plan 5 / 5: alles erledigt" when none is left.</summary>
        public static string Headline(AgentPlan plan)
        {
            var c = Current(plan);
            return "Plan " + Count(plan) + ": " + (c >= 0 ? plan.Steps[c].Text : AllDone);
        }

        /// <summary>The fold above: "3 erledigt" when every step it hides is done, else "3 weitere".</summary>
        public static string FoldBeforeText(int count, bool allDone) => allDone ? count + " done" : More(count);

        /// <summary>"2 weitere", "1 weiterer".</summary>
        public static string More(int count) => "+" + count + " more";

        /// <summary>The checklist's lines: every step when they fit in `maxLines`, else the window around the current step.</summary>
        public static List<PlanLine> Window(AgentPlan plan, int maxLines = MaxLines)
        {
            var lines = new List<PlanLine>();
            var n = plan.Steps.Count;
            var more = plan.More;
            if (n == 0 || maxLines < 3) return lines;
            var focus = Current(plan);
            if (focus < 0) focus = n - 1; // all done: the end of the list

            // the start that shows the most steps with the current one inside, one step of context before it if it can
            int bestStart = 0, bestCount = -1;
            for (var s = 0; s <= focus; s++)
            {
                var k = maxLines - (s > 0 ? 1 : 0);
                if (s + k < n || more > 0) k--; // the fold below
                if (k > n - s) k = n - s;
                if (k <= 0 || focus >= s + k) continue;
                var better = k > bestCount || (k == bestCount && System.Math.Abs(focus - 1 - s) < System.Math.Abs(focus - 1 - bestStart));
                if (!better) continue;
                bestStart = s;
                bestCount = k;
            }
            if (bestCount <= 0) return lines;

            if (bestStart > 0)
            {
                var allDone = true;
                for (var i = 0; i < bestStart; i++) allDone &= plan.Steps[i].Status == PlanStatus.Done;
                lines.Add(new PlanLine(PlanLineKind.FoldBefore, FoldBeforeText(bestStart, allDone), PlanStatus.Done, -1));
            }
            for (var i = bestStart; i < bestStart + bestCount; i++)
                lines.Add(new PlanLine(PlanLineKind.Step, plan.Steps[i].Text, plan.Steps[i].Status, i));
            var after = n - bestStart - bestCount + more;
            if (after > 0) lines.Add(new PlanLine(PlanLineKind.FoldAfter, More(after), PlanStatus.Pending, -1));
            return lines;
        }
    }
}
