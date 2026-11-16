using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Lotse.App.Tests.WorkspaceHarness;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U10, from the first recorded device test and Meta's design guidance: the window is used with the ray from 2 m.
    /// Hit areas (larger than the drawn control, never overlapping, under every existing rule), hover and pressed states
    /// on every control with nothing moving, the menu as a sheet over a scrim, the notice pill only where the window
    /// does not already say it, and the deny-only instruction of a Gemini request.
    /// </summary>
    public class U10PlayTest
    {
        WorkspaceHarness _h;

        [SetUp]
        public void SetUp()
        {
            _h = new WorkspaceHarness();
            _h.SetUp();
        }

        [TearDown]
        public void TearDown()
        {
            _h.TearDown();
        }

        WorkspaceView View => _h.Root.Workspace;
        WindowMenu Menu => View.Menu;

        // ---- hit areas ----

        /// <summary>Every pair of controls in one plane: their hit areas (drawn size plus padding) do not overlap.</summary>
        static void AssertNoHitAreaOverlaps(string screen)
        {
            var buttons = PokeButton.All.Where(b => b != null && b.isActiveAndEnabled && b.Interactable).ToList();
            Assert.Greater(buttons.Count, 1, screen + ": controls on screen");
            var padded = 0;
            for (var i = 0; i < buttons.Count; i++)
            {
                var a = buttons[i];
                if (a.HitPad.x > 0f || a.HitPad.y > 0f) padded++;
                var ra = a.RayRect;
                var normal = Vec3.Cross(ra.Right, ra.Up);
                for (var j = i + 1; j < buttons.Count; j++)
                {
                    var b = buttons[j];
                    var rb = b.RayRect;
                    var rel = rb.Center - ra.Center;
                    if (System.Math.Abs(Vec3.Dot(rel, normal)) > 0.0005f) continue; // another layer (a row over the rail, a sheet over the window)
                    var dx = System.Math.Abs(Vec3.Dot(rel, ra.Right)) - (ra.HalfWidth + rb.HalfWidth);
                    var dy = System.Math.Abs(Vec3.Dot(rel, ra.Up)) - (ra.HalfHeight + rb.HalfHeight);
                    Assert.IsTrue(dx > -1e-4f || dy > -1e-4f,
                        $"{screen}: the hit areas of \"{a.Label}\" and \"{b.Label}\" overlap by {-dx * 1000f:0.0} x {-dy * 1000f:0.0} mm");
                }
            }
            Debug.Log($"[Lotse] hit areas on {screen}: {buttons.Count} controls, {padded} padded, none overlap");
        }

        [UnityTest]
        public IEnumerator HitAreasNeverOverlapOnAnyScreen()
        {
            yield return _h.Start();
            AssertNoHitAreaOverlaps("the start panel");
            _h.TearDown();
            yield return Frames(2);

            _h = new WorkspaceHarness { OpenConversation = false };
            _h.SetUp();
            yield return _h.GoLive(agents: string.Join(",", Enumerable.Range(1, 6).Select(i => Agent("x" + i, "X" + i, "working", "Agent " + i))));
            AssertNoHitAreaOverlaps("the overview with the pager");
            yield return _h.OpenRow("x1");
            _h.Socket.Receive(PermOpen("r1", "x1", "destructive", "rm -rf build"));
            yield return Frames(3);
            Assert.IsTrue(_h.Desk.BlockShown);
            AssertNoHitAreaOverlaps("the conversation with a block");
            yield return _h.OpenMenu();
            AssertNoHitAreaOverlaps("the menu");
            Menu.HaltButton.Click();
            yield return Frames(2);
            Assert.IsTrue(View.Question.IsShown);
            AssertNoHitAreaOverlaps("the halt question band");
            Menu.HaltCancelButton.Click();
            yield return Frames(2);
            yield return _h.OpenSettings();
            AssertNoHitAreaOverlaps("the settings");
            _h.Root.Settings.CloseButton.Click();
            yield return Frames(2);
            _h.Socket.Receive("{\"type\":\"halt_state\",\"on\":true,\"enforcement\":{\"claude\":\"every-step\"}}");
            yield return Frames(3);
            AssertNoHitAreaOverlaps("the halted window");
        }

        [UnityTest]
        public IEnumerator EveryControlOfTheHeaderTheBlockAndTheMenuIsAtLeastThreeDegreesFromTheSeat()
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(3);
            float Degrees(PokeButton b)
            {
                var r = b.RayRect;
                var d = Vector3.Distance(HeadPos, b.transform.position);
                return UiMetrics.Degrees(2f * Mathf.Min(r.HalfWidth, r.HalfHeight), d);
            }
            var controls = new List<PokeButton> { View.Header.AllButton, View.Header.MenuButton, View.Header.CollapseButton,
                _h.Block.DenyButton, _h.Block.LaterButton, View.Conversation.Input.KeyboardButton, View.Conversation.Input.SendButton, View.Conversation.Input.TextButton };
            foreach (var b in controls) Assert.GreaterOrEqual(Degrees(b), 2.95f, b.Label + " (measured from the head, a little off axis)");
            yield return _h.OpenMenu();
            foreach (var b in new[] { Menu.FurtherButton, Menu.CloserButton, Menu.LargerButton, Menu.SmallerButton, Menu.ReplaceButton, Menu.ScreenButton,
                         Menu.SettingsButton, Menu.TestButton, Menu.HaltButton, Menu.CloseButton })
            {
                Assert.GreaterOrEqual(Degrees(b), 2.95f, b.Label);
                Assert.AreEqual(Theme.H, b.Size.y, 1e-6f, b.Label + " is H high");
                Assert.AreEqual(Menu.FurtherButton.Size.x, b.Size.x, 1e-6f, "equal buttons");
            }
        }

        [UnityTest]
        public IEnumerator ARayInAHitAreaOutsideTheDrawnControlClicksItAndAPinchBegunThereNeverFeedsTheHold()
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(3);
            var later = _h.Block.LaterButton;
            // 5 mm (at the built size) above the drawn button: outside it, inside its hit area
            var k = View.transform.lossyScale.y;
            var above = later.transform.position + View.transform.up * ((Theme.H * 0.5f + 0.005f) * k);
            _h.Hands.Right = Hand(rayAt: above);
            yield return Frames(2);
            Assert.IsTrue(later.HoverShown, "the ray in the hit area hovers the control");
            // a pinch that begins there and is held for two seconds: masked from the hold, and no click
            _h.Hands.Right = Hand(1f, rayAt: above);
            yield return Frames(20);
            Assert.IsTrue(_h.Root.Pointer.MaskRight, "a pinch that began on the hit area is masked, like one on the drawn control");
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "it never feeds the hold");
            _h.Hands.Right = Hand(rayAt: above);
            yield return Frames(2);
            _h.Hands.Right = Hand();
            yield return Frames(2);
            Assert.AreEqual(0, _h.Decisions());
            Assert.IsTrue(_h.Desk.BlockShown, "a held pinch is no click");
            // a short pinch there clicks "Later"
            yield return _h.RayPinch(above);
            yield return Frames(2);
            Assert.IsFalse(_h.Desk.BlockShown, "Later: the block goes");
            Assert.AreEqual(0, _h.Decisions(), "nothing sent");
        }

        // ---- hover and pressed states, nothing moves ----

        [UnityTest]
        public IEnumerator EveryKindOfControlShowsHoverAndPressedAndNothingMoves()
        {
            _h.OpenConversation = false;
            yield return _h.GoLive();
            var menu = View.Header.MenuButton; // an area: text only, its hover and pressed state a faint fill
            var column = View.Rows.RowOf(View.Rows.Shown.Last().Sid); // a row that is not raised
            var at = menu.transform.position;
            var before = menu.transform.position;
            Assert.IsFalse(menu.HoverShown);
            _h.Hands.Right = Hand(rayAt: at);
            yield return Frames(3);
            Assert.IsTrue(menu.HoverShown, "the header's Menu shows its hover state under the ray");
            // (U11) drawn by the header's own mesh: no renderer of its own
            Assert.IsNull(menu.transform.Find("Visual/Hover"), "no hover surface");
            Assert.IsTrue(ChromeHas(HeaderHover), "a faint fill behind the tool");
            Assert.AreEqual(before, menu.transform.position, "nothing moves on hover");
            Assert.AreEqual(Vector3.one, menu.transform.Find("Visual").localScale);
            _h.Hands.Right = Hand(rayAt: column.transform.position);
            yield return Frames(3);
            Assert.IsFalse(menu.HoverShown, "gone when the ray leaves");
            Assert.IsFalse(ChromeHas(HeaderHover), "back to its colour");
            Assert.IsTrue(column.HoverShown, "a row shows it too");
            Assert.IsTrue(MeshHas(column.transform.parent.Find("Line"), RowHover), "a row: a faint surface under it, in its own mesh");
            // pressed
            _h.Hands.Right = Hand();
            yield return Frames(1);
            menu.Click();
            Assert.IsTrue(menu.PressShown, "a press shows the pressed state");
            yield return Frames(2);
            Assert.IsTrue(Menu.IsOpen);
            // a drawn button: lighter and its border lit, never scaled
            var further = Menu.FurtherButton;
            _h.Hands.Right = Hand(rayAt: further.transform.position);
            yield return Frames(3);
            Assert.IsTrue(further.HoverShown);
            Assert.AreEqual(1f, further.VisualScale, 1e-6f);
            Assert.AreEqual(Vector3.one, further.transform.Find("Visual").localScale, "nothing moves");
        }

        static Color HeaderHover => FlatMesh.Over(Look.Hex(0xFFFFFF), 0.05f, Look.Asphalt);
        static Color RowHover => FlatMesh.Over(Look.Hex(0xFFFFFF), 0.03f, Look.Asphalt);

        /// <summary>Whether the header's hover mesh has a vertex of this colour.</summary>
        bool ChromeHas(Color c) => MeshHas(View.Header.transform.Find("Hover"), c);

        static bool MeshHas(Transform t, Color c)
        {
            var mesh = t.GetComponent<MeshFilter>().sharedMesh;
            Color32 v = QualitySettings.activeColorSpace == ColorSpace.Linear ? (Color32)c.linear : (Color32)c; // as FlatMesh stores it
            return mesh.colors32.Any(x => x.r == v.r && x.g == v.g && x.b == v.b);
        }

        [UnityTest]
        public IEnumerator U11HoveringARowAHeaderControlOrThePlanLineAddsNoRenderer()
        {
            var agents = string.Join(",", new[]
            {
                Agent("s1", "A", "blocked", "Checkout-Summen korrigieren"), Agent("s2", "B", "editing", "Warenkorb-Badge", "Bearbeitet src/Cart.tsx"),
                Agent("s3", "C", "testing", "Alte Seiten migrieren", "Führt Tests aus"), Agent("s4", "D", "working", "Produktsuche", "Arbeitet"),
                Agent("s5", "E", "done", "Header aufräumen"), Agent("s6", "F", "failed", "Login reparieren"),
            });
            yield return _h.GoLive(agents: agents);
            _h.Socket.Receive(PermOpen("r1", "s1", "destructive", "git push --force origin main"));
            yield return Frames(3);
            Assert.IsTrue(_h.Desk.BlockShown);
            var window = View.Window;
            var renderers = RenderProbe.Renderers(window);
            var passes = RenderProbe.Passes(window);
            var row = View.Rows.RowOf("s4");
            var targets = new List<PokeButton> { row, View.Header.MenuButton, View.Header.CollapseButton, View.Header.AllButton };
            foreach (var b in targets)
            {
                _h.Hands.Right = Hand(rayAt: b.transform.position);
                yield return Frames(3);
                Assert.IsTrue(b.HoverShown, b.Label + ": hovered");
                Assert.IsTrue(b == row ? MeshHas(row.transform.parent.Find("Line"), RowHover) : ChromeHas(HeaderHover), b.Label + ": drawn lighter in a mesh that is there anyway");
                Assert.AreEqual(renderers, RenderProbe.Renderers(window), b.Label + ": no renderer more for its hover");
                Assert.AreEqual(passes, RenderProbe.Passes(window), b.Label + ": no pass more");
                Assert.IsNull(b.transform.Find("Visual/Hover"));
            }
            // both hands and the ray at once: still nothing more
            _h.Hands.Right = Hand(rayAt: row.transform.position);
            _h.Hands.Left = Hand(tip: View.Header.MenuButton.transform.position - View.transform.forward * 0.01f);
            yield return Frames(2);
            Assert.AreEqual(renderers, RenderProbe.Renderers(window));
            Assert.LessOrEqual(RenderProbe.Passes(window), 48);
            _h.Hands.Right = Hand();
            _h.Hands.Left = Hand();
            yield return Frames(3);
            Assert.IsFalse(ChromeHas(HeaderHover) || MeshHas(row.transform.parent.Find("Line"), RowHover), "at rest nothing is lit");
        }

        [UnityTest]
        public IEnumerator U12APrimaryButtonsLabelIsDarkOnItsLightFillAtRestHoveredAndPressed()
        {
            yield return _h.Start();
            var demo = PokeButton.All.First(b => b.Label == Texts.TourButton); // (U14) the start panel's primary button is "Take the tour"
            var label = demo.GetComponentInChildren<TMPro.TextMeshPro>();
            Rgb Of(Color c) => new Rgb(c.r, c.g, c.b);
            var fill = demo.transform.Find("Visual/Background").GetComponent<MeshRenderer>();
            for (var state = 0; state <= 2; state++)
            {
                var c = PokeButton.StateColor(Theme.Primary, state);
                Assert.GreaterOrEqual(Look.Contrast(Of(PokeButton.LabelColor(true)), Of(c)), 12.0, "the label on the fill, state " + state);
                Assert.GreaterOrEqual(c.a, 0.97f, "the fill stays opaque: nothing of the room shows through under the label");
            }
            // on the button itself: at rest, under the ray, pressed
            Assert.AreEqual(Fonts.SemiBold, label.font, "the heaviest face: dark strokes on a light fill thin out on the headset");
            _h.Hands.Right = Hand(rayAt: demo.transform.position);
            yield return Frames(3);
            Assert.IsTrue(demo.HoverShown);
            Assert.GreaterOrEqual(Look.Contrast(Of(label.color), Of(Ui.ColorOf(fill))), 12.0, "hovered");
            Assert.AreEqual(Theme.OnPrimary, label.color);
            // a secondary button for comparison: light on dark in every state
            for (var state = 0; state <= 2; state++)
                Assert.GreaterOrEqual(Look.Contrast(Of(PokeButton.LabelColor(false)), Of(PokeButton.StateColor(Ui.ToColor(Look.Control), state))), 4.0, "secondary (white at 7 % over the asphalt), state " + state); // pressed is a 0.18 s flash
        }

        // ---- the menu: a sheet over a scrim ----

        [UnityTest]
        public IEnumerator TheMenuIsACentredSheetOverAScrimThatStopsTheRay()
        {
            yield return _h.GoLive();
            yield return _h.OpenMenu();
            Assert.IsTrue(Menu.Scrim.gameObject.activeInHierarchy, "the scrim");
            Assert.GreaterOrEqual(WindowMenu.ScrimAlpha, 0.9f, "dense enough: the content is not readable through it");
            // centred in the window's body, inside it
            var local = View.transform.InverseTransformPoint(Menu.transform.position);
            Assert.AreEqual(0f, local.x, 1e-4f, "centred");
            Assert.AreEqual(-WorkspaceHeader.Height * 0.5f, local.y, 1e-4f, "in the middle of the body");
            Assert.Less(Menu.Height, WorkspaceView.BaseHeight - WorkspaceHeader.Height, "the sheet fits in the body");
            var texts = Menu.GetComponentsInChildren<TMPro.TextMeshPro>().Select(t => t.text).ToList();
            CollectionAssert.Contains(texts, WindowMenu.Title);
            CollectionAssert.Contains(texts, WindowMenu.WindowCaption);
            CollectionAssert.Contains(texts, WindowMenu.AppCaption);
            // two columns: each pair on one row
            foreach (var (l, r) in new[] { (Menu.FurtherButton, Menu.CloserButton), (Menu.LargerButton, Menu.SmallerButton), (Menu.ReplaceButton, Menu.ScreenButton),
                         (Menu.SettingsButton, Menu.TestButton), (Menu.HaltButton, Menu.CloseButton) })
            {
                Assert.AreEqual(l.transform.localPosition.y, r.transform.localPosition.y, 1e-6f, l.Label + " and " + r.Label + " share a row");
                Assert.Less(l.transform.localPosition.x, r.transform.localPosition.x);
            }
            // the scrim stops the ray: a row behind it cannot be clicked through it
            var row = _h.Rows.RowOf("s2");
            var selected = _h.Root.SelectedAgent;
            Assert.AreNotEqual("s2", selected);
            yield return _h.RayPinch(row.transform.position);
            yield return Frames(2);
            Assert.AreEqual(selected, _h.Root.SelectedAgent, "nothing behind the scrim is clickable");
            Assert.IsTrue(Menu.IsOpen);
            yield return _h.RayPinch(Menu.CloseButton.transform.position);
            yield return Frames(2);
            Assert.IsFalse(Menu.IsOpen, "Close, by ray");
            yield return _h.RayPinch(row.transform.position);
            yield return Frames(2);
            Assert.AreEqual("s2", _h.Root.SelectedAgent, "clickable again");
        }

        // ---- the notice pill ----

        [UnityTest]
        public IEnumerator NoPillInTheOverviewNorForTheAgentWhoseConversationIsShown()
        {
            _h.OpenConversation = false;
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s2"));
            yield return Frames(3);
            Assert.IsFalse(View.Pill.IsShown, "the overview: the red card says it");
            Assert.IsTrue(View.Rows.RaisedOf("s2"));
            // the open row of that agent: its block says it
            yield return _h.OpenRow("s2");
            yield return Frames(3);
            Assert.IsTrue(_h.Desk.BlockShown);
            Assert.IsFalse(View.Pill.IsShown);
            // another agent asks while this conversation is shown: the pill names it, and at once (the notice that was
            // not shown does not hold it back)
            _h.Socket.Receive(PermOpen("r2", "s1"));
            yield return Frames(4);
            Assert.IsTrue(View.Pill.IsShown, "another agent: the pill");
            Assert.AreEqual("s1", View.Pill.Sid);
            // back to the overview: the pill goes
            yield return _h.CloseRow();
            yield return Frames(2);
            Assert.IsFalse(View.Pill.IsShown, "gone in the overview");
        }

        // ---- deny only ----

        [UnityTest]
        public IEnumerator AGeminiRequestSaysDenyOnlyInItsInstructionLineNotInItsSentence()
        {
            var gemini = Agent("g1", "Gemini 1", "waiting", "Produktsuche").Replace("\"source\":\"claude\"", "\"source\":\"gemini\"");
            yield return _h.GoLive(agents: gemini + "," + Agent("s2", "Agent 2", "working", "Checkout"));
            yield return _h.PokeRow("g1");
            // an older bridge still sends the prefix: it is taken off the sentence
            _h.Socket.Receive(PermOpen("r1", "g1", "safe", "ls -la", "Gemini (Deny only): Gemini lists the files: ls -la"));
            yield return Frames(3);
            Assert.IsTrue(_h.Desk.BlockShown);
            Assert.AreEqual("Gemini lists the files", _h.Block.HeadlineShown, "no vendor prefix inside the sentence");
            StringAssert.StartsWith("Deny only", _h.Block.HintShown, "the instruction line says it");
            StringAssert.Contains("hold a pinch", _h.Block.HintShown);
            // the gesture and what it needs are unchanged
            yield return _h.PinchHold(false, 12);
            Assert.AreEqual(1, _h.Decisions());
            // any other agent keeps the usual instruction
            _h.ReleaseHands();
            yield return _h.PokeRow("s2");
            _h.Socket.Receive(PermOpen("r2", "s2"));
            yield return Frames(3);
            Assert.AreEqual(Texts.HintPinch, _h.Block.HintShown);
        }
    }
}
