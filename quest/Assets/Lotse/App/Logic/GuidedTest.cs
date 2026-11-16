#nullable enable
using System;
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    public enum TestResult { Ok, Fail, Skip }

    /// <summary>What a step of the guided test needs from the demo when it begins.</summary>
    public enum TestNeed { Nothing, HarmlessRequest, DangerousRequest }

    public readonly struct TestStep
    {
        public readonly string Id;
        public readonly string Instruction;
        public readonly TestNeed Need;

        public TestStep(string id, string instruction, TestNeed need = TestNeed.Nothing)
        {
            Id = id;
            Instruction = instruction;
            Need = need;
        }
    }

    /// <summary>
    /// The guided test (U9): the app tells the user what to try, one step at a time, and the user answers "Worked",
    /// "Did not work" or "Skip". Every step's begin and answer, and the summary at the end, go to the device log as
    /// `[Lotse] ev test …` lines (ids and enum names only), so the developer reads the result from the log. It decides
    /// nothing about requests: the app behaves normally while it runs. Pure.
    /// </summary>
    public sealed class GuidedTest
    {
        public static readonly TestStep[] Steps =
        {
            new TestStep("read", "Read the smallest text on a line. Is it sharp?"),
            new TestStep("distance", "Open Menu. Press Further twice, then Larger once."),
            new TestStep("open", "Tap the raised line to open that agent.", TestNeed.HarmlessRequest),
            new TestStep("one-hand", "Approve the harmless request: pinch and hold until the bar is dark.", TestNeed.HarmlessRequest),
            new TestStep("two-hand", "Approve the dangerous request: pinch with both hands and hold until the bar is dark.", TestNeed.DangerousRequest),
            new TestStep("deny", "Deny the next request: tap, or point and pinch, the Deny button.", TestNeed.HarmlessRequest),
            new TestStep("stop", "Hold both palms forward for one second to stop all agents. Then press Continue."),
            new TestStep("collapse", "Press Fold away. Wait three seconds. Then tap the rail to open it again."),
            new TestStep("dock", "Menu, Set up display: tap three corners of your monitor."),
            new TestStep("far", "Menu: press Further until the window is about two metres away. Can you still read and click?"),
            // (U11) answered from what happened when pairing: the test runs on the recording, without a PC
            new TestStep("qr", "On the pairing screen press Scan code and look at the QR code on your monitor."),
        };

        public const string Worked = "Worked";
        public const string DidNotWork = "Did not work";
        public const string Skip = "Skip";
        public const string Done = "Done";
        public const string MenuItem = "Guided test";

        /// <summary>A step is on show, or the summary.</summary>
        public bool Active { get; private set; }
        /// <summary>Every step is answered: the summary is on show.</summary>
        public bool Finished { get; private set; }
        /// <summary>The step on show (0-based).</summary>
        public int Index { get; private set; }
        public int Ok { get; private set; }
        public int Failed { get; private set; }
        public int Skipped { get; private set; }
        public TestStep Current => Steps[Math.Min(Index, Steps.Length - 1)];

        /// <summary>A step began (the app offers what it needs).</summary>
        public event Action<TestStep>? StepBegan;

        /// <summary>"Test 3 of 9".</summary>
        public string Caption => "Test " + (Index + 1) + " of " + Steps.Length;

        /// <summary>"9 tests: 7 worked, 1 failed, 1 skipped".</summary>
        public string Summary => Steps.Length + " tests: " + Ok + " worked, " + Failed + " failed, " + Skipped + " skipped";

        public void Begin()
        {
            Active = true;
            Finished = false;
            Index = 0;
            Ok = Failed = Skipped = 0;
            Enter();
        }

        /// <summary>The user's answer to the step on show: logged once, then the next step (or the summary).</summary>
        public void Answer(TestResult result)
        {
            if (!Active || Finished) return;
            var step = Current;
            Ev.Emit("test", ("step", Index + 1), ("name", step.Id), ("result", result == TestResult.Ok ? "ok" : result == TestResult.Fail ? "fail" : "skip"));
            if (result == TestResult.Ok) Ok++;
            else if (result == TestResult.Fail) Failed++;
            else Skipped++;
            if (Index + 1 < Steps.Length)
            {
                Index++;
                Enter();
                return;
            }
            Finished = true;
            Ev.Emit("test", ("phase", "summary"), ("ok", Ok), ("fail", Failed), ("skip", Skipped));
        }

        /// <summary>The test ends (the summary's "Done", a new session): nothing more is logged.</summary>
        public void End()
        {
            Active = false;
            Finished = false;
        }

        void Enter()
        {
            var step = Current;
            Ev.Emit("test", ("step", Index + 1), ("name", step.Id), ("phase", "begin"));
            StepBegan?.Invoke(step);
        }
    }

    /// <summary>
    /// The guided test's message source (U9): the jury pack's own start (its four agents, their plans, details and
    /// feeds) and then, instead of the pack's timeline, the requests the test's steps need, made here on demand. It is
    /// local and scripted like the pack: never connected to a bridge, nothing leaves the headset. Decisions on its
    /// requests are answered at once (the agent works again, the request closes), halt and release like the pack.
    /// It never finishes by itself.
    /// </summary>
    public sealed class GuidedSource : IMessageSource
    {
        public const string IdPrefix = "test-";

        readonly PackSource _pack;
        readonly double _introMs;
        readonly List<ServerMessage> _queue = new List<ServerMessage>();
        readonly List<ServerMessage> _out = new List<ServerMessage>();
        readonly string[] _sids;
        readonly Dictionary<string, string> _sources = new Dictionary<string, string>();
        readonly Dictionary<string, AgentState> _agents = new Dictionary<string, AgentState>();
        double _now;
        int _next;
        bool _halted;
        PermRequest? _open;
        double _preroll;
        double _introEnd;

        /// <param name="fromPlans">
        /// (U14, the tour) The pack's start is played at once up to the moment every agent has its plan: the window
        /// opens with its lines complete instead of filling for ten seconds.
        /// </param>
        public GuidedSource(DemoPack pack, bool fromPlans = false)
        {
            _pack = new PackSource(pack);
            // the pack plays until just before its first request: its agents are at work by then
            _introMs = double.PositiveInfinity;
            var sids = new List<string>();
            foreach (var e in pack.Messages)
            {
                if (e.Msg is AgentMessage a && !sids.Contains(a.Agent.Sid))
                {
                    sids.Add(a.Agent.Sid);
                    _sources[a.Agent.Sid] = a.Agent.Source ?? "";
                }
                if (e.Msg is PermOpenMessage)
                {
                    _introMs = e.T - 1;
                    break;
                }
            }
            _sids = sids.ToArray();
            _introEnd = _introMs;
            if (!fromPlans) return;
            var planned = new HashSet<string>();
            foreach (var e in pack.Messages)
            {
                if (e.T >= _introMs) break;
                if (e.Msg is AgentMessage a && a.Agent.Plan != null) planned.Add(a.Agent.Sid);
                if (planned.Count == _sids.Length)
                {
                    _preroll = e.T + 1;
                    break;
                }
            }
        }

        /// <summary>The request of the test that is open now, or null.</summary>
        public PermRequest? Open => _open;
        public bool OpenIsDangerous => _open != null && _open.Risk.Level == "destructive";
        public bool Finished => false;

        public IReadOnlyList<ServerMessage> Poll(double dtMs)
        {
            _out.Clear();
            if (!_halted)
            {
                var before = _now;
                _now += dtMs + _preroll;
                _preroll = 0;
                if (before < _introEnd)
                {
                    var dt = Math.Min(_now, _introEnd) - before;
                    if (dt > 0)
                        foreach (var m in _pack.Poll(dt))
                        {
                            // the newest state of each agent, plan included: a request changes only its status
                            if (m is AgentMessage am) _agents[am.Agent.Sid] = am.Agent.Clone();
                            _out.Add(m);
                        }
                }
            }
            if (_queue.Count > 0)
            {
                _out.AddRange(_queue);
                _queue.Clear();
            }
            return _out.ToArray();
        }

        /// <summary>
        /// A step needs a request: the open one stays when it is of that kind; else it is closed (reason "test") and a
        /// new one opens for the next agent in turn. Nothing while halted or before the agents are there.
        /// </summary>
        public bool Offer(bool dangerous)
        {
            if (_halted || _sids.Length == 0 || _agents.Count == 0) return false;
            if (_open != null)
            {
                if (OpenIsDangerous == dangerous) return true;
                _queue.Add(new PermClosedMessage { Id = _open.Id, Reason = "test" });
                _queue.Add(Agent(_open.Sid, "working", "Working"));
                _open = null;
            }
            // (U14) from the first request on the pack's start is over: its later updates of an agent would take the
            // "waiting" from the agent that now asks
            if (_introEnd > _now) _introEnd = _now;
            var sid = _sids[_next % _sids.Length];
            var id = IdPrefix + (++_next);
            var who = Texts.SourceWord(_sources.TryGetValue(sid, out var s) ? s : null);
            var req = new PermRequest
            {
                Id = id, Sid = sid, Tool = dangerous ? "Bash" : "Edit", CreatedAt = _now, ExpiresAt = _now + 600000,
                Risk = new RiskResult { Level = dangerous ? "destructive" : "safe", Reason = dangerous ? "Deletes files" : "Safe" },
            };
            Explanation explain;
            if (dangerous)
            {
                req.Command = "rm -rf ../legacy-site";
                req.CommandLength = req.Command.Length;
                explain = new Explanation { Text = who + " wants to delete the folder legacy-site next to your project", Source = "rules" };
            }
            else
            {
                req.Path = "<repo>/src/components/Cart.tsx";
                req.Paths = new List<string> { req.Path };
                explain = new Explanation { Text = who + " changes the file: src/components/Cart.tsx", Source = "rules" };
            }
            _open = req;
            _queue.Add(Agent(sid, "blocked", "Waiting for approval: " + req.Tool));
            _queue.Add(new PermOpenMessage { Request = req, Explain = explain });
            if (dangerous)
                _queue.Add(new PermImpactMessage
                {
                    Id = id,
                    Impact = new Impact
                    {
                        Kind = "delete", Target = "../legacy-site", Files = 214, OutsideProject = true, Recoverable = "no",
                        Reason = "Not in a Git project: gone for good.",
                    },
                });
            return true;
        }

        /// <summary>The agent as the pack last showed it (its title, project and plan), with another status.</summary>
        AgentMessage Agent(string sid, string status, string lastAction)
        {
            var a = _agents.TryGetValue(sid, out var known) ? known.Clone() : new AgentState { Sid = sid, Name = sid };
            a.Status = status;
            a.LastAction = lastAction;
            a.Since = _now;
            _agents[sid] = a.Clone();
            return new AgentMessage { Agent = a };
        }

        public IReadOnlyList<ServerMessage>? Send(ClientDecision decision)
        {
            if (_open == null || decision.Id != _open.Id) return null;
            var allow = decision.Behavior == "allow";
            var req = _open;
            _open = null;
            return new List<ServerMessage>
            {
                Agent(req.Sid, "working", (allow ? "Allowed: " : "Denied: ") + req.Tool),
                new PermClosedMessage { Id = req.Id, Reason = allow ? "allow" : "deny" },
            };
        }

        public IReadOnlyList<ServerMessage>? SendHalt(bool on)
        {
            var msgs = new List<ServerMessage>();
            var answered = _pack.SendHalt(on);
            if (answered != null) msgs.AddRange(answered);
            if (on && !_halted && _open != null)
            {
                msgs.Add(Agent(_open.Sid, "working", "Denied: " + _open.Tool));
                msgs.Add(new PermClosedMessage { Id = _open.Id, Reason = "halt" });
                _open = null;
            }
            _halted = on;
            return msgs;
        }

        public bool RequestDetail(string sid, out AgentDetailMessage? answer) => _pack.RequestDetail(sid, out answer);
        public IReadOnlyList<ServerMessage>? SendReply(string sid, string text) => _pack.SendReply(sid, text);
        public bool SubscribeFeed(string sid, out FeedSnapshotMessage? answer) => _pack.SubscribeFeed(sid, out answer);
        public void UnsubscribeFeed(string sid) => _pack.UnsubscribeFeed(sid);
    }
}
