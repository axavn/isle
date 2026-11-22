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

public sealed record SessionItem(string Id, string Project, string StateText, Brush Brush, bool Pinned);

public sealed class ClaudeViewModel : INotifyPropertyChanged
{
    readonly IIslandHost _host;
    readonly IslePaths _paths = IslePaths.Default;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly List<FileSystemWatcher> _watchers = new();
    DispatcherTimer? _timer;
    int _scheduled;
    bool _started;

    string? _currentId, _pinnedId, _handledRequest, _lastQKey;
    SessionStatus? _cur;
    PendingRequest? _pending;
    int _qIndex;
    readonly Dictionary<string, string> _answers = new();
    IReadOnlyList<string> _options = Array.Empty<string>();
    bool _needsAttention, _hasSession;
    int _livePid;
    string? _liveName;

    public ClaudeViewModel(IIslandHost host)
    {
        _host = host;
        Allow = new RelayCommand(_ => Respond(ResponseBuilder.Allow()));
        Deny = new RelayCommand(_ => Respond(ResponseBuilder.Deny("Denied from the island")));
        Answer = new RelayCommand(o => AnswerQuestion(o as string));
        Jump = new RelayCommand(_ => TerminalBridge.Jump(TerminalBridge.FindHostWindow(_livePid)));
        SendReply = new RelayCommand(_ => _ = SendReplyAsync());
        Pin = new RelayCommand(o => { if (o is SessionItem s) { _pinnedId = _pinnedId == s.Id ? null : s.Id; _currentId = s.Id; Refresh(); } });
        // "+2 >": step to the next live session and stay on it
        NextSession = new RelayCommand(_ =>
        {
            if (Sessions.Count < 2) return;
            int i = Sessions.ToList().FindIndex(x => x.Id == _currentId);
            var next = Sessions[(i + 1) % Sessions.Count];
            _pinnedId = next.Id; _currentId = next.Id; Refresh();
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
        ClaudeState.NeedsApproval => _pending?.ToolName is { Length: > 0 } t ? $"Allow {t}?" : "Needs approval",
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

    public IReadOnlyList<QuestionOption> QuestionChoices => CurrentQuestion?.Options ?? (IReadOnlyList<QuestionOption>)Array.Empty<QuestionOption>();
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
        if (!IsThinking || _livePid == 0) { if (_liveVerb != null) { _liveVerb = null; Notify(); } return; }
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
    public string QuestionText => CurrentQuestion?.Text ?? "";
    public string? QuestionHeader => CurrentQuestion?.Header;
    public IReadOnlyList<string> QuestionOptions => _options;
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
    public ICommand Answer { get; }
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
    void Refresh()
    {
        if (!_started) return;
        try { RefreshCore(); } catch { }
    }

    void RefreshCore()
    {
        var now = DateTimeOffset.UtcNow;
        var records = RegistryStore.ReadAll(_paths);
        // Desktop-app sessions have no terminal to type into, so the pill only follows CLI sessions.
        var alive = records.Where(r => RegistryStore.IsAlive(r) && !TerminalBridge.IsDesktopSession(_paths.ClaudeSessions, r.Pid)).ToList();
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
        _startProject = string.IsNullOrEmpty(rec?.Cwd) ? null : Path.GetFileName(rec.Cwd.TrimEnd('\\', '/'));
        _hasSession = live.Count > 0;

        // question progress resets when the request changes
        var key = _pending?.RequestId;
        if (key != _lastQKey) { _lastQKey = key; _qIndex = 0; _answers.Clear(); }
        _options = CurrentQuestion?.Options.Select(o => o.Label).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

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

        bool attn = Attn;
        bool changed = attn != _needsAttention;
        _needsAttention = attn;
        Notify();
        if (changed) { _host.RequestOpen(attn); if (attn) _host.ShowFace(IslandFace.Claude); }
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
    void Respond(string json)
    {
        if (_pending == null) return;
        _handledRequest = _pending.RequestId;
        try { RequestStore.WriteResponse(_paths, _pending.RequestId, json); } catch { }
        Notify();
    }

    void AnswerQuestion(string? label)
    {
        if (_pending is not { Kind: RequestKind.Question } p || label == null || CurrentQuestion is not { } q) return;
        _answers[q.Text] = label;
        _qIndex++;
        if (_qIndex >= p.Questions.Count)
        {
            var input = RequestStore.ReadToolInput(p) ?? new System.Text.Json.Nodes.JsonObject();
            Respond(ResponseBuilder.Answers(input, new Dictionary<string, string>(_answers)));
        }
        else
        {
            _options = CurrentQuestion?.Options.Select(o => o.Label).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
            Notify();
        }
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
