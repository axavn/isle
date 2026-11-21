using System.Windows.Controls;
using System.Windows.Input;

namespace IsleWin.Claude;

public partial class ClaudeView : UserControl
{
    public ClaudeView() { InitializeComponent(); }
    ClaudeViewModel? Vm => DataContext as ClaudeViewModel;

    void OnFocus(object s, KeyboardFocusChangedEventArgs e) => Vm?.KeyboardFocus(true);
    void OnLostFocus(object s, KeyboardFocusChangedEventArgs e) => Vm?.KeyboardFocus(false);
    void OnMouseDown(object s, MouseButtonEventArgs e) => Vm?.KeyboardFocus(true);
    void OnKey(object s, KeyEventArgs e) { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Send(); } }
    void OnSend(object s, System.Windows.RoutedEventArgs e) => Send();
    // Don't drop focus here: it raced the jump to the terminal. SendReplyAsync releases it after a paste,
    // and on a failed/confirm send the box stays focused so you can just press Enter again.
    void Send() => Vm?.SendReply.Execute(null);
}
