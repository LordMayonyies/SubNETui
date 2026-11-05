using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Sub.NETUI.UI;

internal sealed class StatusBar : View
{
    private readonly Label _statusLabel;

    public StatusBar()
    {
        _statusLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            Text = "Espacio Pausa  Ctrl+S Detener  N/P Siguiente/Anterior  Tab Panel  Ctrl+Q Salir"
        };
        Add(_statusLabel);
    }

    public void SetStatus(string? message)
    {
        _statusLabel.Text = string.IsNullOrWhiteSpace(message)
            ? "Espacio Pausa  Ctrl+S Detener  N/P Siguiente/Anterior  Tab Panel  Ctrl+Q Salir"
            : message;
    }
}