#nullable enable
using System;

namespace Lotse.App
{
    public enum TourPhase { Off, Instruction, Confirm, End }

    /// <summary>The real event that ends a step of the tour.</summary>
    public enum TourAdvance
    {
        /// <summary>After LinesMs, or a pinch that began in the step.</summary>
        TimeOrPinch,
        /// <summary>The row of the tour's request stands open.</summary>
        RowOpen,
        /// <summary>A completed one-hand approval.</summary>
        OneHand,
        /// <summary>A completed two-hand approval.</summary>
        TwoHand,
        /// <summary>"Deny" pressed on a request.</summary>
        Deny,
        /// <summary>Every agent is halted.</summary>
        Halted,
        /// <summary>The halt is released.</summary>
        Released,
        /// <summary>The window was folded away in this step and stands open again.</summary>
        Unfolded,
    }

    public readonly struct TourStep
    {
        public readonly string Id;
        public readonly string Instruction;
        /// <summary>One more line after HintMs without the step's event; empty: none.</summary>
        public readonly string Hint;
        /// <summary>The one word shown for a moment when the step is done; empty: the next step at once.</summary>
        public readonly string Done;
        public readonly TourAdvance Advance;
        public readonly TestNeed Need;
        /// <summary>
        /// The instruction while the row of the step's request is not open (U14b: after a deny the request is another
        /// agent's, after "Later" the row is closed); empty: the step has one instruction only.
        /// </summary>
        public readonly string Waiting;
        /// <summary>The hint that goes with Waiting.</summary>
        public readonly string WaitingHint;

        public TourStep(string id, string instruction, string hint, string done, TourAdvance advance, TestNeed need = TestNeed.Nothing,
            string waiting = "", string waitingHint = "")
        {
            Waiting = waiting;
            WaitingHint = waitingHint;
            Id = id;
            Instruction = instruction;
            Hint = hint;
            Done = done;
            Advance = advance;
            Need = need;
        }
    }

    /// <summary>What the app sees in one frame, as far as the tour cares.</summary>
    public struct TourFacts
    {
        /// <summary>A hand pinches (any, anywhere).</summary>
        public bool Pinching;
        /// <summary>The row of the tour's open request is open and its motion has finished.</summary>
        public bool RequestRowOpen;
        /// <summary>The row of the tour's open request is the open row (its motion may still run): the request can be read.</summary>
        public bool RequestShown;
        public bool Halted;
        /// <summary>The window is folded into the rail (or folding).</summary>
        public bool Folded;
        /// <summary>The window stands fully open.</summary>
        public bool WindowOpen;
    }

    /// <summary>
    /// The tour (U14): what a stranger meets first. Eight steps on the demo's agents with the guided test's local
    /// requests; each shows one instruction and ends by itself when the user has really done the thing (the app reports
    /// what happened; the tour never performs a gesture and decides nothing about a request). A done step shows one
    /// word for ConfirmMs, then the next instruction. After HintMs without the step's event its hint line shows.
    /// "Skip" leaves a step undone. Log: `[Lotse] ev tour step=3 name=one-hand phase=begin|done|skip|hint` and
    /// `tour phase=end`. Pure.
    /// </summary>
    public sealed class Tour
    {
        /// <summary>The hint of an approval step while its request's row is not open.</summary>
        public const string TapHint = "Point at the raised line and pinch briefly.";

        public static readonly TourStep[] Steps =
        {
            new TourStep("lines", "Each line is one agent. The dot shows where it is.", "", "", TourAdvance.TimeOrPinch),
            new TourStep("open", "One agent is waiting for you. Tap the raised line.",
                "Point at the line and pinch once, or touch it with a fingertip.", "Opened.", TourAdvance.RowOpen, TestNeed.HarmlessRequest),
            new TourStep("one-hand", "It wants to change one file. Pinch and hold until the bar is dark.",
                "Touch thumb and index finger, keep them together.", "Cleared.", TourAdvance.OneHand, TestNeed.HarmlessRequest,
                "One agent is waiting for you. Tap the raised line.", TapHint),
            new TourStep("two-hand", "This one deletes a folder for good. Pinch with both hands and hold.",
                "Both hands up, thumb and index finger together.", "Cleared.", TourAdvance.TwoHand,
                TestNeed.DangerousRequest, "This one deletes a folder for good. Tap the raised line.", TapHint),
            new TourStep("deny", "You can always say no. Open the next one and press Deny.",
                "Tap the raised line. Then point at Deny and pinch once.", "Denied.", TourAdvance.Deny, TestNeed.HarmlessRequest),
            new TourStep("stop", "Stop every agent at once: hold both palms forward for a second.",
                "Palms away from you, fingers up, like stopping traffic.", "Stopped.", TourAdvance.Halted),
            new TourStep("continue", "Everything is stopped. Press Continue.",
                "Continue is at the top right. Point at it and pinch once, then at Yes.", "Moving.", TourAdvance.Released),
            new TourStep("fold", "Done for now? Press Fold away, then tap the rail to come back.",
                "Fold away is at the top right. The rail is the narrow strip that stays.", "Back.", TourAdvance.Unfolded),
        };

        public const string StartButton = "Take the tour";
        public const string Skip = "Skip";
        public const string Again = "Again";
        public const string Done = "Done";
        public const string EndText = "That is Marshal. With your PC connected, these are your real agents.";
        /// <summary>Shown instead of a step's instruction when everything was stopped before the tour got to stopping.</summary>
        public const string HaltedText = "Everything is stopped. Press Continue to go on with the tour.";

        public const double LinesMs = 5000;
        public const double HintMs = 20000;
        public const double ConfirmMs = 1000;

        double _inStep;
        double _confirm;
        bool _pinchSeenOpen;
        bool _sawFolded;
        bool _sawHalted;
        bool _halted;
        bool _shown;

        /// <summary>The step on show has a request whose row is not open: its instruction is how to open it.</summary>
        public bool Waiting => Phase == TourPhase.Instruction && !_shown && Current.Waiting.Length > 0;

        /// <summary>A step, a confirmation or the end is on show.</summary>
        public bool Active => Phase != TourPhase.Off;
        public TourPhase Phase { get; private set; }
        /// <summary>The step on show (0-based).</summary>
        public int Index { get; private set; }
        public TourStep Current => Steps[Math.Min(Index, Steps.Length - 1)];
        /// <summary>The step's hint line shows.</summary>
        public bool HintShown { get; private set; }
        /// <summary>Counts every change of what the band shows.</summary>
        public int Version { get; private set; }

        /// <summary>A step began (the app sets the scene for it).</summary>
        public event Action<TourStep>? StepBegan;
        /// <summary>A step was done by the user (the tick).</summary>
        public event Action<TourStep>? StepDone;

        /// <summary>Everything is stopped although this step is not about that: the band says how to go on.</summary>
        public bool Blocked => Phase == TourPhase.Instruction && _halted && Current.Advance != TourAdvance.Halted &&
                               Current.Advance != TourAdvance.Released;

        /// <summary>The band's sentence.</summary>
        public string Text => Phase == TourPhase.End ? EndText : Phase == TourPhase.Confirm ? Current.Done : Blocked ? HaltedText : Waiting ? Current.Waiting : Current.Instruction;

        /// <summary>The muted line under the sentence, or "".</summary>
        public string Note => Phase == TourPhase.Instruction && HintShown && !Blocked ? Waiting ? Current.WaitingHint : Current.Hint : "";

        /// <summary>"3 of 8"; "" at the end.</summary>
        public string Progress => Phase == TourPhase.End || Phase == TourPhase.Off ? "" : (Index + 1) + " of " + Steps.Length;

        /// <summary>The request the step on show needs from the demo (none while a confirmation shows or all is stopped).</summary>
        public TestNeed Need => Phase == TourPhase.Instruction && !_halted ? Current.Need : TestNeed.Nothing;

        public void Begin()
        {
            Index = 0;
            _halted = false;
            _shown = false;
            Enter();
        }

        /// <summary>The tour ends without a word (a new session, "Done").</summary>
        public void End()
        {
            if (Phase == TourPhase.Off) return;
            Phase = TourPhase.Off;
            Version++;
        }

        /// <summary>"Skip": the step on show is left undone; the next one begins.</summary>
        public void SkipStep()
        {
            if (Phase != TourPhase.Instruction) return;
            Log("skip");
            Next();
        }

        /// <summary>A decision was sent for a request: a completed gesture (`allow`, with one hand or two) or "Deny".</summary>
        public void Decided(bool allow, bool twoHand)
        {
            if (Phase != TourPhase.Instruction) return;
            var a = Current.Advance;
            if (!allow ? a == TourAdvance.Deny : twoHand ? a == TourAdvance.TwoHand : a == TourAdvance.OneHand) Complete();
        }

        /// <summary>One frame: the facts decide whether the step on show is done; time brings the hint and ends a confirmation.</summary>
        public void Step(double dtMs, in TourFacts f)
        {
            if (Phase == TourPhase.Off || Phase == TourPhase.End) return;
            if (f.Halted != _halted)
            {
                _halted = f.Halted;
                Version++;
            }
            if (f.RequestShown != _shown)
            {
                _shown = f.RequestShown;
                if (Phase == TourPhase.Instruction && Current.Waiting.Length > 0) Version++;
            }
            if (Phase == TourPhase.Confirm)
            {
                _confirm += dtMs;
                if (_confirm >= ConfirmMs) Next();
                return;
            }
            var pinchBegan = f.Pinching && _pinchSeenOpen;
            if (!f.Pinching) _pinchSeenOpen = true;
            if (f.Folded) _sawFolded = true;
            if (f.Halted) _sawHalted = true;
            if (!Blocked) _inStep += dtMs;
            switch (Current.Advance)
            {
                case TourAdvance.TimeOrPinch:
                    if (_inStep >= LinesMs || pinchBegan) Complete();
                    return;
                case TourAdvance.RowOpen:
                    if (f.RequestRowOpen) Complete();
                    break;
                case TourAdvance.Halted:
                    if (f.Halted) Complete();
                    break;
                case TourAdvance.Released:
                    if (_sawHalted && !f.Halted) Complete();
                    else if (!_sawHalted)
                    {
                        // nothing is stopped (the step before was skipped): there is nothing to continue
                        Log("skip");
                        Next();
                    }
                    break;
                case TourAdvance.Unfolded:
                    if (_sawFolded && !f.Folded && f.WindowOpen) Complete();
                    break;
            }
            if (Phase == TourPhase.Instruction && !HintShown && !Blocked && Current.Hint.Length > 0 && _inStep >= HintMs)
            {
                HintShown = true;
                Version++;
                Log("hint");
            }
        }

        void Complete()
        {
            var step = Current;
            Log("done");
            StepDone?.Invoke(step);
            if (step.Done.Length == 0)
            {
                Next();
                return;
            }
            Phase = TourPhase.Confirm;
            _confirm = 0;
            Version++;
        }

        void Next()
        {
            if (Index + 1 < Steps.Length)
            {
                Index++;
                Enter();
                return;
            }
            Phase = TourPhase.End;
            Version++;
            Ev.Emit("tour", ("phase", "end"));
        }

        void Enter()
        {
            Phase = TourPhase.Instruction;
            _inStep = 0;
            _pinchSeenOpen = false;
            _sawFolded = false;
            _sawHalted = false;
            HintShown = false;
            Version++;
            Log("begin");
            StepBegan?.Invoke(Current);
        }

        void Log(string phase) => Ev.Emit("tour", ("step", Index + 1), ("name", Current.Id), ("phase", phase));
    }
}
