#nullable enable

namespace Lotse.App
{
    /// <summary>The rail's orbs (U6): one per agent in the list's order, at most 7, then "+2" for the rest. Pure.</summary>
    public static class RailSlots
    {
        public const int MaxOrbs = 7;

        public static int Shown(int agents) => agents < 0 ? 0 : agents > MaxOrbs ? MaxOrbs : agents;

        /// <summary>"+2", or "" when every agent has its orb.</summary>
        public static string Extra(int agents) => agents > MaxOrbs ? "+" + (agents - MaxOrbs) : "";
    }
}
