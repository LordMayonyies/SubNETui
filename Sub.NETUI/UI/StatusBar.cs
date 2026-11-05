using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Sub.NETUI.UI;

internal sealed class StatusBar : View
{
    private const string HelpText =
        "[Enter/Esc] Navigate  [0-9] Select track  [Ctrl+Space] Pause  [Ctrl+S] Stop  [Ctrl+N/P] Skip  [Ctrl+Q] Quit";

    private readonly Label _statusLabel;

    public StatusBar()
    {
        _statusLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            Text = HelpText
        };
        Add(_statusLabel);
    }

    public void SetStatus(string? message)
    {
        _statusLabel.Text = string.IsNullOrWhiteSpace(message) ? HelpText : message;
    }
}
