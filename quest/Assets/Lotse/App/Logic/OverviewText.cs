#nullable enable
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>
    /// The overview's own words (U6): the header's summary line ("4 Agenten, 1 wartet auf dich, 11 von 19 Schritten
    /// erledigt"; the steps part only when an agent has a plan, the waiting part only when one waits), the tab names, the
    /// column's bottom line while its agent waits ("Needs you: " and the notice's short text), the page label. Pure.
    /// </summary>
    public static class OverviewText
    {
        public const string OverviewTab = "Overview";
        public const string ConversationTab = "Conversation";
        public const string Collapse = "Fold away";
        public const string WaitsPrefix = "Needs you: ";
        public const string AutoCollapse = "Fold away by itself";
        /// <summary>A card without a plan (U9): the count's place, and the caption over its latest activity lines.</summary>
        public const string NoPlan = "no plan";
        public const string Holding = "Holding";
        public const string Landed = "Landed";
        public const string Stopped = "Stopped";
        public const string Failed = "Failed";
        public const string LandedNoPlan = "Done";
        /// <summary>"3 of 5": the step the agent is at.</summary>
        public static string StepOf(int step, int steps) => step + " of " + steps;
        /// <summary>"All 4 steps done" above a landed agent's dot.</summary>
        public static string AllDone(int steps) => steps == 1 ? "The one step is done" : "All " + steps + " steps done";
        public const string RecentActivity = "Recent activity";

        /// <param name="agents">Every agent.</param>
        /// <param name="needsYou">Whether an agent needs the user (AgentOrder's group).</param>
        public static string Summary(IReadOnlyList<AgentState> agents, System.Func<AgentState, bool> needsYou)
        {
            var waiting = 0;
            int done = 0, total = 0;
            var anyPlan = false;
            foreach (var a in agents)
            {
                if (needsYou(a)) waiting++;
                if (a.Plan == null) continue;
                anyPlan = true;
                done += PlanLines.Done(a.Plan);
                total += PlanLines.Total(a.Plan);
            }
            var line = Texts.AgentCount(agents.Count);
            if (waiting > 0) line += ", " + waiting + (waiting == 1 ? " needs you" : " need you");
            if (anyPlan) line += ", " + done + " of " + total + (total == 1 ? " step done" : " steps done");
            return line;
        }

        /// <summary>The column's bottom line while its agent has an open request: "Wartet auf dich: Claude ändert die Datei".</summary>
        public static string Waits(string headline) => WaitsPrefix + headline;

        /// <summary>"1 / 2".</summary>
        public static string Page(int page, int pages) => (page + 1) + " / " + pages;
    }
}
