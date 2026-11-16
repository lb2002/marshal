using System;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// The one window (U5a, U6; U13's layout): one opaque asphalt surface (0.62 x 0.36 m at the default height: 1180
    /// units across; its pose and size come from a WorkspaceDock), with no cards and no borders. At its top the header
    /// (WorkspaceHeader: "Marshal", the one sentence, the tools). Under it the rows (TaxiRowsView): one taxi line per
    /// agent. A row can be open: it grows in place, the others thin out, and the open agent's request or work
    /// (ConversationView) stands on it once it has (nearly) reached its height. There are no tabs. The notice pill
    /// stands on the window's top edge. Folded, the panel shrinks in 0.15 s (at once with Theme.Still) into the rail at
    /// its top corner next to the display (RailView): the content goes as the fold starts and comes back once the panel
    /// is open again (Open). It does not follow the head. Built at the default size and scaled as a whole for other
    /// heights. Its face looks along -z (toward the user).
    /// </summary>
    public sealed class WorkspaceView : MonoBehaviour
    {
        /// <summary>The size it is built at; a dock of another height scales it.</summary>
        public const float BaseHeight = WorkspaceDock.BuildHeight;
        public const float BaseWidth = BaseHeight * WorkspaceDock.Aspect;
        public const float FoldSeconds = 0.15f;
        /// <summary>The guided test's band along the bottom edge: its buttons' width.</summary>
        public const float TestButtonWidth = UiMetrics.MenuButtonWidth; // "Did not work" needs it
        public const float QuestionButtonWidth = UiMetrics.ButtonWidth;

        Transform _root;
        Transform _content;
        WorkspaceTab _tab;
        bool _collapsed;
        float _fold; // 0 open .. 1 collapsed (the panel's width between them)
        float _railHeightApplied = -1f;
        float _extentApplied = -1f;
        bool _railRight;
        float _extra; // the room between the header and the body: a question band (U9)
        float _below; // the room under the body: the guided test's band (U9)
        bool _halted;

        public WorkspaceHeader Header { get; private set; }
        /// <summary>The agents' lines; one of them can be open.</summary>
        public TaxiRowsView Rows { get; private set; }
        /// <summary>What stands on the open row: the request, or the agent's work.</summary>
        public ConversationView Conversation { get; private set; }
        public RailView Rail { get; private set; }
        public NoticePill Pill { get; private set; }
        /// <summary>Halt-all (U7): the stop bar along the top edge (or the rail), "Continue" in the header while halted.</summary>
        public HaltView Halt { get; private set; }
        /// <summary>The header's Menu panel (U8), over the body.</summary>
        public WindowMenu Menu { get; private set; }
        /// <summary>The header's "Menu" was pressed (the app decides whether it opens: never during a hold).</summary>
        public Action MenuPressed { get; set; }
        /// <summary>An item of the Menu was pressed.</summary>
        public Action<WindowMenuItem> MenuItem { get; set; }
        /// <summary>Halted is shown ("Continue" stands in the header).</summary>
        public bool Halted => _halted;
        /// <summary>A question the app asks (U9), directly under the header; while it shows nothing can be approved.</summary>
        public WindowBand Question { get; private set; }
        /// <summary>The guided test's band along the bottom edge (U9); not a question.</summary>
        public WindowBand TestBand { get; private set; }
        public MeshRenderer Background { get; private set; }
        public bool IsShown => _root.gameObject.activeSelf;
        public WorkspaceDock Dock { get; private set; }
        /// <summary>Fully open (not collapsed, not folding): only then is its content, and with it a request, on screen.</summary>
        public bool Open => !_collapsed && _fold <= 0f;
        /// <summary>Fully folded into the rail.</summary>
        public bool Folded => _collapsed && _fold >= 1f;
        public bool Collapsed => _collapsed;
        public WorkspaceTab Tab => _tab;
        /// <summary>The open row's content is on screen (the row has nearly reached its height).</summary>
        public bool ContentShown => Open && _tab == WorkspaceTab.Conversation && Rows.ContentShown;
        /// <summary>
        /// The open row's content is on screen and the opening motion has finished: the only state in which a request
        /// can be answered (U13).
        /// </summary>
        public bool OpenSettled => ContentShown && Rows.Settled;

        /// <summary>Its corners (local, at the base size).</summary>
        public static float Left => -BaseWidth * 0.5f;
        public static float Top => BaseHeight * 0.5f;
        public static float BodyTop => Top - WorkspaceHeader.Height;

        /// <summary>The centre of the body, well in front of the content (the raised row stands 6 mm in front of the panel).</summary>
        static Vector3 MenuCentre => new Vector3(0f, -WorkspaceHeader.Height * 0.5f, -0.014f);

        public static WorkspaceView Create(Transform parent, Action<string> onRow, Action<string> onDeny, Action onLater, Action<int> onScroll,
            Action onScrollToEnd, Action<string> onNotice, Action onAllAgents, Action onCollapse, Action onPreviousPage, Action onNextPage,
            Action onRail, Action<string> onRailSign)
        {
            var v = Ui.Child(parent, "Workspace", Vector3.zero).AddComponent<WorkspaceView>();
            v._root = Ui.Child(v.transform, "Window", Vector3.zero).transform;
            v.Background = Ui.Panel(v._root, "Background", new Vector3(0f, 0f, 0.002f), new Vector2(BaseWidth, BaseHeight), Theme.Glass);
            v._content = Ui.Child(v._root, "Content", Vector3.zero).transform;
            v.Header = WorkspaceHeader.Create(v._content, new Vector3(Left, Top, 0f), BaseWidth, onAllAgents, onCollapse, onPreviousPage, onNextPage);
            var rowsW = BaseWidth - 2f * WorkspaceHeader.SidePad;
            v.Rows = TaxiRowsView.Create(v._content, new Vector3(Left + WorkspaceHeader.SidePad, BodyTop, 0f), rowsW,
                BaseHeight - WorkspaceHeader.Height - WorkspaceHeader.BottomPad, onRow);
            v.Conversation = ConversationView.Create(v._content, rowsW + 2f * TaxiRowsView.Bleed, onDeny, onLater, onScroll, onScrollToEnd);
            v.Conversation.gameObject.SetActive(false);
            v.Rail = RailView.Create(v._root, new Vector3(Left, Top, 0f), onRail, onRailSign);
            v.Pill = NoticePill.Create(v._root, new Vector3(Left, Top, -0.001f), BaseWidth, onNotice);
            v.Halt = HaltView.Create(v._content, v._root, BaseWidth, null);
            // the menu: a sheet in the middle of the body, over a scrim that covers the body
            v.Menu = WindowMenu.Create(v._content, MenuCentre, new Vector2(BaseWidth - 2f * WorkspaceHeader.SidePad, BaseHeight - WorkspaceHeader.Height - 4f * Theme.S),
                item => v.MenuItem?.Invoke(item));
            v.Question = WindowBand.Create(v._content, "Question", BaseWidth, WindowBand.QuestionHeight, QuestionButtonWidth);
            v.TestBand = WindowBand.Create(v._content, "TestBand", BaseWidth, WindowBand.CaptionedHeight, TestButtonWidth);
            v.Menu.Band = v.Question;
            v.Menu.BandChanged = v.Refresh;
            v.Header.OnMenu = () => v.MenuPressed?.Invoke();
            v._tab = WorkspaceTab.Overview;
            v.ApplyMode();
            v._root.gameObject.SetActive(false);
            return v;
        }

        /// <summary>Stands where `dock` says: its centre, facing, up; scaled to its height.</summary>
        public void Place(WorkspaceDock dock)
        {
            Dock = dock;
            transform.SetPositionAndRotation(Ui.ToVector(dock.Center),
                Quaternion.LookRotation(Ui.ToVector(dock.Forward), Ui.ToVector(dock.Up)));
            transform.localScale = Vector3.one * (dock.Height / BaseHeight);
        }

        public void Show(bool on)
        {
            if (_root.gameObject.activeSelf != on) _root.gameObject.SetActive(on);
        }

        /// <summary>Whether a row is open (Conversation) and whether it is collapsed: the content follows at once, the panel's width over 0.15 s.</summary>
        public void SetMode(WorkspaceTab tab, bool collapsed)
        {
            if (tab == _tab && collapsed == _collapsed) return;
            _tab = tab;
            _collapsed = collapsed;
            if (Theme.Still) _fold = collapsed ? 1f : 0f;
            ApplyMode();
        }

        /// <summary>Halted (U7; U13): "Continue" stands in the header, the window keeps its size.</summary>
        public void SetHalted(bool on)
        {
            if (on == _halted) return;
            _halted = on;
            ApplyMode();
        }

        /// <summary>A band came or went (a question, the guided test's): the window makes room for it at once.</summary>
        public void Refresh() => ApplyMode();

        /// <summary>The question band or the open menu stands over the body: nothing can be approved meanwhile (AppRoot).</summary>
        public bool Asking => Question.IsShown || Menu.IsOpen;

        /// <summary>The panel's current rectangle (window local, base size): the stop bar runs along its top edge.</summary>
        public Rect PanelRect
        {
            get
            {
                var bg = Background.transform;
                var size = bg.localScale;
                return new Rect(bg.localPosition.x - size.x * 0.5f, bg.localPosition.y - size.y * 0.5f, size.x, size.y);
            }
        }

        /// <summary>
        /// The rail's edge (U5b): docked left of a display, the window's right edge (it hugs the display); otherwise its
        /// left edge. Folding and unfolding grow away from that edge.
        /// </summary>
        public bool RailOnRight
        {
            get => _railRight;
            set
            {
                if (value == _railRight) return;
                _railRight = value;
                ApplyMode();
            }
        }

        void Update()
        {
            var target = _collapsed ? 1f : 0f;
            if (!Mathf.Approximately(_railHeightApplied, Rail.Height) || !Mathf.Approximately(_extra, ExtraNow) ||
                !Mathf.Approximately(_below, BelowNow) || !Mathf.Approximately(_extentApplied, Header.Extent)) ApplyMode();
            if (!Mathf.Approximately(_fold, target))
            {
                _fold = Theme.Still ? target : Mathf.MoveTowards(_fold, target, Time.deltaTime / FoldSeconds);
                ApplyMode();
            }
            PlaceOpen();
        }

        void LateUpdate() => PlaceOpen();

        float ExtraNow => Question != null && Question.IsShown ? WindowBand.QuestionHeight : 0f;
        float BelowNow => TestBand != null && TestBand.IsShown ? WindowBand.CaptionedHeight : 0f;

        /// <summary>What shows for the mode and the fold: the content only fully open, the rail only fully folded.</summary>
        void ApplyMode()
        {
            _extra = ExtraNow;
            _below = BelowNow;
            var width = Mathf.Lerp(BaseWidth, RailView.Width, _fold);
            var height = Mathf.Lerp(BaseHeight + _extra + _below, Rail.Height, _fold);
            var top = Top + _extra * (1f - _fold);
            _railHeightApplied = Rail.Height;
            _extentApplied = Header.Extent;
            var bg = Background.transform;
            bg.localScale = new Vector3(width, height, 1f);
            Theme.SetRadius(Background, Mathf.Lerp(Theme.WindowRadius, RailView.Radius, _fold));
            // anchored at the rail's edge and the top edge: the fold shrinks toward the rail's corner
            bg.localPosition = new Vector3(_railRight ? -Left - width * 0.5f : Left + width * 0.5f, top - height * 0.5f, 0.002f);
            Header.transform.localPosition = new Vector3(Left, Top + _extra, 0f);
            // a question stands between the header and the body; the body stays where it is
            var bodyTop = Top - Header.Extent;
            Question.transform.localPosition = new Vector3(0f, bodyTop + _extra, -0.001f);
            TestBand.transform.localPosition = new Vector3(0f, -Top, -0.001f);
            Halt.PlaceBand(Top + _extra, Header.SayBottom);
            Menu.transform.localPosition = MenuCentre; // the body stays where it is whatever bands stand above it
            Rail.transform.localPosition = new Vector3(_railRight ? -Left - RailView.Width : Left, Top, 0f);
            Rows.transform.localPosition = new Vector3(Left + WorkspaceHeader.SidePad, bodyTop, 0f);
            Rows.SetArea(BaseHeight - Header.Extent - WorkspaceHeader.BottomPad);
            var open = Open;
            if (!open) Menu.Hide();
            SetActive(_content.gameObject, open);
            SetActive(Rows.gameObject, !Menu.IsOpen); // the menu's sheet stands alone on the asphalt
            SetActive(Rail.gameObject, Folded);
            if (!open && Pill.IsShown) Pill.Hide();
            PlaceOpen();
        }

        /// <summary>
        /// The open row's content: on the open row, 6 mm in front of the window like the row itself, shown once the row
        /// has (nearly) reached its height. The row gets the height the content needs.
        /// </summary>
        void PlaceOpen()
        {
            if (Rows == null || Conversation == null) return;
            Conversation.SetRoom(Rows.OpenRoom);
            Rows.SetOpenHeight(Conversation.Height);
            SetActive(Rows.gameObject, !Menu.IsOpen);
            var on = ContentShown;
            SetActive(Conversation.gameObject, on);
            if (!on) return;
            var box = Rows.OpenBox;
            var at = Rows.transform.localPosition + new Vector3(box.x, box.yMax, -TaxiRowsView.RaisedDepth - 0.0006f);
            if (Conversation.transform.localPosition != at) Conversation.transform.localPosition = at;
        }

        static void SetActive(GameObject go, bool on)
        {
            if (go.activeSelf != on) go.SetActive(on);
        }

        /// <summary>The rail's centre in world space (the gaze timer aims at it).</summary>
        public Vector3 RailCentre => transform.TransformPoint(Rail.LocalCentre);

        /// <summary>The window's face in world space (a ray or a finger there counts as touching it).</summary>
        public PokeRect Rect => new PokeRect(Ui.ToVec(Background.transform.position), Ui.ToVec(transform.right), Ui.ToVec(transform.up),
            Background.transform.lossyScale.x * 0.5f, Background.transform.lossyScale.y * 0.5f);

        /// <summary>The window's root (what the render probe counts).</summary>
        public GameObject Window => _root.gameObject;
    }
}
