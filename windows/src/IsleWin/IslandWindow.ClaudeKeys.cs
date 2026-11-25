using IsleWin.Claude;
using IsleWin.Core;

namespace IsleWin;

/// Isle's Claude shortcuts (ShortcutPlan), system-wide but only while a request is waiting:
/// - Approve Ctrl+' and Deny Ctrl+Esc for an approval; in the "Always…" review they are Confirm and Back.
/// - Alt+1–4 pick a question's options; on a multi-select question Approve's key is Next / Done.
/// The keys are taken 0.5 s after the request appears, so a keystroke already on its way to another app
/// reaches it, and given back once it's answered or a field has the keyboard. If Windows won't let Ctrl+Esc
/// go (the shell may hold it for Start), Deny falls back to Ctrl+Shift+'.
/// A press while the Claude face isn't in view only brings it into view (Deny excepted: it always denies);
/// the review's keys also wait until the face has been in view for 0.5 s.
public partial class IslandWindow
{
    const int HotApprove = 0x1520, HotDeny = 0x1521, HotOption1 = 0x1522; // options: 0x1522..0x1525
    const int MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, VK_OEM_7 = 0xDE, VK_ESCAPE = 0x1B;
    const double ArmDelay = 0.5, SeenDelay = 0.5;

    int _heldKeys;          // bit per id above, the keys actually registered
    string? _armId; double _armAt, _claudeSeenAt = -1;

    /// Per frame: track how long the Claude face has been in view, and hold the keys the request wants.
    void ClaudeKeysFrame(double t)
    {
        bool inView = _open && _place == IslandFace.Claude && !_dropping;
        if (!inView) _claudeSeenAt = -1; else if (_claudeSeenAt < 0) _claudeSeenAt = t;

        var k = Claude.Keys;
        if (k != null && k.RequestId != _armId) { _armId = k.RequestId; _armAt = t + ArmDelay; }
        int want = 0;
        if (k != null && t >= _armAt)
        {
            if (!k.IsQuestion) want = Bit(HotApprove) | Bit(HotDeny);
            else
            {
                for (int i = 0; i < Math.Min(k.Options, 4); i++) want |= Bit(HotOption1 + i);
                if (k.MultiSelect) want |= Bit(HotApprove);
            }
        }
        if (want != _heldKeys) HoldKeys(want);
    }

    static int Bit(int id) => 1 << (id - HotApprove);

    /// `_heldKeys` is what was asked for; a combination another app holds just isn't taken (its button still
    /// works), and giving back a key that was never taken is harmless.
    void HoldKeys(int want)
    {
        if (_hwnd == IntPtr.Zero) return;
        for (int id = HotApprove; id <= HotOption1 + 3; id++)
        {
            int b = Bit(id);
            if ((_heldKeys & b) != 0 && (want & b) == 0) UnregisterHotKey(_hwnd, id);
            else if ((_heldKeys & b) == 0 && (want & b) != 0)
                _ = id switch
                {
                    HotApprove => RegisterHotKey(_hwnd, id, MOD_CONTROL | MOD_NOREPEAT, VK_OEM_7),
                    HotDeny => RegisterHotKey(_hwnd, id, MOD_CONTROL | MOD_NOREPEAT, VK_ESCAPE)
                               || RegisterHotKey(_hwnd, id, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_OEM_7),
                    _ => RegisterHotKey(_hwnd, id, MOD_ALT | MOD_NOREPEAT, 0x31 + (id - HotOption1)),
                };
        }
        _heldKeys = want;
    }

    /// WM_HOTKEY for one of these: reveal or decide.
    bool ClaudeKeyPressed(int id)
    {
        if (id < HotApprove || id > HotOption1 + 3) return false;
        var k = Claude.Keys;
        if (k == null || _carrying) return true;
        double t = _clock.Elapsed.TotalSeconds;
        bool hidden = _claudeSeenAt < 0, seen = !hidden && t - _claudeSeenAt >= SeenDelay;
        bool deny = id == HotDeny;

        if (k.Reviewing) { if (!seen) Reveal(); else if (deny) Claude.KeyDeny(); else Claude.KeyApprove(); }
        else if (!k.IsQuestion && deny) Claude.KeyDeny();
        else if (hidden) Reveal();
        else if (id == HotApprove) Claude.KeyApprove();
        else Claude.KeyOption(id - HotOption1 + 1);
        return true;
    }

    /// Bring the Claude face into view where you are, and nothing else; its seen clock starts again.
    void Reveal()
    {
        _hold = true; // held open until the request is answered, as an alert's own opening is
        if (_place != IslandFace.Claude) ShowPlace(IslandFace.Claude);
        Open();
        _claudeSeenAt = _clock.Elapsed.TotalSeconds;
    }

    /// Isle's Claude alert and done sounds, each off until switched on.
    void OnClaudeSound(ClaudeSound s)
    {
        var o = IsleSettings.Current.Sounds;
        if (s == ClaudeSound.Alert && o.ClaudeAlertEnabled) IsleWin.Media.Sounds.Play(o.ClaudeAlert);
        if (s == ClaudeSound.Done && o.ClaudeDoneEnabled) IsleWin.Media.Sounds.Play(o.ClaudeDone);
    }
}
