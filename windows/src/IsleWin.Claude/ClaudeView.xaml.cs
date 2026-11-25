using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace IsleWin.Claude;

public partial class ClaudeView : UserControl
{
    public ClaudeView() { InitializeComponent(); }
    ClaudeViewModel? Vm => DataContext as ClaudeViewModel;

    void OnFocus(object s, KeyboardFocusChangedEventArgs e) => Vm?.KeyboardFocus(true);
    void OnLostFocus(object s, KeyboardFocusChangedEventArgs e) => Vm?.KeyboardFocus(false);
    void OnMouseDown(object s, MouseButtonEventArgs e) => Vm?.KeyboardFocus(true);
    void OnKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Send(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Release(); }
    }

    /// Isle's click-out: a press on the island outside the reply field lets the field go, as Esc does.
    /// The draft stays in the box; the keyboard goes back to the window it came from.
    public void ClickOut(DependencyObject? pressed)
    {
        if (!Reply.IsKeyboardFocused && !AnswerBox.IsKeyboardFocused && !DenyBox.IsKeyboardFocused) return;
        for (var d = pressed; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == Reply || d == AnswerBox || d == DenyBox) return;
        if (DenyBox.IsKeyboardFocused) Vm?.CancelDeny.Execute(null);
        Release();
    }

    // "Type something": the answer box takes the keyboard as it appears.
    void OnAnswerVisible(object s, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) Dispatcher.BeginInvoke(() => { Vm?.KeyboardFocus(true); AnswerBox.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
    }
    void OnAnswerKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Vm?.SubmitTyped.Execute(null); }
        else if (e.Key == Key.Escape) { e.Handled = true; Vm?.TypeSomething.Execute(null); Release(); }
    }

    // Deny with a reason: the field takes the keyboard as it appears; Return sends, Esc lets go and keeps the draft.
    void OnDenyVisible(object s, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) Dispatcher.BeginInvoke(() => { Vm?.KeyboardFocus(true); DenyBox.Focus(); DenyBox.CaretIndex = DenyBox.Text.Length; }, System.Windows.Threading.DispatcherPriority.Input);
    }
    void OnDenyKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Vm?.SubmitDeny.Execute(null); Release(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Vm?.CancelDeny.Execute(null); Release(); }
    }

    void Release() { Keyboard.ClearFocus(); Vm?.KeyboardFocus(false); }
    void OnSend(object s, System.Windows.RoutedEventArgs e) => Send();
    // Don't drop focus here: it raced the jump to the terminal. SendReplyAsync releases it after a paste,
    // and on a failed/confirm send the box stays focused so you can just press Enter again.
    void Send() => Vm?.SendReply.Execute(null);
}
