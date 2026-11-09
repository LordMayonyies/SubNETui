using Sub.NETUI.Playback;
using Sub.NETUI.Subsonic;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Sub.NETUI.UI;

internal sealed class MainWindow : Window
{
    private readonly IApplication _app;
    private readonly SubsonicClient _subsonicClient;
    private readonly IPlaybackService _playback;
    private readonly ArtistsPane _artistsPane = new();
    private readonly AlbumsPane _albumsPane = new();
    private readonly TracksPane _tracksPane = new();
    private readonly NowPlayingPane _nowPlayingPane = new();
    private readonly StatusBar _statusBar = new();
    private CancellationTokenSource? _artistSelectionCancellation;
    private CancellationTokenSource? _albumSelectionCancellation;
    private IReadOnlyList<Song> _visibleTracks = [];
    private bool _updatingPanes;
    private bool _stopping;
    private string _trackNumberBuffer = string.Empty;
    private long _lastTrackDigitTimestamp;
    private Uri? _visibleArtworkUri;
    private Song? _displayedTrack;

    public MainWindow(
        IApplication app,
        SubsonicClient subsonicClient,
        IPlaybackService playback)
    {
        _app = app;
        _subsonicClient = subsonicClient;
        _playback = playback;

        Title = "Sub.NETUI - Navidrome";
        Width = Dim.Fill();
        Height = Dim.Fill();

        _artistsPane.X = 0;
        _artistsPane.Y = 0;
        _artistsPane.Width = Dim.Percent(30);
        _artistsPane.Height = Dim.Fill(1);

        var rightPane = new View
        {
            CanFocus = true,
            X = Pos.Right(_artistsPane),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1)
        };

        _albumsPane.X = 0;
        _albumsPane.Y = 0;
        _albumsPane.Width = Dim.Fill();
        _albumsPane.Height = Dim.Percent(38);

        _tracksPane.X = 0;
        _tracksPane.Y = Pos.Bottom(_albumsPane);
        _tracksPane.Width = Dim.Fill();
        _tracksPane.Height = Dim.Percent(42);

        _nowPlayingPane.X = 0;
        _nowPlayingPane.Y = Pos.Bottom(_tracksPane);
        _nowPlayingPane.Width = Dim.Fill();
        _nowPlayingPane.Height = Dim.Fill();

        rightPane.Add(_albumsPane, _tracksPane, _nowPlayingPane);

        _statusBar.X = 0;
        _statusBar.Y = Pos.AnchorEnd(1);
        _statusBar.Width = Dim.Fill();
        _statusBar.Height = 1;

        Add(_artistsPane, rightPane, _statusBar);
        _artistsPane.ValueChanged += OnArtistSelectionChanged;
        _albumsPane.ValueChanged += OnAlbumSelectionChanged;
        _artistsPane.ItemAccepted += _ => _albumsPane.FocusList();
        _albumsPane.ItemAccepted += _ => _tracksPane.FocusList();
        _tracksPane.ValueChanged += OnTrackSelectionChanged;
        _tracksPane.ItemAccepted += song => _ = PlaySelectedSongAsync(song);
        _app.Keyboard.KeyDown += OnKeyDown;
        _playback.StateChanged += OnPlaybackStateChanged;
        _playback.ProgressChanged += OnPlaybackProgressChanged;
        _playback.NoticeRaised += OnPlaybackNoticeRaised;
        _playback.ShutdownRequested += OnPlaybackShutdownRequested;

        OnPlaybackStateChanged(this, _playback.Current);
        OnPlaybackProgressChanged(this, _playback.Progress);
        _artistsPane.FocusList();
        _ = LoadArtistsAsync();
    }

    public void StopBackgroundWork()
    {
        _stopping = true;
        _app.Keyboard.KeyDown -= OnKeyDown;
        _playback.StateChanged -= OnPlaybackStateChanged;
        _playback.ProgressChanged -= OnPlaybackProgressChanged;
        _playback.NoticeRaised -= OnPlaybackNoticeRaised;
        _playback.ShutdownRequested -= OnPlaybackShutdownRequested;
        _artistSelectionCancellation?.Cancel();
        _albumSelectionCancellation?.Cancel();
    }

    private async Task LoadArtistsAsync()
    {
        try
        {
            _statusBar.SetStatus("Connecting to Navidrome...");
            IReadOnlyList<Artist> artists = await _subsonicClient.GetArtistsAsync();
            _app.Invoke(() =>
            {
                if (_stopping)
                {
                    return;
                }

                _updatingPanes = true;
                _artistsPane.SetItems(artists);
                _updatingPanes = false;
                if (artists.Count == 0)
                {
                    _statusBar.SetStatus("Navidrome has no artists available.");
                }
                else
                {
                    _statusBar.SetStatus(null);
                    _ = LoadAlbumsAsync(_artistsPane.SelectedValue);
                }
            });
        }
        catch (Exception exception)
            when (exception is HttpRequestException or SubsonicException or System.Text.Json.JsonException)
        {
            _app.Invoke(() =>
            {
                if (!_stopping)
                {
                    _statusBar.SetStatus($"Could not load artists: {exception.Message}");
                }
            });
        }
    }

    private void OnArtistSelectionChanged(Artist? artist)
    {
        if (_updatingPanes)
        {
            return;
        }

        _ = LoadAlbumsAsync(artist);
    }

    private async Task LoadAlbumsAsync(Artist? artist)
    {
        _artistSelectionCancellation?.Cancel();
        _artistSelectionCancellation?.Dispose();
        _artistSelectionCancellation = null;
        _albumSelectionCancellation?.Cancel();
        _visibleTracks = [];
        _visibleArtworkUri = null;
        _ = _playback.CueAsync([], -1, null);
        _tracksPane.SetItems([]);
        _albumsPane.SetItems([]);
        if (!_playback.IsActive)
        {
            _nowPlayingPane.SetMessage("Select an album to view its tracks");
        }

        if (artist is null || _stopping)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _artistSelectionCancellation = cancellation;
        var cancellationToken = cancellation.Token;

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            var albums = await _subsonicClient.GetArtistAlbumsAsync(artist.Id, cancellationToken);
            _app.Invoke(() =>
            {
                if (_stopping
                    || cancellationToken.IsCancellationRequested
                    || !ReferenceEquals(_artistSelectionCancellation, cancellation)
                   )
                {
                    return;
                }

                _updatingPanes = true;
                _albumsPane.SetItems(albums);
                _updatingPanes = false;
                if (albums.Count == 0)
                {
                    _statusBar.SetStatus($"{artist.Name} has no albums.");
                }
                else
                {
                    _statusBar.SetStatus(null);
                    _ = LoadTracksAsync(_albumsPane.SelectedValue);
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
            when (exception is HttpRequestException or SubsonicException or System.Text.Json.JsonException
                      or OperationCanceledException)
        {
            _app.Invoke(() =>
            {
                if (!_stopping && !cancellationToken.IsCancellationRequested)
                {
                    _statusBar.SetStatus($"Could not load albums: {exception.Message}");
                }
            });
        }
    }

    private void OnAlbumSelectionChanged(AlbumSummary? album)
    {
        if (_updatingPanes)
        {
            return;
        }

        _ = LoadTracksAsync(album);
    }

    private async Task LoadTracksAsync(AlbumSummary? albumSummary)
    {
        _albumSelectionCancellation?.Cancel();
        _albumSelectionCancellation?.Dispose();
        _albumSelectionCancellation = null;
        _visibleTracks = [];
        _visibleArtworkUri = null;
        _ = _playback.CueAsync([], -1, null);
        _tracksPane.SetItems([]);

        if (albumSummary is null || _stopping)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _albumSelectionCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        try
        {
            var album = await _subsonicClient.GetAlbumAsync(albumSummary.Id, cancellationToken);

            _app.Invoke(() =>
            {
                if (_stopping || cancellationToken.IsCancellationRequested ||
                    !ReferenceEquals(_albumSelectionCancellation, cancellation))
                {
                    return;
                }

                var tracks =
                    (album.Songs ?? [])
                    .OrderBy(song => song.DiscNumber ?? 0)
                    .ThenBy(song => song.Track ?? int.MaxValue)
                    .ToArray();
                _visibleTracks = tracks;
                _visibleArtworkUri = _subsonicClient.BuildCoverArtUri(album.CoverArt);
                _updatingPanes = true;
                _tracksPane.SetItems(tracks);
                _updatingPanes = false;
                _ = _playback.CueAsync(tracks, _tracksPane.SelectedIndex ?? 0, _visibleArtworkUri);
                if (!_playback.IsActive)
                {
                    _nowPlayingPane.SetMessage($"{album.Name} · {tracks.Length} tracks");
                }

                _statusBar.SetStatus(tracks.Length == 0 ? "This album has no tracks." : null);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
            when (exception is HttpRequestException or SubsonicException or System.Text.Json.JsonException
                      or OperationCanceledException)
        {
            _app.Invoke(() =>
            {
                if (!_stopping && !cancellationToken.IsCancellationRequested)
                {
                    _statusBar.SetStatus($"Could not load tracks: {exception.Message}");
                }
            });
        }
    }

    private async Task PlaySelectedSongAsync(Song song)
    {
        int index = _tracksPane.SelectedIndex ?? -1;
        if (index < 0 || index >= _visibleTracks.Count || _visibleTracks[index].Id != song.Id)
        {
            index = _visibleTracks.ToList().FindIndex(track => track.Id == song.Id);
            if (index < 0)
            {
                return;
            }
        }

        await _playback.PlayQueueAsync(_visibleTracks, index, _visibleArtworkUri);
    }

    private void OnTrackSelectionChanged(Song? song)
    {
        if (_updatingPanes || _tracksPane.SelectedIndex is not { } index)
        {
            return;
        }

        _ = _playback.CueAsync(_visibleTracks, index, _visibleArtworkUri);
    }

    private void OnPlaybackStateChanged(object? sender, PlaybackSnapshot snapshot)
    {
        _app.Invoke(() =>
        {
            if (_stopping)
            {
                return;
            }

            switch (snapshot.State)
            {
                case PlaybackState.Playing or PlaybackState.Paused:
                    if (_displayedTrack != snapshot.Track && snapshot.Track is { } track)
                    {
                        _nowPlayingPane.SetTrack(track.Artist ?? string.Empty, track.Title);
                    }

                    _displayedTrack = snapshot.Track;
                    _statusBar.SetStatus(snapshot.State == PlaybackState.Paused ? "Playback paused." : null);
                    break;
                default:
                    _displayedTrack = null;
                    _nowPlayingPane.SetMessage(snapshot.Message ?? "Nothing is playing");
                    _statusBar.SetStatus(null);
                    break;
            }
        });
    }

    private void OnPlaybackProgressChanged(object? sender, PlaybackProgress progress)
    {
        _app.Invoke(() =>
        {
            if (!_stopping && _displayedTrack is not null)
            {
                _nowPlayingPane.SetProgress(progress.Position, progress.Duration);
            }
        });
    }

    private async Task ShutdownAndQuitAsync()
    {
        _statusBar.SetStatus("Stopping playback...");
        await _playback.ShutdownAsync();
        _app.Invoke(() => _app.RequestStop());
    }

    private void OnPlaybackShutdownRequested(object? sender, EventArgs e)
    {
        _app.Invoke(() =>
        {
            if (!_stopping)
            {
                _app.RequestStop();
            }
        });
    }

    private void OnPlaybackNoticeRaised(object? sender, string message)
    {
        _app.Invoke(() =>
        {
            if (!_stopping)
            {
                _statusBar.SetStatus(message);
            }
        });
    }

    private void OnKeyDown(object? sender, Key e)
    {
        if (_stopping)
        {
            return;
        }

        if (e.KeyCode == Key.Q.WithCtrl)
        {
            e.Handled = true;
            _app.RequestStop();
        }
        else if (e.KeyCode == Key.X.WithCtrl)
        {
            e.Handled = true;
            _ = ShutdownAndQuitAsync();
        }
        else if (e.KeyCode == Key.S.WithCtrl)
        {
            e.Handled = true;
            _ = _playback.StopAsync();
        }
        else if (e.KeyCode == Key.Space.WithCtrl)
        {
            e.Handled = true;
            _ = _playback.TogglePauseAsync();
        }
        else if (e.KeyCode == Key.N.WithCtrl)
        {
            e.Handled = true;
            _ = _playback.NextAsync();
        }
        else if (e.KeyCode == Key.P.WithCtrl)
        {
            e.Handled = true;
            _ = _playback.PreviousAsync();
        }
        else if (e.KeyCode == Key.Tab)
        {
            e.Handled = true;
            FocusNextPane();
        }
        else if (e.KeyCode == Key.Enter)
        {
            if (_artistsPane.ListHasFocus)
            {
                e.Handled = true;
                _albumsPane.FocusList();
            }
            else if (_albumsPane.ListHasFocus)
            {
                e.Handled = true;
                _tracksPane.FocusList();
            }
        }
        else if (e.KeyCode == Key.Esc)
        {
            e.Handled = true;
            if (_tracksPane.ListHasFocus)
            {
                _albumsPane.FocusList();
            }
            else if (_albumsPane.ListHasFocus)
            {
                _artistsPane.FocusList();
            }
        }
        else if (e.KeyCode is >= KeyCode.D0 and <= KeyCode.D9 && _tracksPane.ListHasFocus)
        {
            e.Handled = true;
            SelectTrackByDigit((int)(e.KeyCode - KeyCode.D0));
        }
    }

    private void SelectTrackByDigit(int digit)
    {
        long now = Environment.TickCount64;
        if (now - _lastTrackDigitTimestamp > 1000)
        {
            _trackNumberBuffer = string.Empty;
        }

        _lastTrackDigitTimestamp = now;
        _trackNumberBuffer += digit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int index = FindTrackIndex(int.Parse(_trackNumberBuffer, System.Globalization.CultureInfo.InvariantCulture));
        if (index < 0 && _trackNumberBuffer.Length > 1)
        {
            _trackNumberBuffer = digit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            index = FindTrackIndex(digit);
        }

        if (index >= 0)
        {
            _tracksPane.SelectIndex(index);
        }
    }

    private int FindTrackIndex(int trackNumber)
    {
        int? currentDisc = _tracksPane.SelectedValue?.DiscNumber;
        int fallback = -1;
        for (int i = 0; i < _visibleTracks.Count; i++)
        {
            Song song = _visibleTracks[i];
            if (song.Track != trackNumber)
            {
                continue;
            }

            if (song.DiscNumber == currentDisc)
            {
                return i;
            }

            if (fallback < 0)
            {
                fallback = i;
            }
        }

        return fallback;
    }

    private void FocusNextPane()
    {
        if (_artistsPane.ListHasFocus)
        {
            _albumsPane.FocusList();
        }
        else if (_albumsPane.ListHasFocus)
        {
            _tracksPane.FocusList();
        }
        else
        {
            _artistsPane.FocusList();
        }
    }
}