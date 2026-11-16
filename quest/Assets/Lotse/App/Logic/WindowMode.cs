#nullable enable

namespace Lotse.App
{
    /// <summary>The window's two tabs (U6).</summary>
    public enum WorkspaceTab { Overview, Conversation }

    /// <summary>
    /// What the window shows (U6): the tab ("Übersicht" or "Unterhaltung", the user's choice, never changed by itself), and
    /// whether it is folded into the rail. The conversation, and with it the only place an approval block can be, is on
    /// screen only in Unterhaltung and not collapsed. The window opens on Übersicht; expanding returns to the tab that was
    /// open, unless an orb in the rail was pressed (then Unterhaltung). Pure.
    /// </summary>
    public sealed class WindowMode
    {
        public WorkspaceTab Tab { get; private set; } = WorkspaceTab.Overview;
        public bool Collapsed { get; private set; }

        /// <summary>Counts every change (the views redraw on it).</summary>
        public int Version { get; private set; }

        /// <summary>The approval block's place is on screen.</summary>
        public bool ConversationShown => !Collapsed && Tab == WorkspaceTab.Conversation;

        /// <summary>A tab pressed by the user. True when it changed.</summary>
        public bool Show(WorkspaceTab tab)
        {
            if (Collapsed || tab == Tab) return false;
            Tab = tab;
            Version++;
            return true;
        }

        /// <summary>"Einklappen" or the gaze timer. True when it changed.</summary>
        public bool Collapse()
        {
            if (Collapsed) return false;
            Collapsed = true;
            Version++;
            return true;
        }

        /// <summary>Expands into the tab that was open, or into `tab` (an orb's press: Unterhaltung). True when it changed.</summary>
        public bool Expand(WorkspaceTab? tab = null)
        {
            if (!Collapsed && (tab == null || tab == Tab)) return false;
            Collapsed = false;
            if (tab != null) Tab = tab.Value;
            Version++;
            return true;
        }

        /// <summary>A new session: Übersicht, open.</summary>
        public void Reset()
        {
            Tab = WorkspaceTab.Overview;
            Collapsed = false;
            Version++;
        }
    }
}
