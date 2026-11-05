using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Sub.NETUI.UI;

internal sealed class NowPlayingPane : View
{
    private readonly Label _trackLabel;
    private readonly Label _timeLabel;
    private readonly ProgressBar _progressBar;

    public NowPlayingPane()
    {
        Title = "Now Playing";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Single;

        _trackLabel = new Label { X = 1, Y = 0, Width = Dim.Fill(2) };
        _progressBar = new ProgressBar { X = 1, Y = 1, Width = Dim.Fill(2) };
        _timeLabel = new Label { X = 1, Y = 2, Width = Dim.Fill(2) };
        Add(_trackLabel, _progressBar, _timeLabel);
        SetMessage("Nothing is playing");
    }

    public void SetMessage(string message)
    {
        _trackLabel.Text = message;
        _timeLabel.Text = string.Empty;
        _progressBar.Fraction = 0;
    }

    public void SetTrack(string artist, string title)
    {
        _trackLabel.Text = $"{artist} — {title}";
        _timeLabel.Text = "00:00 / 00:00";
        _progressBar.Fraction = 0;
    }

    public void SetProgress(TimeSpan? position, TimeSpan? duration)
    {
        if (position is null || duration is null || duration.Value.TotalSeconds <= 0)
        {
            return;
        }

        _progressBar.Fraction = (float)Math.Clamp(position.Value.TotalSeconds / duration.Value.TotalSeconds, 0, 1);
        _timeLabel.Text = $"{FormatTime(position.Value)} / {FormatTime(duration.Value)}";
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }
}