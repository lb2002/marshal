#nullable enable

namespace Lotse.App
{
    /// <summary>
    /// The overview's pages (U6): 4 columns per page, in the agent list's order; more agents page with two small buttons
    /// ("1 / 2"). The page stays where the user put it and is pulled back only when it no longer exists. Pure.
    /// </summary>
    public sealed class ColumnPages
    {
        public const int PerPage = 5;

        int _count;

        /// <summary>The page shown (0-based).</summary>
        public int Page { get; private set; }

        public int Pages => PagesFor(_count);

        public static int PagesFor(int count) => count <= 0 ? 1 : (count + PerPage - 1) / PerPage;

        /// <summary>The number of agents changed: the page is kept, clamped to the last one.</summary>
        public void Update(int count)
        {
            _count = count < 0 ? 0 : count;
            if (Page > Pages - 1) Page = Pages - 1;
        }

        /// <summary>The first agent's index on this page, and how many are on it.</summary>
        public int First => Page * PerPage;

        public int OnPage => System.Math.Max(0, System.Math.Min(PerPage, _count - First));

        public bool CanPrevious => Page > 0;
        public bool CanNext => Page < Pages - 1;

        /// <summary>The page that holds item `index` (the open row's agent is always on screen).</summary>
        public void Reveal(int index)
        {
            if (index < 0 || index >= _count) return;
            Page = index / PerPage;
        }

        public void Next()
        {
            if (CanNext) Page++;
        }

        public void Previous()
        {
            if (CanPrevious) Page--;
        }

        public string Label => OverviewText.Page(Page, Pages);

        public void Reset()
        {
            Page = 0;
            _count = 0;
        }
    }
}
