using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IsleWin.Core;

namespace IsleWin.Claude;

public sealed class RelayCommand : ICommand
{
    readonly Action<object?> _run;
    public RelayCommand(Action<object?> run) { _run = run; }
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => _run(p);
}

/// One option row of the question on screen.
public sealed record OptionItem(string Label, string? Description, bool Picked, bool Multi);

public sealed record SessionItem(string Id, string Project, string StateText, Brush Brush, bool Pinned);

/// The chimes Isle plays for Claude: "needs you" (an approval, a question, a failure) and "turn finished".
public enum ClaudeSound { Alert, Done }

/// What a key could decide right now (null `Keys` = nothing). The window holds its hotkeys exactly while this is non-null.
/// Approval: Approve/Deny keys (`Reviewing`: they are Confirm/Back of the "Always..." review; `HasAlways`: Always... is offered).
/// Question: option keys 1..`Options` (the window caps at 4), and Approve is "Next/Done" when `MultiSelect`.
public sealed record PendingKeys(string RequestId, bool IsQuestion, bool HasAlways, bool Reviewing, int Options, bool MultiSelect);

public sealed class ClaudeViewModel : INotifyPropertyChanged
{
    readonly IIslandHost _host;
    readonly IslePaths _paths;
    readonly string _home;

    /// Raised once each time Isle would play a Claude chime: the island's session newly needs you, or finished a turn.
    public event Action<ClaudeSound>? Sound;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly List<FileSystemWatcher> _watchers = new();
    DispatcherTimer? _timer;
    int _scheduled;
    bool _started;

    string? _currentId, _pinnedId, _handledRequest, _lastQKey;
    SessionStatus? _cur;
    PendingRequest? _pending;
    int _qIndex;
    readonly List<HashSet<string>> _picks = new();
    readonly Dictionary<int, string> _typed = new();
    IReadOnlyList<OptionItem> _choices = Array.Empty<OptionItem>();
    bool _typing, _busy;
    string _answerText = "";
    bool _isDesktop;
    bool _needsAttention, _hasSession;
    int _livePid;
    string? _liveName;

    public ClaudeViewModel(IIslandHost host) : this(host, IslePaths.Default) { }

    public ClaudeViewModel(IIslandHost host, IslePaths paths)
    {
        _host = host; _paths = paths; _home = paths.UserHome;
        Allow = new RelayCommand(_ => Decide(true));
        Deny = new RelayCommand(_ => Decide(false));
        Always = new RelayCommand(_ => { if (ShowApproval && CanRemember && !_denying) { _reviewing = true; Notify(); } });
        ConfirmAlways = new RelayCommand(_ => ConfirmRemember());
        BackAlways = new RelayCommand(_ => { if (_reviewing) { _reviewing = false; Notify(); } });
        BeginDeny = new RelayCommand(_ => { if (ShowApproval && !_reviewing && !_busy) { _denying = true; Notify(); } });
        SubmitDeny = new RelayCommand(_ => SubmitDenial());
        CancelDeny = new RelayCommand(_ => { if (_denying) { _denying = false; Notify(); } });
        Answer = new RelayCommand(o => PickOption(o as string));
        Advance = new RelayCommand(_ => AdvanceQuestion());
        PreviousQuestion = new RelayCommand(_ => { if (_qIndex > 0 && !_busy) { _qIndex--; _typing = false; RebuildChoices(); Notify(); } });
        TypeSomething = new RelayCommand(_ => { _typing = !_typing; Notify(); });
        SubmitTyped = new RelayCommand(_ => SubmitTypedAnswer());
        AnswerInTerminal = new RelayCommand(_ => { if (_pending != null && HasButtons) Respond(ResponseBuilder.Terminal()); DoJump(); });
        Jump = new RelayCommand(_ => DoJump());
        SendReply = new RelayCommand(_ => _ = SendReplyAsync());
        Pin = new RelayCommand(o => { if (o is SessionItem s) { _userPicked = true; _pinnedId = _pinnedId == s.Id ? null : s.Id; _currentId = s.Id; Refresh(); } });
        // "+2 >": step to the next live session and stay on it
        NextSession = new RelayCommand(_ =>
        {
            if (Sessions.Count < 2) return;
            int i = Sessions.ToList().FindIndex(x => x.Id == _currentId);
            var next = Sessions[(i + 1) % Sessions.Count];
            _userPicked = true; _pinnedId = next.Id; _currentId = next.Id; Refresh();
        });
        _wordIndex = Random.Shared.Next(WorkingWords.Length);
        _wordTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => { _wordIndex = (_wordIndex + 1) % WorkingWords.Length; if (IsThinking && _liveVerb == null) Notify(); }, _ui);
        _wordTimer.Start();
    }

    // ---- Isle (macOS) look: words, colours, meta line ----
    // Isle's rotating working words, used until the terminal's own spinner word has been read.
    static readonly string[] WorkingWords =
    {
        "Thinking", "Coalescing", "Percolating", "Ruminating", "Cogitating", "Simmering", "Pondering", "Noodling", "Churning", "Brewing",
        "Conjuring", "Wrangling", "Synthesizing", "Contemplating", "Marinating", "Deliberating", "Computing", "Puzzling", "Finagling", "Vibing",
    };
    readonly DispatcherTimer _wordTimer;
    int _wordIndex;
    public string WorkingWord => _liveVerb ?? WorkingWords[_wordIndex];

    /// The one word on the collapsed island, as Isle words it.
    public string CollapsedWord => Limit != null ? "Limit" : State switch
    {
        ClaudeState.Working => WorkingWord + "…",
        ClaudeState.NeedsApproval or ClaudeState.NeedsQuestion => _pending?.Kind == RequestKind.Permission ? "Approve" : "Question",
        ClaudeState.WaitingInput => "Waiting",
        ClaudeState.Done => "Done",
        ClaudeState.Idle => "Ready",
        ClaudeState.Failed => "Error",
        ClaudeState.Compacting => "Compacting",
        _ => "",
    };

    /// Big word on the open face.
    public string Headline => Limit != null ? Limit.Title : State switch
    {
        ClaudeState.Working => WorkingWord + "…",
        ClaudeState.Compacting => "Compacting…",
        ClaudeState.NeedsApproval => _pending is { Kind: RequestKind.Permission } && _info != null
            ? (_info.IsPlan ? "Plan ready" : IsReviewing ? "Don't ask again?" : "Needs your approval") : "Needs approval",
        ClaudeState.NeedsQuestion => "Question",
        ClaudeState.WaitingInput => "Waiting",
        ClaudeState.Done => "Done",
        ClaudeState.Failed => "Error",
        ClaudeState.Idle => "Ready",
        _ => "No session",
    };

    /// The line under the headline.
    public string Detail => Limit != null ? Limit.Detail : State switch
    {
        ClaudeState.Done or ClaudeState.WaitingInput => DoneSummary,
        ClaudeState.Working or ClaudeState.Compacting => ToolLine,
        ClaudeState.Failed => ErrorText,
        ClaudeState.NeedsApproval or ClaudeState.NeedsQuestion when _pending == null => "Answer in your terminal",
        _ => "",
    };
    public bool HasDetail => Detail.Length > 0 && !ShowApproval && !ShowQuestion;

    /// "3s ago" beside the project.
    public string Ago => _cur == null ? "" : AgoShort(DateTimeOffset.UtcNow - _cur.UpdatedAt);
    static string AgoShort(TimeSpan t) => t.TotalSeconds < 60 ? $"{Math.Max(0, (int)t.TotalSeconds)}s ago" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m ago" : t.TotalHours < 24 ? $"{(int)t.TotalHours}h ago" : $"{(int)t.TotalDays}d ago";

    /// "+2" chip: the other live sessions.
    public string MoreText => Sessions.Count > 1 ? $"+{Sessions.Count - 1}" : "";
    public bool HasMore => Sessions.Count > 1;
    public string SessionsLine => Sessions.Count == 1 ? "1 session" : $"{Sessions.Count} sessions";
    public ICommand NextSession { get; }

    // the cover's colours, pushed in by the window so the marker can wear them like the waveform does
    public Color PalettePrimary { get; private set; } = Color.FromRgb(115, 115, 115);
    public Color PaletteSecondary { get; private set; } = Color.FromRgb(77, 77, 77);
    public Color PaletteAccent { get; private set; } = Color.FromRgb(153, 153, 153);
    public void SetPalette(Color primary, Color secondary, Color accent)
    {
        if (primary == PalettePrimary && secondary == PaletteSecondary && accent == PaletteAccent) return;
        PalettePrimary = primary; PaletteSecondary = secondary; PaletteAccent = accent; Notify();
    }
    public Brush MarkerBrush { get { var b = new SolidColorBrush(MarkerColor(PaletteAccent)); b.Freeze(); return b; } }

    public IReadOnlyList<OptionItem> QuestionChoices => _choices;
    public bool IsMultiSelect => CurrentQuestion?.MultiSelect == true;
    /// "2 of 3" for a multi-question ask.
    public string QuestionStep => _pending is { Kind: RequestKind.Question } p && p.Questions.Count > 1 ? $"{_qIndex + 1} of {p.Questions.Count}" : "";
    public bool HasQuestionStep => QuestionStep.Length > 0;
    public bool CanGoBack => _qIndex > 0 && HasQuestionStep;
    bool IsLastQuestion => _pending == null || _qIndex + 1 >= _pending.Questions.Count;
    public string AdvanceLabel => IsLastQuestion ? "Done" : "Next";
    /// Single-select answers on a click; a multi-select needs Next / Done.
    public bool ShowAdvance => IsMultiSelect && !_typing;
    public bool CanAdvance => _qIndex < _picks.Count && _picks[_qIndex].Count > 0;
    public bool ShowTypeBox => _typing;
    public bool ShowQuestionPills => !_typing;
    public string TypeSendLabel => IsLastQuestion ? "Send" : "Next";
    public string AnswerText { get => _answerText; set => _answerText = value; }
    /// A desktop-app session has no console: no reply box, and Jump raises the app instead of a terminal.
    public bool IsDesktop => _isDesktop;
    public bool CanReply => !_isDesktop;
    public string JumpLabel => _isDesktop ? "Claude" : "Terminal";
    public string TerminalAnswerLabel => _isDesktop ? "Answer in app" : "Answer in terminal";
    void DoJump() => TerminalBridge.Jump(_isDesktop ? TerminalBridge.FindDesktopWindow(_livePid) : TerminalBridge.FindHostWindow(_livePid));
    public bool HasQuestionHeader => !string.IsNullOrEmpty(QuestionHeader);

    /// The marker's colour per Isle's default marker designs (palette states use the cover's accent).
    public Color MarkerColor(Color accent) => Limit != null ? Color.FromRgb(0xFF, 0x9F, 0x0A) : State switch
    {
        ClaudeState.NeedsApproval or ClaudeState.NeedsQuestion => Color.FromRgb(0x0A, 0x84, 0xFF),
        ClaudeState.Done => Color.FromRgb(0x34, 0xC7, 0x59),
        ClaudeState.Failed => Color.FromRgb(0xFF, 0x3B, 0x30),
        ClaudeState.Disconnected => Color.FromRgb(0x8E, 0x8E, 0x93),
        _ => accent,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    // ---- exposed state ----
    public bool NeedsAttention => _needsAttention;
    public bool HasSession => _hasSession;
    public ClaudeState State => _cur?.State ?? ClaudeState.Disconnected;
    // Name the session after the folder it was started in (Claude Code's own record); hooks report the shell's
    // current folder, which drifts when commands cd elsewhere (e.g. into a build output folder).
    public string Project => _startProject ?? _cur?.Project ?? "";
    string? _startProject;
    public string StatusLine { get; private set; } = "";
    public string DoneSummary { get; private set; } = "";
    /// The claude.ai usage limit this session stopped on, if it did.
    public UsageLimit? Limit { get; private set; }
    /// The marker's design: a usage limit wears the error cross rather than Done's check.
    public ClaudeState MarkerState => Limit != null ? ClaudeState.Failed : State;
    /// "3 agents · Explore ×2, Plan" while the session has subagents running.
    public string AgentsLine { get; private set; } = "";
    public bool HasAgents => AgentsLine.Length > 0;
    public string OtherSessionsLine { get; private set; } = "";
    public IReadOnlyList<SessionItem> Sessions { get; private set; } = Array.Empty<SessionItem>();
    public Brush StateBrush => BrushFor(State);
    public bool IsThinking => State is ClaudeState.Working or ClaudeState.Compacting;
    /// The word Claude Code's own spinner shows in the terminal ("Pondering"); null until read, then the spinner picks one.
    public string? ThinkingVerb => State == ClaudeState.Compacting ? "Compacting" : _liveVerb;
    string? _liveVerb;
    DispatcherTimer? _verbTimer;
    bool _verbBusy;

    // While Claude is thinking, mirror the terminal's spinner word into the pill.
    void PollVerb()
    {
        if (!IsThinking || _livePid == 0 || _isDesktop) { if (_liveVerb != null) { _liveVerb = null; Notify(); } return; }
        if (_verbBusy) return;
        _verbBusy = true;
        int pid = _livePid;
        Task.Run(() => TerminalBridge.ReadSpinnerVerb(pid)).ContinueWith(t => _ui.BeginInvoke(() =>
        {
            _verbBusy = false;
            var v = t.IsCompletedSuccessfully ? t.Result : null;
            if (v != null && v != _liveVerb && pid == _livePid) { _liveVerb = v; Notify(); }
        }));
    }
    public string MiniLabel => State switch { ClaudeState.Working or ClaudeState.Compacting => "Working", ClaudeState.NeedsApproval => "Approve?", ClaudeState.NeedsQuestion => "Question", ClaudeState.Done or ClaudeState.WaitingInput => "Done", ClaudeState.Failed => "Failed", _ => "Idle" };

    public string? PendingTool => _pending?.ToolName;
    public string? PendingSummary => _pending?.ToolSummary;

    // ---- the approval card (Isle's ClaudeApproval) ----
    ApprovalInfo? _info;
    bool _reviewing, _denying;
    string _denyText = "";
    public bool IsPlan => _info?.IsPlan == true;
    /// "Always..." is offered: every suggestion can be spelled out (never for a plan).
    public bool CanRemember => _info?.Remember != null;
    public bool IsReviewing => _reviewing && CanRemember;
    public bool IsDenying => _denying && !IsReviewing;
    /// Plain row: Always... on offer, so Approve/Deny go title-only and Terminal icon-only.
    public bool ShowPlainRow => !IsReviewing && !IsDenying;
    public bool ShowAlways => CanRemember && ShowPlainRow;
    public bool ShowTerminalWord => !CanRemember;
    public string DenyPlaceholder => IsPlan ? "No, and tell Claude what to change…" : "No, and tell Claude what to do instead…";
    /// The reason typed so far; kept per request when Esc lets the field go.
    public string DenialText { get => _denyText; set { _denyText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSendDenial))); } }
    public bool CanSendDenial => _denyText.Trim().Length > 0;

    /// Card line 1: a bold part (tool label, or the review's changes), then the muted subject.
    public string CardLabel => IsReviewing ? ReviewLine1 : _info is { IsPlan: false } i ? i.Label : "";
    public string CardText => IsReviewing ? "" : _info?.Subject ?? _pending?.ToolSummary ?? "";
    public bool CardTail => IsPlan;
    public bool HasCardSecond => IsReviewing || !string.IsNullOrEmpty(_info?.Note);
    public string CardSecondBold => IsReviewing && _info!.Remember!.Changes.Count > 1 ? _info.Remember.Changes[^1] + " · " : "";
    public string CardSecond => IsReviewing ? _info!.Remember!.Scope : _info?.Note ?? "";
    /// Lines the card's text may take: 2, or 1 above a second line.
    public int CardMaxLines => HasCardSecond ? 1 : 2;
    /// Two lines are kept whenever there is a note or Always..., so the buttons don't move when the review opens.
    public double CardMinHeight => (HasCardSecond || CanRemember ? 2 : 1) * 14 + 12;
    /// The answer is being written: the row dims (a second click can't double-send).
    public bool IsAnswering => _busy;
    string ReviewLine1 { get { var c = _info!.Remember!.Changes; return string.Join(" · ", c.Count > 1 ? c.Take(c.Count - 1) : c); } }

    /// What a key could decide now; null = nothing pending (or a field has the keyboard).
    public PendingKeys? Keys
    {
        get
        {
            if (!Attn || !HasButtons || _busy || _pending == null) return null;
            if (_pending.Kind == RequestKind.Permission)
                return _denying && !IsReviewing ? null : new(_pending.RequestId, false, CanRemember, IsReviewing, 0, false);
            if (CurrentQuestion is not { } q || _typing) return null;
            return new(_pending.RequestId, true, false, false, q.Options.Count, q.MultiSelect);
        }
    }
    public string QuestionText => CurrentQuestion?.Text ?? "";
    public string? QuestionHeader => CurrentQuestion?.Header;
    Question? CurrentQuestion => _pending is { Kind: RequestKind.Question } p && _qIndex < p.Questions.Count ? p.Questions[_qIndex] : null;

    bool Attn => State is ClaudeState.NeedsApproval or ClaudeState.NeedsQuestion;
    bool HasButtons => _pending != null && _pending.RequestId != _handledRequest;
    public bool ShowApproval => Attn && HasButtons && _pending!.Kind == RequestKind.Permission;
    public bool ShowQuestion => Attn && HasButtons && _pending!.Kind == RequestKind.Question && CurrentQuestion != null;
    public bool ShowTerminalFallback => Attn && _pending == null;
    public bool ShowDone => State is ClaudeState.Done or ClaudeState.WaitingInput;
    public bool ShowWorking => State is ClaudeState.Working or ClaudeState.Compacting or ClaudeState.Idle;
    public bool ShowFailed => State == ClaudeState.Failed;
    public bool ShowToolLine => !string.IsNullOrEmpty(_cur?.Tool);
    public string ToolLine => _cur?.Tool is { Length: > 0 } t ? (string.IsNullOrEmpty(_cur.ToolTarget) ? t : $"{t} · {_cur.ToolTarget}") : "";
    public string ErrorText => _cur?.ErrorType ?? "unknown error";
    public bool HasOthers => OtherSessionsLine.Length > 0;

    string _replyText = "", _replyStatus = "";
    public string ReplyText { get => _replyText; set => _replyText = value; }
    public string ReplyStatus { get => _replyStatus; private set { _replyStatus = value; Notify(); } }

    public ICommand Allow { get; }
    public ICommand Deny { get; }
    public ICommand Always { get; }
    public ICommand ConfirmAlways { get; }
    public ICommand BackAlways { get; }
    public ICommand BeginDeny { get; }
    public ICommand SubmitDeny { get; }
    public ICommand CancelDeny { get; }
    public ICommand Answer { get; }
    public ICommand Advance { get; }
    public ICommand PreviousQuestion { get; }
    public ICommand TypeSomething { get; }
    public ICommand SubmitTyped { get; }
    public ICommand AnswerInTerminal { get; }
    public ICommand Jump { get; }
    public ICommand SendReply { get; }
    public ICommand Pin { get; }

    /// Views call this from focus events.
    public void KeyboardFocus(bool on) => _host.RequestKeyboard(on);

    // ---- lifecycle ----
    public void Start()
    {
        if (_started) return;
        _started = true;
        try { Directory.CreateDirectory(_paths.Sessions); Directory.CreateDirectory(_paths.Requests); Directory.CreateDirectory(Path.GetDirectoryName(_paths.AnsweringMarker)!); } catch { }
        try { AnsweringMarker.Write(_paths, Environment.ProcessId); } catch { }
        foreach (var dir in new[] { _paths.Sessions, _paths.Requests, _paths.ClaudeSessions })
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var w = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size, InternalBufferSize = 32768 };
                w.Changed += (_, _) => Schedule(); w.Created += (_, _) => Schedule(); w.Deleted += (_, _) => Schedule(); w.Renamed += (_, _) => Schedule();
                w.Error += (_, _) => Schedule();
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch { }
        }
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Refresh(), _ui);
        _timer.Start();
        _verbTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => PollVerb(), _ui);
        _verbTimer.Start();
        Refresh();
    }

    public void Stop()
    {
        _started = false;
        _timer?.Stop(); _timer = null;
        _verbTimer?.Stop(); _verbTimer = null;
        foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        _watchers.Clear();
        try { AnsweringMarker.Remove(_paths); } catch { }
    }

    void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) != 0) return;
        _ = Task.Delay(75).ContinueWith(_ => _ui.BeginInvoke(() => { Interlocked.Exchange(ref _scheduled, 0); Refresh(); }));
    }

    // ---- refresh ----
    /// Re-reads everything now (the watchers and a 5 s timer do this on their own).
    public void RefreshNow() => Refresh();

    void Refresh()
    {
        if (!_started) return;
        try { RefreshCore(); } catch { }
    }

    void RefreshCore()
    {
        var now = DateTimeOffset.UtcNow;
        var records = RegistryStore.ReadAll(_paths);
        // Desktop-app sessions are followed too (hooks and answers work the same); they just have no console to type into.
        var alive = records.Where(r => RegistryStore.IsAlive(r)).ToList();
        // Hook files carry the detail; Claude Code's own records fill in live sessions with none.
        var hooked = StatusStore.ReadAll(_paths);
        var hookedIds = hooked.Select(s => s.SessionId).ToHashSet();
        var statuses = hooked.Concat(alive.Where(r => r.SessionId != null && !hookedIds.Contains(r.SessionId))
                                         .Select(r => RegistryStore.ToStatus(r, now))).ToList();
        IReadOnlySet<string>? liveIds = records.Count == 0 ? null : alive.Where(r => r.SessionId != null).Select(r => r.SessionId!).ToHashSet();

        var live = statuses.Where(s => liveIds != null ? liveIds.Contains(s.SessionId) : now - s.UpdatedAt < TimeSpan.FromMinutes(30))
            .OrderBy(s => SessionRanker.Rank(s.State)).ThenByDescending(s => s.UpdatedAt).ToList();
        var pendingAll = RequestStore.ReadPending(_paths);

        var cur = SessionRanker.Select(statuses, liveIds, _currentId, _pinnedId, now);
        if (cur != null && _pinnedId == null)
        {
            // a live session whose hook is waiting for us beats everything else
            var waiting = live.FirstOrDefault(s => pendingAll.Any(p => p.SessionId == s.SessionId));
            if (waiting != null && !pendingAll.Any(p => p.SessionId == cur.SessionId)) cur = waiting;
        }
        _cur = cur; _currentId = cur?.SessionId;
        _pending = cur == null ? null : pendingAll.Where(p => p.SessionId == cur.SessionId).OrderBy(p => p.CreatedAt).FirstOrDefault();

        var rec = cur == null ? null : alive.FirstOrDefault(r => r.SessionId == cur.SessionId);
        _livePid = rec?.Pid ?? 0; _liveName = rec?.Name;
        _isDesktop = rec != null && TerminalBridge.IsDesktopSession(_paths.ClaudeSessions, rec.Pid);
        _startProject = string.IsNullOrEmpty(rec?.Cwd) ? null : Path.GetFileName(rec.Cwd.TrimEnd('\\', '/'));
        _hasSession = live.Count > 0;

        // question progress resets when the request changes
        var key = _pending?.RequestId;
        if (key != _lastQKey)
        {
            _lastQKey = key; _qIndex = 0; _typing = false; _busy = false; _answerText = ""; _typed.Clear(); _picks.Clear();
            _reviewing = false; _denying = false; _denyText = ""; _info = null;
            if (_pending is { Kind: RequestKind.Permission } pp)
                _info = ClaudeText.Approval(RequestStore.ReadPayload(pp), _home);
            if (_pending is { Kind: RequestKind.Question } qp) foreach (var _ in qp.Questions) _picks.Add(new());
        }
        RebuildChoices();

        var others = live.Where(s => s.SessionId != cur?.SessionId).ToList();
        int working = others.Count(s => s.State is ClaudeState.Working or ClaudeState.Compacting);
        OtherSessionsLine = working > 0 ? $"+{working} working" : others.Count > 0 ? $"+{others.Count} more" : "";
        Sessions = live.Select(s => new SessionItem(s.SessionId, s.Project ?? "?", s.State.ToString(), BrushFor(s.State), s.SessionId == _pinnedId)).ToList();

        StatusLine = BuildStatus(cur, now);
        DoneSummary = ""; Limit = null;
        bool over = cur != null && cur.State is ClaudeState.Done or ClaudeState.WaitingInput or ClaudeState.Failed;
        if (over && cur!.TranscriptPath != null) Limit = TranscriptReader.CurrentLimit(cur.TranscriptPath);
        if (over && Limit == null && cur!.State != ClaudeState.Failed)
            DoneSummary = ClaudeText.DoneLine(cur.LastMessage ?? (cur.TranscriptPath != null ? TranscriptReader.LastAssistantText(cur.TranscriptPath) : null));
        // a session whose process is gone has no agents, whatever its record says
        AgentsLine = rec != null ? ClaudeText.SubagentLine(cur?.Subagents) : "";

        RaiseSound(cur);

        bool attn = Attn;
        bool changed = attn != _needsAttention;
        _needsAttention = attn;
        Notify();
        if (changed) { _host.RequestOpen(attn); if (attn) _host.ShowFace(IslandFace.Claude); }
    }

    // Isle's playSound(for:): on a genuine change of the island's state, never at launch and never for a session
    // the user just picked. A usage-limit failure is silent (nothing to do but wait).
    bool _soundSeen, _userPicked;
    ClaudeState _soundState;
    void RaiseSound(SessionStatus? cur)
    {
        var state = cur?.State ?? ClaudeState.Disconnected;
        bool picked = _userPicked; _userPicked = false;
        bool baseline = !_soundSeen; _soundSeen = true;
        if (state == _soundState) return;
        _soundState = state;
        if (baseline || picked) return;
        switch (state)
        {
            case ClaudeState.NeedsApproval or ClaudeState.NeedsQuestion: Sound?.Invoke(ClaudeSound.Alert); break;
            case ClaudeState.Failed when Limit == null: Sound?.Invoke(ClaudeSound.Alert); break;
            case ClaudeState.Done: Sound?.Invoke(ClaudeSound.Done); break;
        }
    }

    static string BuildStatus(SessionStatus? s, DateTimeOffset now) => s == null ? "No session" : s.State switch
    {
        ClaudeState.Working => string.IsNullOrEmpty(s.Tool) ? "Working" : "Running " + s.Tool + (string.IsNullOrEmpty(s.ToolTarget) ? "" : " · " + s.ToolTarget),
        ClaudeState.Compacting => "Compacting context",
        ClaudeState.NeedsApproval => "Needs approval",
        ClaudeState.NeedsQuestion => "Has a question",
        ClaudeState.WaitingInput => "Waiting for you · " + AgoLong(now - s.UpdatedAt),
        ClaudeState.Done => "Done · " + AgoLong(now - s.UpdatedAt),
        ClaudeState.Failed => "Failed" + (string.IsNullOrEmpty(s.ErrorType) ? "" : " · " + s.ErrorType),
        _ => "Idle",
    };

    static string AgoLong(TimeSpan t) => t.TotalSeconds < 60 ? "just now" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m ago" : t.TotalHours < 24 ? $"{(int)t.TotalHours}h ago" : $"{(int)t.TotalDays}d ago";

    static Brush BrushFor(ClaudeState s)
    {
        var c = s switch
        {
            ClaudeState.Working or ClaudeState.Compacting => "#D85A30",
            ClaudeState.NeedsApproval or ClaudeState.NeedsQuestion => "#EF9F27",
            ClaudeState.Done or ClaudeState.WaitingInput => "#5DCAA5",
            ClaudeState.Failed => "#E24B4A",
            _ => "#888780",
        };
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(c)!; b.Freeze(); return b;
    }

    // ---- commands ----
    /// Writes the answer. False when nothing reached the hook: the request is gone (it gave up, or the terminal
    /// answered first) or the write failed.
    bool Respond(string json)
    {
        if (_pending == null || !HasButtons) return false;
        bool ok = File.Exists(_pending.FilePath);
        if (ok) try { RequestStore.WriteResponse(_paths, _pending.RequestId, json); } catch { ok = false; }
        if (ok) { _handledRequest = _pending.RequestId; _busy = false; _denying = false; }
        Notify();
        return ok;
    }

    /// Approve / Deny from the card. A plan's approval carries the tool input back; a denial says who denied it.
    bool Decide(bool allow, string? words = null)
    {
        if (!ShowApproval || _busy) return false;
        string json;
        if (!allow) json = ResponseBuilder.Deny(ResponseBuilder.DenyMessage(IsPlan, words));
        else if (IsPlan) json = ResponseBuilder.AllowPlan(RequestStore.ReadToolInput(_pending!) ?? new System.Text.Json.Nodes.JsonObject());
        else json = ResponseBuilder.Allow();
        return Respond(json);
    }

    /// "Always..." -> Confirm: Claude Code's own suggestions go back exactly as they came.
    bool ConfirmRemember()
    {
        if (!ShowApproval || !IsReviewing || _busy) return false;
        if (RequestStore.ReadPayload(_pending!)?["permission_suggestions"] is not { } s) return false;
        return Respond(ResponseBuilder.AllowAlways(s));
    }

    void SubmitDenial()
    {
        var words = _denyText.Trim();
        if (words.Length == 0 || !IsDenying) return;
        if (Decide(false, words)) { _denyText = ""; _host.RequestKeyboard(false); }
    }

    // ---- keyboard decisions (the window registers the hotkeys; these decide as Isle's shortcuts do) ----
    void ShortcutToast(string word, string glyph, string? tint)
    {
        var project = Project;
        string text = word;
        if (project.Length > 0)
        {
            var suffix = " · " + word.ToLowerInvariant();
            const int Max = 30;
            if (project.Length + suffix.Length > Max) project = project[..Math.Max(1, Max - suffix.Length - 3)].TrimEnd() + "...";
            text = project + suffix;
        }
        _host.Toast(new IslandToast("shortcut", text, glyph, tint));
    }
    void Approved(bool ok) { if (ok) ShortcutToast("Approved", IsleStyle.CheckCircle, "#34C759"); else NotSent(); }
    void NotSent() => ShortcutToast("Not sent", IsleStyle.ExclaimCircle, "#FF9F0A");
    bool Answered(string id) { if (_handledRequest == id) ShortcutToast("Answered", IsleStyle.CheckCircle, null); return true; }

    /// Approve's key: approves; in the "Always..." review it confirms; on a multi-select question it is Next / Done.
    public bool KeyApprove()
    {
        if (Keys is not { } k) return false;
        if (k.IsQuestion)
        {
            if (!k.MultiSelect || !CanAdvance) return false;
            AdvanceQuestion();
            return Answered(k.RequestId);
        }
        if (k.Reviewing)
        {
            if (!ConfirmRemember()) { NotSent(); return true; }
            ShortcutToast("Always allowed", IsleStyle.CheckCircle, "#34C759");
            return true;
        }
        Approved(Decide(true));
        return true;
    }

    /// Deny's key: denies; in the "Always..." review it is Back.
    public bool KeyDeny()
    {
        if (Keys is not { IsQuestion: false } k) return false;
        if (k.Reviewing) { _reviewing = false; Notify(); return true; }
        if (Decide(false)) ShortcutToast("Denied", IsleStyle.XCircle, "#FF453A"); else NotSent();
        return true;
    }

    /// An option's key (1-based): single-select answers; multi-select toggles (Approve's key submits).
    public bool KeyOption(int n)
    {
        if (Keys is not { IsQuestion: true } k || n < 1 || n > k.Options || CurrentQuestion is not { } q) return false;
        PickOption(q.Options[n - 1].Label);
        return Answered(k.RequestId);
    }

    void RebuildChoices() =>
        _choices = CurrentQuestion is { } q && _qIndex < _picks.Count
            ? q.Options.Select(o => new OptionItem(o.Label, o.Description, _picks[_qIndex].Contains(o.Label), q.MultiSelect)).ToList()
            : Array.Empty<OptionItem>();

    /// A click on an option: single-select answers (and moves on); multi-select toggles.
    void PickOption(string? label)
    {
        if (_pending is not { Kind: RequestKind.Question } || label == null || CurrentQuestion is not { } q || _busy || _qIndex >= _picks.Count) return;
        _typed.Remove(_qIndex); _typing = false;
        var set = _picks[_qIndex];
        if (q.MultiSelect) { if (!set.Remove(label)) set.Add(label); RebuildChoices(); Notify(); }
        else { set.Clear(); set.Add(label); AdvanceQuestion(); }
    }

    void SubmitTypedAnswer()
    {
        var text = _answerText.Trim();
        if (text.Length == 0 || _pending is not { Kind: RequestKind.Question } || _busy || _qIndex >= _picks.Count) return;
        _typed[_qIndex] = text; _picks[_qIndex].Clear();
        _answerText = ""; _typing = false; _host.RequestKeyboard(false);
        AdvanceQuestion();
    }

    /// Next / Done: move past the current question once it has an answer; after the last, send them all.
    void AdvanceQuestion()
    {
        if (_pending is not { Kind: RequestKind.Question } p || _busy || _qIndex >= _picks.Count) return;
        if (_picks[_qIndex].Count == 0 && !_typed.ContainsKey(_qIndex)) return;
        if (_qIndex + 1 < p.Questions.Count) { _qIndex++; _typing = false; RebuildChoices(); Notify(); return; }
        // Keyed by question text; typed text goes in as written, picks in the order offered.
        var answers = new Dictionary<string, string>();
        for (int i = 0; i < p.Questions.Count && i < _picks.Count; i++)
            answers[p.Questions[i].Text] = _typed.TryGetValue(i, out var t) ? t : ResponseBuilder.JoinPicks(p.Questions[i], _picks[i]);
        var input = RequestStore.ReadToolInput(p) ?? new System.Text.Json.Nodes.JsonObject();
        _busy = true;
        if (!Respond(ResponseBuilder.Answers(input, answers))) { _busy = false; Notify(); }
    }

    async Task SendReplyAsync()
    {
        var text = _replyText.Trim();
        if (text.Length == 0 || _cur == null) return;
        if (_livePid == 0) { ReplyStatus = "No running Claude process found"; return; }
        var cur = _cur;
        try
        {
            var r = await TerminalBridge.PasteReply(_livePid, text, _liveName, cur.Project);
            if (r != PasteResult.Sent) { ReplyStatus = "Couldn't reach that Claude terminal"; return; }
            _replyText = ""; ReplyStatus = "Sent…"; // box keeps focus so you can keep prompting
            bool ok = false;
            if (cur.TranscriptPath != null)
                for (int i = 0; i < 20 && !ok; i++) { await Task.Delay(500); ok = await Task.Run(() => TranscriptReader.ContainsUserText(cur.TranscriptPath, text)); }
            ReplyStatus = ok ? "Delivered" : "Typed into the terminal (Claude may still be busy)";
        }
        catch (Exception e) { ReplyStatus = "Reply failed: " + e.Message; }
    }
}
