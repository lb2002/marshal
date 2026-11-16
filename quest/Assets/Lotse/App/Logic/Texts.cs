#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>Everything the app writes on cards and panels (English UI since U5b; the product name is one constant).</summary>
    public static class Texts
    {
        public const string ProductName = "Marshal";
        public const string StartTitle = ProductName;
        public const string StartSubtitle = "Your AI agents at your desk";
        public const string StartButton = "Watch the demo";
        public const string TourButton = Tour.StartButton;
        public const string StartHeadline = "Direct your coding agents with your hands.";
        public const string BridgeButton = "Connect to my PC";
        public const string StartHint = "Point and pinch briefly to select. Hold to allow.";
        public const string Replace = "Place again";
        public const string PlaceHint = "Tap the desk next to your laptop (not the keyboard)";
        public const string PlaceFallback = "Placed without a desk. You can place it again later.";
        public const string PlaceKept = "Desk not found. Everything stays where it was.";
        public const string Again = "Again";
        public const string HintPinch = "Pinch and hold until the bar is dark";
        public const string HintTwoHand = "Pinch with both hands and hold until the bar is dark";
        /// <summary>The words of HintTwoHand drawn in ink.</summary>
        public const string HintTwoHandStrong = "both hands";
        public const string KeepHolding = "Keep holding";
        public const string AllAgents = "All agents";
        /// <summary>The facts of a two-hand request (U13): a large value over a small caption.</summary>
        public const string FactFiles = "files";
        public const string FactFile = "file";
        public const string FactFilesAtLeast = "files at least";
        public const string FactSize = "in size";
        public const string FactSizeAtLeast = "at least";
        public const string FactOutside = "Outside";
        public const string FactInside = "Inside";
        public const string FactProject = "this project";
        public const string FactGone = "Gone";
        public const string FactGoneCaption = "no way back";
        public const string FactUnknown = "Unknown";
        public const string FactChecking = "Checking";
        public const string FactEffects = "effects";
        public const string NoPlanYet = "No plan yet";
        public const string TruncatedReason = "Shortened, so both hands";
        public const string DemoUnavailable = "Demo not available";
        public const string CueRaiseHands = "Hands a little higher, look at the request";
        public const string CueRelease = "Let go briefly, then pinch again";
        public const string AiLabel = "AI";

        // ---- docking to a display (U5b) ----
        public const string ScreenSetup = "Set up display";
        public const string ScreenOffer = "Put " + ProductName + " next to a display?";
        public const string ScreenOfferLater = "Later";
        public const string ScreenCornerTopLeft = "Hold a fingertip on the top left corner of the display.";
        public const string ScreenCornerTopRight = "Now the top right corner.";
        public const string ScreenCornerBottomRight = "Now the bottom right corner.";
        public const string ScreenRejected = "That does not look like a display. Please try again.";
        public const string ScreenPickSide = "Where should " + ProductName + " stand? Point at a frame and pinch, or touch it.";
        public const string ScreenLost = "Display not found again. Set it up again in the menu.";
        // U10: the dock is made at once after the third corner; a band says where and offers the other sides
        public const string OtherSide = "Other side";
        public const string Done = "Done";

        /// <summary>"Docked right of your display", "… left of …", "… above …", "… below …".</summary>
        public static string Docked(DockSide side)
        {
            switch (side)
            {
                case DockSide.Left: return "Docked left of your display";
                case DockSide.Above: return "Docked above your display";
                case DockSide.Below: return "Docked below your display";
                default: return "Docked right of your display";
            }
        }

        // ---- the facts row of a dangerous request (U7) ----
        public const string CheckedPrefix = "Checked: ";
        public const string OutsideProject = "outside the project";
        public const string InsideProject = "inside the project";
        public const string GoneForGood = "Gone for good";
        public const string EffectsUnknown = "Effects unknown";
        public const string CheckingEffects = "Checking effects";

        // ---- halt-all (U7) ----
        public const string HaltAll = "Halt all agents";
        // ---- the header's Menu (U8) ----
        public const string Menu = "Menu";
        public const string Further = "Further";
        public const string Closer = "Closer";
        public const string Larger = "Larger";
        public const string Smaller = "Smaller";
        public const string Close = "Close";
        public const string Undocked = "Undocked";
        public const string CloseMenuToAnswer = "Close the menu to answer";
        /// <summary>The block's line while a question band of the window waits for its answer (U9).</summary>
        public const string AnswerQuestionFirst = "Answer the question above first";
        public const string HaltAsk = "Halt all agents?";
        public const string Cancel = "Cancel";
        public const string Continue = "Continue";
        public const string ContinueAsk = "Really continue?";
        public const string HaltNotConnected = "Not connected, nothing halted";
        public const string HaltDenied = "Denied: all agents halted";

        /// <summary>"Halted for 12 s", "Halted for 3 min".</summary>
        public static string HaltedFor(double ms)
        {
            var s = ms <= 0 ? 0L : (long)Math.Floor(ms / 1000.0);
            return s < 60 ? $"Halted for {s} s" : $"Halted for {s / 60} min";
        }

        /// <summary>A source's name in the enforcement line (the bridge's keys are untrusted: unknown ones are "other agents").</summary>
        static string EnforcementName(string source)
        {
            switch (source)
            {
                case "claude": return "Claude Code";
                case "codex": return "Codex";
                case "muse": return "Muse";
                case "gemini": return "Gemini CLI";
                default: return "Other agents";
            }
        }

        static readonly string[] KnownSources = { "claude", "codex", "muse", "gemini" };

        /// <summary>
        /// The line under the halted band, from the bridge's `enforcement`: "Claude Code and Codex stop before their next
        /// step. Muse: only requests are denied. Gemini CLI keeps running." Sources in a fixed order (unknown ones, once,
        /// as "Other agents" last); empty without any source.
        /// </summary>
        public static string Enforcement(IReadOnlyDictionary<string, string> enforcement)
        {
            var step = new List<string>();
            var requests = new List<string>();
            var none = new List<string>();
            void Add(string mode, string name)
            {
                var list = mode == "every-step" ? step : mode == "requests-only" ? requests : none;
                if (!list.Contains(name)) list.Add(name);
            }
            foreach (var s in KnownSources)
                if (enforcement.TryGetValue(s, out var mode)) Add(mode, EnforcementName(s));
            var others = new List<string>(enforcement.Keys);
            others.Sort(StringComparer.Ordinal);
            foreach (var s in others)
                if (Array.IndexOf(KnownSources, s) < 0) Add(enforcement[s], EnforcementName(s));

            var parts = new List<string>();
            if (step.Count > 0)
                parts.Add(JoinNames(step) + (Plural(step) ? " stop before their next step." : " stops before its next step."));
            if (requests.Count > 0) parts.Add(JoinNames(requests) + ": only requests are denied.");
            if (none.Count > 0) parts.Add(JoinNames(none) + (Plural(none) ? " keep running." : " keeps running."));
            return string.Join(" ", parts);
        }

        static bool Plural(List<string> names) => names.Count > 1 || names[0] == "Other agents";

        /// <summary>"A", "A and B", "A, B and C".</summary>
        static string JoinNames(List<string> names) =>
            names.Count == 1 ? names[0] : string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[names.Count - 1];

        /// <summary>The prompt for the next corner (0, 1, 2 points tapped so far).</summary>
        public static string ScreenCorner(int tapped) =>
            tapped <= 0 ? ScreenCornerTopLeft : tapped == 1 ? ScreenCornerTopRight : ScreenCornerBottomRight;

        // ---- the workspace (U5a) ----
        public const string WorkspaceTitle = ProductName;
        public const string NoAgents = "No agent connected yet. Run \"lotse connect\" on your PC.";
        public const string PickAgent = "Pick an agent on the left.";
        public const string Deny = "Deny";
        public const string Later = "Later";
        public const string RiskDangerous = "Dangerous";
        public const string RiskNetwork = "Network";
        public const string RiskSafe = "Harmless";
        public const string Denied = "Denied";
        public const string TimedOut = "Timed out. Please answer in the terminal.";
        public const string ReplyAfterRequest = "Answer the request above first";
        const int HeadlineMax = 90;

        /// <summary>"4 agents", "1 agent".</summary>
        public static string AgentCount(int n) => n == 1 ? "1 agent" : n + " agents";

        /// <summary>"+2 more": the selected agent's other open requests.</summary>
        public static string MoreRequests(int n) => "+" + n + " more";

        /// <summary>
        /// The risk word on the block: "dangerous" for a destructive or cut request and for anything the risk could not
        /// be read from (fail closed, like the gesture: PermRequest.NeedsTwoHand); "network"; "safe".
        /// </summary>
        public static string RiskWord(PermRequest req)
        {
            if (req.NeedsTwoHand) return RiskDangerous;
            return req.Risk.Level == "network" ? RiskNetwork : RiskSafe;
        }

        /// <summary>
        /// What the agent wants, in one line: the explanation's first part (up to its first colon or full stop), capped;
        /// without an explanation, the tool. Untrusted, shown plain.
        /// </summary>
        public static string Headline(Explanation? e, PermRequest req)
        {
            var text = e != null ? WithoutVendorPrefix(e.Text.Trim()) : "";
            if (text.Length == 0) return "Wants to use " + Untrusted.Clamp(req.Tool, Untrusted.ToolMax);
            var end = HeadlineEnd(text);
            return Untrusted.Clamp(end > 0 ? text.Substring(0, end) : text, HeadlineMax);
        }

        /// <summary>
        /// The explanation beyond its headline (a colon's rest is the subject, shown on its own line, so it is left out
        /// here), then the AI's extra line ("KI: …"); empty when there is nothing more.
        /// </summary>
        public static string Detail(Explanation? e)
        {
            if (e == null) return "";
            var text = WithoutVendorPrefix(e.Text.Trim());
            var end = HeadlineEnd(text);
            var rest = end > 0 && text[end] == '.' ? text.Substring(end + 1).Trim() : "";
            var parts = new List<string>();
            if (rest.Length > 0) parts.Add(Untrusted.Clamp(rest, Untrusted.ExplainMax));
            if (!string.IsNullOrWhiteSpace(e.AiText)) parts.Add(AiLine(e.AiText!));
            return string.Join(" ", parts);
        }

        /// <summary>Where the headline ends: the first colon, or the first full stop followed by a space; 0 for the whole text.</summary>
        static int HeadlineEnd(string text)
        {
            for (var i = 1; i < text.Length - 1; i++)
            {
                if (text[i] == ':') return i;
                if (text[i] == '.' && text[i + 1] == ' ') return i;
            }
            return 0;
        }

        /// <summary>The notice above the window: "Checkout-Summen korrigieren braucht dich: Claude ändert die Datei".</summary>
        public static string Notice(AgentState? agent, string sid, string headline) =>
            (agent != null ? AgentTitle(agent) : LogSid(sid)) + " needs you: " + headline;

        /// <summary>An agent's title in the list and the header: its title, else its name, else its project; capped.</summary>
        public static string AgentTitle(AgentState agent)
        {
            if (!string.IsNullOrWhiteSpace(agent.Title)) return Untrusted.Clamp(agent.Title, Untrusted.TitleMax);
            if (!string.IsNullOrWhiteSpace(agent.Name)) return Untrusted.Clamp(agent.Name, Untrusted.NameMax);
            if (!string.IsNullOrWhiteSpace(agent.Project)) return Untrusted.Clamp(agent.Project, Untrusted.ProjectMax);
            return "Agent";
        }

        /// <summary>"Claude Code in shop-web": the coding agent behind it (as text, no logo) and its project.</summary>
        public static string SourceLine(AgentState agent)
        {
            var source = SourceWord(agent.Source);
            return string.IsNullOrWhiteSpace(agent.Project) ? source : source + " in " + Untrusted.Clamp(agent.Project, Untrusted.ProjectMax);
        }

        public static string SourceWord(string? source)
        {
            switch (source)
            {
                case "claude": return "Claude Code";
                case "muse": return "Muse";
                case "codex": return "Codex";
                case "gemini": return "Gemini CLI";
                default: return "Agent";
            }
        }
        public const string Loading = "Loading the demo …";

        // the agent detail panel
        public const string NoDetails = "No details";
        public const string LoadingDetails = "Loading details …";
        public const string CloseDetail = "Close";
        public const string RecentTitle = "Recent";

        // replying to an agent
        public const string ReplyButton = "Reply";
        public const string ReplyNotWaiting = "You can reply once the agent waits for you";
        public const string ReplyNotSupported = "Replies work with Claude Code sessions only for now";
        public const string ReplyPlaceholder = "Type your reply";
        /// <summary>The empty reply field on an open row: "Tell Codex something" (the vendor's name is ours).</summary>
        public static string ReplyTell(string vendor) => "Tell " + vendor + " something";
        public const string ReplySend = "Send";
        public const string ReplyCancel = "Cancel";
        public const string ReplySending = "Sending …";
        /// <summary>"Sent ✓" in the brief; ✓ is not in Liberation Sans, √ is.</summary>
        public const string ReplyDelivered = "Sent √";
        public const string ReplyNotDelivered = "The agent is not waiting right now. Message not delivered";
        public const string ReplyNoAnswer = "No answer from the bridge";
        public const string ReplyTooSoon = "Please wait a moment, then send again";
        public const string ReplyNoKeyboard = "Keyboard not available";
        public const string ReplyEdit = "Edit";
        public const string ReplyNotAccepted = "Message not accepted";

        // ---- the live view (U4b) ----
        public const string FeedLoading = "Loading history …";
        public const string FeedUnavailable = "No history available";
        public const string FeedEmpty = "Nothing has happened yet";
        public const string SummaryTitle = "Summary";
        public const string ReplyAgentBusy = "The agent is working. Your message goes once it waits";
        public const string KeyboardToggle = "Keyboard";
        public const string Space = "Space";
        public const string FeedNewer = "Newer";
        public const string FeedOlder = "Older";

        /// <summary>The input bar's line while the agent waits for something.</summary>
        public static string WaitingForLine(string? waitingFor) => "Waiting for: " + Untrusted.Clamp(waitingFor, Untrusted.WaitingForMax);

        /// <summary>"▼ 3 new": entries that came below while the user was scrolled up.</summary>
        public static string FeedNewBelow(int n) => "▼ " + n + " new";

        /// <summary>A feed entry's label: the bridge's, else a word for its kind (ours).</summary>
        public static string FeedLabel(string kind, string label)
        {
            if (!string.IsNullOrWhiteSpace(label)) return label;
            switch (kind)
            {
                case "prompt": return "Your task";
                case "thinking": return "thinks";
                case "text": return "says";
                case "tool": return "tool";
                case "result": return "result";
                case "error": return "error";
                default: return "note";
            }
        }

        /// <summary>
        /// A feed text without a first word that repeats its label ("waits" + "Waits for input" shows "for input").
        /// </summary>
        public static string WithoutLabel(string text, string label)
        {
            if (string.IsNullOrEmpty(label) || text.Length <= label.Length) return text;
            if (!text.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return text;
            var next = text[label.Length];
            if (next != ' ' && next != ':') return text;
            var rest = text.Substring(label.Length).TrimStart(' ', ':');
            return rest.Length > 0 ? rest : text;
        }

        /// <summary>What the reply panel says when the bridge refused a reply with this error.</summary>
        public static string ReplyRefused(string error) => error == "rate-limited" ? ReplyTooSoon : ReplyNotAccepted;
        /// <summary>The demo agent's reaction to a reply when the pack has none of its own.</summary>
        public const string ReplyFallbackReaction = "Got it, I will do that.";

        /// <summary>A sid for the log: plain, at most 12 characters.</summary>
        public static string LogSid(string? sid) => sid == null ? "-" : Untrusted.Clamp(sid, 12);

        /// <summary>The reply panel's title.</summary>
        public static string ReplyTitle(string agentName) => "Reply to " + Untrusted.Clamp(agentName, Untrusted.NameMax);

        // live mode: pairing panel, connection status, waiting for the bridge
        public const string PairTitle = "Connect to my PC";
        public const string PairHint = "\"lotse connect\" shows the code and the address in the terminal.";
        public const string NewHost = "New address";
        public const string Back = "Back";
        public const string Ok = "OK";
        public const string Backspace = "\u2190";
        public const string Connecting = "Connecting…";
        public const string ConnectingWait = "Connecting… please wait";
        public const string Paired = "Paired";
        public const string ConnectionLost =
            "Connection lost. Open requests go to the terminal or come back after reconnecting.";
        public const string SentWaiting = "Sent …";
        public const string NeedCode = "Please enter the 6-digit code.";

        // ---- U11: pairing by looking at the QR code ----
        public const string UnreachableGaveUp = "Bridge not reachable. Check the address, then enter a new code.";
        public const string ScanButton = "Scan code on your monitor";
        public const string ScanInstruction = "Look at the code in your terminal";
        /// <summary>How to show the code: "Run  lotse connect  on your PC to show it", the command in the mono face.</summary>
        public const string ScanHowBefore = "Run  ";
        public const string ScanHowCommand = "lotse connect";
        public const string ScanHowAfter = "  on your PC to show it";
        /// <summary>While the one attempt to reach the PC of the last time runs.</summary>
        public const string Looking = "Looking for your PC…";
        public const string TypeInstead = "Type it instead";
        public const string ScanInstead = "Scan instead";
        public const string ScanAgain = "Scan again";
        /// <summary>The viewfinder at rest without anything to say (after "Cancel", after a pause).</summary>
        public const string ScanIdle = "Scanning is paused.";
        public const string ScanDenied = "Scanning needs the Spatial data permission. Enter the code instead.";
        public const string ScanFound = "Code found";
        /// <summary>Under the question: the bridge prints the address next to the QR code.</summary>
        public const string ScanCheck = "Check that your terminal shows the same address.";
        /// <summary>After a scanned code's one attempt did not pair.</summary>
        public const string ScanFailed = "That code did not work. Press Enter in the terminal for a new one, then scan again.";
        public const string Pair = "Pair";
        /// <summary>The question after a sighting. `host` is the parsed address literal (PairLink): digits and dots.</summary>
        public static string ScanAsk(string host) => "Pair with the PC at " + host + "?";
        public const string NeedHost = "Please pick an address first.";
        public const string BadHost = "Invalid address. Example: 192.168.1.20";
        public const string CodeRejected = "Code refused. Please enter the current code from the terminal.";
        public const string SessionExpired = "Pairing expired. Please enter a new code.";
        public const string CodeUnconfirmed = "No answer to the code. Please enter a new code.";
        public const string Disconnect = "Disconnect";

        // ---- settings (U4a: the wrist menu's "Einstellungen") ----
        public const string Settings = "Settings";
        public const string SettingsConnection = "Connection";
        public const string SettingsSound = "Sounds";
        public const string SettingsMotion = "Motion";
        public const string MotionNormal = "Normal";
        public const string MotionCalm = "Calm";
        public const string SoundOn = "On";
        public const string SoundOff = "Off";
        public const string Repair = "Pair again";
        public const string RepairConfirm = "Really pair again? The connection ends.";
        public const string Yes = "Yes";
        public const string NotConnected = "Not connected";
        public const string DemoRunning = "Demo running, no PC connected";

        /// <summary>The connection line in the settings: who we are connected to, or how far it got.</summary>
        public static string ConnectionLine(LinkState state, string? host)
        {
            var at = string.IsNullOrEmpty(host) ? "" : " to " + host;
            switch (state)
            {
                case LinkState.Live: return "Connected" + at;
                case LinkState.Connecting: return "Connecting" + at + " …";
                case LinkState.Retrying: return "Connection interrupted, trying again";
                case LinkState.NeedsCode: return "Code needed" + at;
                default: return NotConnected;
            }
        }
        public const string Approved = "Allowed";
        public const string NotTaken = "Not taken. The terminal asks by itself.";
        public const string SentUnclear = "Sent, result unclear. Please check the terminal.";

        /// <summary>
        /// What the block's place says once its request closed, from the bridge's perm_closed reason (never from what was
        /// sent): "allow" is the only approval, "deny" reads "Denied", a timeout sends the user to the terminal. A
        /// decision that went out and then lost its link may or may not have arrived: unclear, check the terminal.
        /// Anything else (hook-gone, a snapshot without it, ...) means the terminal asks by itself.
        /// </summary>
        public static string Closed(string reason, ClientDecision? sent)
        {
            switch (reason)
            {
                case "allow": return Approved;
                case "deny": return Denied;
                case "halt": return HaltDenied;
                case "timeout": return TimedOut;
                case "client-disconnected" when sent != null: return SentUnclear;
                default: return NotTaken;
            }
        }

        /// <summary>The bridge did not answer; `retryMs` until the next attempt.</summary>
        public static string Unreachable(double retryMs) =>
            $"Bridge not reachable. Trying again in {Math.Max(1, (int)Math.Ceiling(retryMs / 1000.0))} s.";

        /// <summary>"Code: 123 4__" while typing the 6-digit code.</summary>
        public static string CodeLine(string typed)
        {
            var padded = typed.PadRight(6, '_');
            return "Code: " + padded.Substring(0, 3) + " " + padded.Substring(3, 3);
        }

        /// <summary>"Address: 192.168.1.20", or a prompt when none is chosen yet.</summary>
        public static string HostLine(string? host) => "Address: " + (string.IsNullOrEmpty(host) ? "–" : host);

        /// <summary>The bridge refused a decision (bridge/src/broker.ts DecideResult errors), in plain English.</summary>
        public static string Refused(string error)
        {
            switch (error)
            {
                case "needs-two-hand": return "Please confirm with both hands.";
                case "unknown": return "The request has already ended.";
                default: return "The bridge refused.";
            }
        }

        /// <summary>Which gesture approves the block; follows PermRequest.NeedsTwoHand, the same rule GestureMachine uses.</summary>
        public static string GestureHint(PermRequest req) => req.NeedsTwoHand ? HintTwoHand : HintPinch;

        /// <summary>
        /// What an older bridge put in front of a Gemini explanation. An explanation carries no vendor prefix (U10): the
        /// headset says "Deny only" itself, in the block's instruction line.
        /// </summary>
        public const string OldGeminiPrefix = "Gemini (Deny only): ";

        static string WithoutVendorPrefix(string text) =>
            text.StartsWith(OldGeminiPrefix, StringComparison.Ordinal) ? text.Substring(OldGeminiPrefix.Length) : text;

        /// <summary>A Gemini request can only be denied from here (Gemini asks again in its own terminal after an allow).</summary>
        public static bool DenyOnly(string? source) => source == "gemini";

        /// <summary>
        /// The instruction line of a deny-only request: what the buttons and the gesture mean for it. The gesture and
        /// what it needs are unchanged; it only lets Gemini go on to its own question.
        /// </summary>
        public static string DenyOnlyHint(PermRequest req) =>
            "Deny only: Gemini asks again in its terminal. To let it ask, " + (req.NeedsTwoHand ? "hold both hands for 1.5 s" : "hold a pinch");

        /// <summary>What the hint says while the HoldGate holds a pinch back; null when nothing is held back.</summary>
        public static string? Cue(HoldCue cue)
        {
            switch (cue)
            {
                case HoldCue.RaiseHands: return CueRaiseHands;
                case HoldCue.Release: return CueRelease;
                default: return null;
            }
        }

        /// <summary>Why a block that would take a pinch needs both hands: it was cut for display. Null otherwise.</summary>
        public static string? TwoHandReason(PermRequest req) => req.Truncated && req.Risk.IsLowRisk ? TruncatedReason : null;

        /// <summary>The AI's extra line, labelled "AI".</summary>
        public static string AiLine(string aiText) => AiLabel + ": " + Untrusted.Clamp(aiText, Untrusted.ExplainMax);


        /// <summary>
        /// The raw thing the agent wants to do: the command, else the path(s), else the URL, else the tool;
        /// capped, with its control characters shown (Untrusted). "&lt;repo&gt;/" is dropped only in the offline
        /// demo (`stripRepo`), and only from the command and the paths; live, the bridge's text is shown as it is.
        /// </summary>
        public static string Subject(PermRequest req, bool stripRepo)
        {
            string s;
            if (!string.IsNullOrEmpty(req.Command))
                s = Untrusted.Clamp(stripRepo ? AgentStore.DisplayText(req.Command!) : req.Command, Untrusted.CommandMax);
            else if (!string.IsNullOrEmpty(req.Path)) s = PathLine(req.Path!, stripRepo);
            else if (req.Paths.Count > 0)
            {
                var lines = new List<string>();
                for (var i = 0; i < req.Paths.Count && i < Untrusted.MaxPaths; i++) lines.Add(PathLine(req.Paths[i], stripRepo));
                s = string.Join("\n", lines);
            }
            else if (!string.IsNullOrEmpty(req.Url)) s = Untrusted.Clamp(req.Url, Untrusted.UrlMax);
            else s = Untrusted.Clamp(req.Tool, Untrusted.ToolMax);
            if (req.Truncated) s += " … (shortened)";
            return s;
        }

        static string PathLine(string path, bool stripRepo) =>
            Untrusted.Clamp(stripRepo ? AgentStore.DisplayPath(path) : path, Untrusted.PathMax);

        static readonly Regex NoParseEnd = new Regex(@"<\s*/\s*noparse\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Second layer only: the slice shows untrusted text in TextMeshPro objects with rich text OFF
        /// (Ui.Text's default). Where untrusted text ever has to sit inside rich text, wrap it with this.
        /// Any closing noparse tag in the text (any case, any whitespace) would end the protection early,
        /// so it is defused: its angle brackets become the look-alikes U+2039/U+203A, which no parser reads as
        /// a tag (a replacement like "&lt;/ noparse&gt;" would itself match the whitespace-tolerant pattern).
        /// </summary>
        public static string Literal(string text) =>
            "<noparse>" + NoParseEnd.Replace(text, "\u2039/noparse\u203A") + "</noparse>";

        /// <summary>
        /// For untrusted text in a plain TextMeshPro object (rich text and control-character parsing off).
        /// As a second layer a word joiner (U+2060, invisible) follows every backslash, so no "\u0041",
        /// "\U0001F600" or "\n" typed as characters can ever be decoded by TMP.
        /// Every control, format and bidi character is shown as well (Untrusted.Visible), except the line
        /// feed, which is ours at this point: untrusted fields have theirs shown as "¶" already.
        /// </summary>
        public static string Plain(string text) => Untrusted.Visible(text, keepLineFeeds: true).Replace("\\", "\\\u2060");

        /// <summary>"4 minutes 12 seconds", "1 minute", "45 seconds"; rounded down to whole seconds.</summary>
        public static string Duration(double ms)
        {
            var total = ms <= 0 ? 0L : (long)Math.Floor(ms / 1000.0);
            var min = total / 60;
            var sec = total % 60;
            var parts = new List<string>();
            if (min > 0) parts.Add(min == 1 ? "1 minute" : $"{min} minutes");
            if (sec > 0 || min == 0) parts.Add(sec == 1 ? "1 second" : $"{sec} seconds");
            return string.Join(" ", parts);
        }

        /// <summary>The closing card's sentence (spec decision 8).</summary>
        public static string EndSummary(double totalWaitingMs, int dangerousStopped)
        {
            var stopped = dangerousStopped == 1
                ? "1 dangerous command stopped."
                : $"{dangerousStopped} dangerous commands stopped.";
            return $"Your agents waited for you for {Duration(totalWaitingMs)} in total. {stopped}";
        }

        /// <summary>The log's line for an agent that finished.</summary>
        public static string Review(string name, string summary) =>
            Untrusted.Clamp(name, Untrusted.NameMax) + ": " + Untrusted.Clamp(summary, Untrusted.ExplainMax);
    }
}
