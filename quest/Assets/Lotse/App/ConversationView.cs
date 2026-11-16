using System;
using System.Collections.Generic;
using Lotse.Core;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// What stands on the open row (U13; it was the workspace's right column): the open agent's request, or its work.
    /// At the right, in both, the plan as a list: a yellow mark for a done step, faint marks for the rest, and at the
    /// current step a small red light (it is held there by a request) or a white dot (it works there).
    /// - A request (ApprovalBlockView): the command, the facts, the stop bar with its instruction, "Later" and "Deny".
    /// - Work: at the left the last activity lines (FeedListView: verb in muted, object in mono, the newest in ink),
    ///   above them one muted line for an incident; along the bottom the reply field ("Tell Codex something"),
    ///   "Keyboard" and "Send" (InputBar, built by the ReplyController), or one muted line when the agent cannot take
    ///   replies. A line after a request closed ("Denied", "Timed out. …") stands above the reply field.
    /// The window's headline says which agent this is and what it wants or does. Origin: the open row's top left
    /// corner; the face looks along the parent's -z. Every agent text is plain and capped.
    /// </summary>
    public sealed class ConversationView : MonoBehaviour
    {
        const float U = Theme.U;
        public const float PadTop = 24f * U;
        public const float PadX = 28f * U;
        public const float PadBottom = 22f * U;
        const float Gap = 22f * U;
        const float PlanPitch = 30f * U;
        const int PlanLinesMax = 5;
        /// <summary>The top part (the log or the command and facts, next to the plan) is at least five plan lines high.</summary>
        public const float TopMin = PlanLinesMax * PlanPitch - 11f * U;
        const float LineRow = 19f * U;
        const float LineGap = 8f * U;
        const float MarkColumn = 26f * U;
        /// <summary>The open row's height for an agent at work, and at least for a request.</summary>
        public const float StandardHeight = PadTop + TopMin + Gap + InputBar.Height + PadBottom;

        float _width;
        float _contentW;
        float _leftW;
        TextMeshPro _incident;
        TextMeshPro _plan;
        LineColors _planColors;
        FlatMesh _planMarks;
        string _planKey;
        string _incidentLine;
        float _planHeight;
        string _vendor;
        Action<int> _onScroll;
        Action _onScrollToEnd;

        public FeedListView Feed { get; private set; }
        public ApprovalBlockView Block { get; private set; }
        public InputBar Input { get; private set; }
        /// <summary>The plan as drawn, one step per line; "No plan yet" without one.</summary>
        public string PlanShown => _plan.text;
        /// <summary>The marks before the plan's lines, top to bottom: "done", "held", "now", "ahead", "" (a fold).</summary>
        public List<string> PlanMarks { get; } = new List<string>();
        public string IncidentShown => _incident.gameObject.activeSelf ? _incident.text : null;
        /// <summary>The height the open row needs for what is shown now.</summary>
        public float Height { get; private set; } = StandardHeight;
        /// <summary>A request (or its cleared state) is shown, not the agent's work.</summary>
        public bool RequestShown => Block.IsShown && (Block.RequestId != null || Block.ClearedShown);
        /// <summary>The input bar's top edge.</summary>
        public float InputTop => -(PadTop + Mathf.Max(TopMin, _planHeight) + Gap);

        /// <summary>Every text on the open row as shown (for tests).</summary>
        public List<string> Lines
        {
            get
            {
                var list = new List<string>();
                foreach (var t in GetComponentsInChildren<TextMeshPro>(false))
                    if (t.GetComponent<MeshRenderer>().enabled) list.Add(t.text);
                return list;
            }
        }

        /// <param name="width">The open row's width (its box, bleed included).</param>
        public static ConversationView Create(Transform parent, float width, Action<string> onDeny, Action onLater, Action<int> onScroll,
            Action onScrollToEnd)
        {
            var v = Ui.Child(parent, "Conversation", Vector3.zero).AddComponent<ConversationView>();
            v._width = width;
            v._contentW = width - 2f * PadX;
            v._leftW = v._contentW - ApprovalBlockView.PlanGap - ApprovalBlockView.PlanWidth;
            v._onScroll = onScroll;
            v._onScrollToEnd = onScrollToEnd;

            v._incident = Ui.Text(v.transform, "Incident", "", Vector3.zero, v._leftW, Theme.Small, Theme.Muted, TextAlignmentOptions.Left);
            Ui.Middle(v._incident, v._leftW, LineRow, TextAlignmentOptions.Left);
            v._incident.gameObject.SetActive(false);

            var planX = PadX + v._leftW + ApprovalBlockView.PlanGap;
            var planTextW = ApprovalBlockView.PlanWidth - MarkColumn;
            v._plan = Ui.Text(v.transform, "Plan", "", new Vector3(planX + MarkColumn + planTextW * 0.5f, -PadTop, -0.001f), planTextW, Theme.Small, Theme.Muted);
            v._plan.textWrappingMode = TextWrappingModes.NoWrap;
            v._planColors = LineColors.On(v._plan);
            v._planMarks = new FlatMesh(v.transform, "PlanMarks", -0.0004f);
            v._planMarks.Renderer.transform.localPosition = new Vector3(planX, -PadTop, -0.0004f);

            v.Feed = FeedListView.Create(v.transform, Vector3.zero, v._leftW, TopMin, i => v._onScroll?.Invoke(i), () => v._onScrollToEnd?.Invoke());
            v.Block = ApprovalBlockView.Create(v.transform, v._contentW, onDeny, onLater);
            v.SetPlan(null, false);
            v.Layout();
            return v;
        }

        /// <summary>The room the open row can have at most: a taller request is capped to fewer lines.</summary>
        public void SetRoom(float height) => Block.MaxHeight = Mathf.Max(StandardHeight, height) - PadTop - PadBottom;

        /// <summary>The agent on the open row: the reply field's placeholder names its vendor ("Tell Codex something").</summary>
        public void SetAgent(AgentState agent)
        {
            var vendor = Texts.SourceWord(agent.Source);
            if (vendor == _vendor) return;
            _vendor = vendor;
            if (Input != null) Input.SetPlaceholder(Texts.ReplyTell(vendor));
        }

        /// <summary>The muted incident line above the log (null: none).</summary>
        public void SetIncident(string line)
        {
            if (string.IsNullOrEmpty(line)) line = null;
            if (line == _incidentLine) return;
            _incidentLine = line;
            if (line != null) _incident.text = Ui.OneLine(_incident, line, _leftW);
            Layout();
        }

        /// <summary>
        /// The plan at the right: every step when five lines hold them, else the window around the current step
        /// (PlanLines). `held`: the agent is held at its current step by a request (a red light), else it works there
        /// (a white dot). Null: "No plan yet".
        /// </summary>
        public void SetPlan(AgentPlan plan, bool held)
        {
            var lines = plan != null ? PlanLines.Window(plan, PlanLinesMax) : null;
            var current = plan != null ? PlanLines.Current(plan) : -1;
            var key = plan == null ? "-" : held + "|" + current + "|" + string.Join("\n", lines.ConvertAll(l => l.ToString()));
            if (key == _planKey) return;
            _planKey = key;
            PlanMarks.Clear();
            var m = _planMarks;
            m.Clear();
            _planColors.Clear();
            if (plan == null)
            {
                _plan.lineSpacing = 0f;
                _planColors.Set(Texts.NoPlanYet);
                Ui.Rows(_plan, PlanPitch, out var s0);
                _plan.transform.localPosition = new Vector3(_plan.transform.localPosition.x, -PadTop + s0, -0.001f);
                _planHeight = 0f;
                Block.MinTop = 0f;
                m.Apply();
                Layout();
                return;
            }
            var sb = new System.Text.StringBuilder();
            var room = _plan.rectTransform.sizeDelta.x;
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (i > 0) sb.Append('\n');
                var start = sb.Length;
                sb.Append(Ui.OneLine(_plan, l.Text ?? "", room));
                var cy = -(i + 0.5f) * PlanPitch;
                var cx = 7f * U;
                var now = l.Kind == PlanLineKind.Step && l.Index == current;
                if (l.Kind != PlanLineKind.Step) PlanMarks.Add("");
                else if (now && held)
                {
                    // held here: a small red light
                    m.RoundedRect(new Rect(cx - 6.5f * U, cy - 4.5f * U, 13f * U, 9f * U), Theme.LightRadius, FlatMesh.Solid(Look.Stop));
                    PlanMarks.Add("held");
                }
                else if (now)
                {
                    m.Disc(new Vector2(cx, cy), 5f * U, FlatMesh.Solid(Look.Ink));
                    PlanMarks.Add("now");
                }
                else
                {
                    var done = l.Status == PlanStatus.Done;
                    m.RoundedRect(new Rect(cx - 1.5f * U, cy - 7f * U, 3f * U, 14f * U), 1.5f * U, FlatMesh.Solid(done ? Look.Taxi : Look.Faint));
                    PlanMarks.Add(done ? "done" : "ahead");
                }
                if (now) _planColors.Add(start, sb.Length, Theme.Text);
            }
            m.Apply();
            _plan.lineSpacing = 0f;
            _planColors.Set(sb.ToString());
            var rows = Ui.Rows(_plan, PlanPitch, out var shift);
            _plan.transform.localPosition = new Vector3(_plan.transform.localPosition.x, -PadTop + shift, -0.001f);
            _planHeight = rows * PlanPitch - 11f * U;
            // (U14) a request's top part is at least as high as the plan beside it: "Later" and "Deny" stand under the
            // plan, never on it (a request without facts next to a plan of five lines put them on its last lines)
            Block.MinTop = _planHeight;
            Layout();
        }

        /// <summary>The input bar at the bottom, with the ReplyController's actions (built once per selection).</summary>
        public InputBar BuildInput(Action onSend, Action onEdit, Action onKeyboard)
        {
            DropInput();
            Input = InputBar.Create(transform, new Vector3(_width * 0.5f, InputTop, 0f), _contentW, onSend, onEdit, onKeyboard);
            if (_vendor != null) Input.SetPlaceholder(Texts.ReplyTell(_vendor));
            Layout();
            return Input;
        }

        public void DropInput()
        {
            if (Input == null) return;
            Input.gameObject.SetActive(false);
            Destroy(Input.gameObject);
            Input = null;
        }

        /// <summary>
        /// Places what is shown: a request (the block over the left column and along the bottom), or the agent's work
        /// (the incident line, the log, a closing note, the reply field). The plan stands at the right in both.
        /// </summary>
        public void Layout()
        {
            var top = Mathf.Max(TopMin, _planHeight);
            if (RequestShown)
            {
                Block.transform.localPosition = new Vector3(PadX, -PadTop, 0f);
                SetActive(Feed.gameObject, false);
                SetActive(_incident.gameObject, false);
                if (Input != null) SetActive(Input.gameObject, false);
                Height = Mathf.Max(StandardHeight, PadTop + Block.Height + PadBottom);
                return;
            }
            var y = -PadTop;
            var room = top;
            SetActive(_incident.gameObject, _incidentLine != null);
            if (_incidentLine != null)
            {
                _incident.transform.localPosition = new Vector3(PadX + _leftW * 0.5f, y - LineRow * 0.5f, -0.001f);
                y -= LineRow + LineGap;
                room -= LineRow + LineGap;
            }
            if (Block.IsShown)
            {
                // a closing note: one line at the bottom of the log's column
                Block.transform.localPosition = new Vector3(PadX, -PadTop - top + Block.Height, 0f);
                room -= Block.Height + LineGap;
            }
            SetActive(Feed.gameObject, true);
            // a log row is 20 units high, a plan row 30: 5 units down puts their first lines on one level
            Feed.transform.localPosition = new Vector3(PadX + _leftW * 0.5f, y - 5f * U, 0f);
            Feed.SetHeight(Mathf.Max(LineRow, room));
            if (Input != null)
            {
                SetActive(Input.gameObject, true);
                Input.transform.localPosition = new Vector3(_width * 0.5f, InputTop, 0f);
            }
            Height = PadTop + top + Gap + InputBar.Height + PadBottom;
        }

        static void SetActive(GameObject go, bool on)
        {
            if (go.activeSelf != on) go.SetActive(on);
        }
    }
}
