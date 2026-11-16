#nullable enable

namespace Lotse.App
{
    /// <summary>
    /// The agent list's scroll (U5a): at most `visible` rows show; more scroll with the feed's drag logic (the content
    /// follows the hand, one row per row height dragged). Top is the first row shown. Pure.
    /// </summary>
    public sealed class ListScroll
    {
        float _drag;
        int _count;

        public ListScroll(int visible)
        {
            Visible = visible;
        }

        public int Visible { get; }
        public int Top { get; private set; }

        /// <summary>The list now has `count` rows: Top stays where it is, within reach.</summary>
        public void Update(int count)
        {
            _count = count;
            Top = Clamp(Top);
        }

        /// <summary>Moves by whole rows: positive shows rows further down.</summary>
        public void Scroll(int rows) => Top = Clamp(Top + rows);

        /// <summary>A drag of `dyMeters` (up positive) with rows `stepMeters` tall: the content follows the hand.</summary>
        public void Drag(float dyMeters, float stepMeters)
        {
            if (stepMeters <= 0f) return;
            _drag += dyMeters / stepMeters;
            var whole = (int)_drag; // toward zero
            if (whole == 0) return;
            _drag -= whole;
            Scroll(whole);
        }

        public void EndDrag() => _drag = 0f;

        /// <summary>Scrolls just enough that row `index` shows.</summary>
        public void Reveal(int index)
        {
            if (index < Top) Top = Clamp(index);
            else if (index >= Top + Visible) Top = Clamp(index - Visible + 1);
        }

        int Clamp(int top)
        {
            var max = _count > Visible ? _count - Visible : 0;
            return top < 0 ? 0 : top > max ? max : top;
        }
    }
}
