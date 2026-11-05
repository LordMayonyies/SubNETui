using Sub.NETUI.Playback;
using Sub.NETUI.Subsonic;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Sub.NETUI.UI;

internal sealed class MainWindow : Window
{
    private readonly IApplication _app;
    private readonly SubsonicClient _subsonicClient;
    private readonly IAudioPlayer _audioPlayer;
    private readonly PlaybackQueue _queue;
    private readonly ArtistsPane _artistsPane = new();
    private readonly AlbumsPane _albumsPane = new();
    private readonly TracksPane _tracksPane = new();
    private readonly NowPlayingPane _nowPlayingPane = new();
    private readonly StatusBar _statusBar = new();
    private readonly Timer _progressTimer;
    private CancellationTokenSource? _artistSelectionCancellation;
    private CancellationTokenSource? _albumSelectionCancellation;
    private IReadOnlyList<Song> _visibleTracks = [];
    private bool _updatingPanes;
    private bool _stopping;
    private int _progressUpdateInFlight;

    public MainWindow(IApplication app, SubsonicClient subsonicClient, IAudioPlayer audioPlayer, PlaybackQueue queue)
    {
        _app = app;
        _subsonicClient = subsonicClient;
        _audioPlayer = audioPlayer;
        _queue = queue;
        _progressTimer = new Timer(
            _ => _ = UpdateProgressAsync(),
            null,
            System.Threading.Timeout.InfiniteTimeSpan,
            System.Threading.Timeout.InfiniteTimeSpan);

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
        _tracksPane.ItemAccepted += song => _ = PlaySelectedSongAsync(song);
        KeyDown += OnKeyDown;
        _audioPlayer.TrackEnded += OnTrackEnded;

        _artistsPane.FocusList();
        _ = LoadArtistsAsync();
    }

    public void StopBackgroundWork()
    {
        _stopping = true;
        _artistSelectionCancellation?.Cancel();
        _albumSelectionCancellation?.Cancel();
        _progressTimer.Dispose();
    }

    private async Task LoadArtistsAsync()
    {
        try
        {
            _statusBar.SetStatus("Conectando con Navidrome...");
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
                    _statusBar.SetStatus("Navidrome no tiene artistas disponibles.");
                }
                else
                {
                    _statusBar.SetStatus(null);
                    _ = LoadAlbumsAsync(_artistsPane.SelectedValue);
                }
            });
        }
        catch (Exception exception) when (exception is HttpRequestException or SubsonicException or System.Text.Json.JsonException)
        {
            _app.Invoke(() =>
            {
                if (!_stopping)
                {
                    _statusBar.SetStatus($"Error al cargar artistas: {exception.Message}");
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
        _albumSelectionCancellation?.Cancel();
        _visibleTracks = [];
        if (!_audioPlayer.IsPlaying)
        {
            _queue.SetTracks([]);
        }
        _tracksPane.SetItems([]);
        _albumsPane.SetItems([]);
        if (!_audioPlayer.IsPlaying)
        {
            _nowPlayingPane.SetMessage("Selecciona un álbum para ver sus canciones");
        }

        if (artist is null || _stopping)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _artistSelectionCancellation = cancellation;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation.Token);
            IReadOnlyList<AlbumSummary> albums = await _subsonicClient.GetArtistAlbumsAsync(artist.Id, cancellation.Token);
            _app.Invoke(() =>
            {
                if (_stopping || cancellation.IsCancellationRequested || !ReferenceEquals(_artistSelectionCancellation, cancellation))
                {
                    return;
                }

                _updatingPanes = true;
                _albumsPane.SetItems(albums);
                _updatingPanes = false;
                if (albums.Count == 0)
                {
                    _statusBar.SetStatus($"{artist.Name} no tiene álbumes.");
                }
                else
                {
                    _statusBar.SetStatus(null);
                    _ = LoadTracksAsync(_albumsPane.SelectedValue);
                }
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or SubsonicException or System.Text.Json.JsonException)
        {
            _app.Invoke(() =>
            {
                if (!_stopping && !cancellation.IsCancellationRequested)
                {
                    _statusBar.SetStatus($"Error al cargar álbumes: {exception.Message}");
                }
            });
        }
        finally
        {
            if (ReferenceEquals(_artistSelectionCancellation, cancellation))
            {
                _artistSelectionCancellation = null;
            }

            cancellation.Dispose();
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
        _visibleTracks = [];
        if (!_audioPlayer.IsPlaying)
        {
            _queue.SetTracks([]);
        }
        _tracksPane.SetItems([]);

        if (albumSummary is null || _stopping)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _albumSelectionCancellation = cancellation;
        try
        {
            Album album = await _subsonicClient.GetAlbumAsync(albumSummary.Id, cancellation.Token);
            _app.Invoke(() =>
            {
                if (_stopping || cancellation.IsCancellationRequested || !ReferenceEquals(_albumSelectionCancellation, cancellation))
                {
                    return;
                }

                Song[] tracks = (album.Songs ?? [])
                    .OrderBy(song => song.DiscNumber ?? 0)
                    .ThenBy(song => song.Track ?? int.MaxValue)
                    .ToArray();
                _visibleTracks = tracks;
                if (!_audioPlayer.IsPlaying)
                {
                    _queue.SetTracks(tracks);
                }
                _updatingPanes = true;
                _tracksPane.SetItems(tracks);
                _updatingPanes = false;
                if (!_audioPlayer.IsPlaying)
                {
                    _nowPlayingPane.SetMessage($"{album.Name} · {tracks.Length} canciones");
                }
                _statusBar.SetStatus(tracks.Length == 0 ? "El álbum no contiene canciones." : null);
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or SubsonicException or System.Text.Json.JsonException)
        {
            _app.Invoke(() =>
            {
                if (!_stopping && !cancellation.IsCancellationRequested)
                {
                    _statusBar.SetStatus($"Error al cargar canciones: {exception.Message}");
                }
            });
        }
        finally
        {
            if (ReferenceEquals(_albumSelectionCancellation, cancellation))
            {
                _albumSelectionCancellation = null;
            }

            cancellation.Dispose();
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

        _queue.SetTracks(_visibleTracks);
        _queue.StartAt(index);
        await StartSongAsync(song);
    }

    private async Task StartSongAsync(Song song)
    {
        try
        {
            StopProgressTimer();
            await _audioPlayer.PlayAsync(_subsonicClient.BuildStreamUri(song.Id));
            _app.Invoke(() =>
            {
                if (_stopping)
                {
                    return;
                }

                _nowPlayingPane.SetTrack(song.Artist ?? string.Empty, song.Title);
                _statusBar.SetStatus(null);
                StartProgressTimer();
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            _app.Invoke(() =>
            {
                if (!_stopping)
                {
                    StopProgressTimer();
                    _nowPlayingPane.SetMessage($"No se pudo reproducir: {exception.Message}");
                    _statusBar.SetStatus("Comprueba que mpv está instalado y disponible en PATH.");
                }
            });
        }
    }

    private void OnTrackEnded(object? sender, EventArgs eventArgs)
    {
        _app.Invoke(() =>
        {
            if (_stopping)
            {
                return;
            }

            Song? nextSong = _queue.Next();
            if (nextSong is null)
            {
                StopProgressTimer();
                _nowPlayingPane.SetMessage("Fin del álbum");
                _statusBar.SetStatus(null);
            }
            else
            {
                _ = StartSongAsync(nextSong);
            }
        });
    }

    private void OnKeyDown(object? sender, Key e)
    {
        if (e.KeyCode == Key.Q.WithCtrl)
        {
            e.Handled = true;
            _app.RequestStop();
        }
        else if (e.KeyCode == Key.S.WithCtrl)
        {
            e.Handled = true;
            _ = StopPlaybackAsync();
        }
        else if (e.KeyCode == Key.Space)
        {
            e.Handled = true;
            _ = TogglePauseAsync();
        }
        else if (e.KeyCode == Key.N)
        {
            e.Handled = true;
            _ = PlayQueueItemAsync(_queue.Next());
        }
        else if (e.KeyCode == Key.P)
        {
            e.Handled = true;
            _ = PlayQueueItemAsync(_queue.Previous());
        }
        else if (e.KeyCode == Key.Tab)
        {
            e.Handled = true;
            FocusNextPane();
        }
    }

    private async Task TogglePauseAsync()
    {
        try
        {
            await _audioPlayer.TogglePauseAsync();
            _app.Invoke(() =>
            {
                if (_audioPlayer.IsPaused)
                {
                    StopProgressTimer();
                    _statusBar.SetStatus("Reproducción pausada.");
                }
                else if (_audioPlayer.IsPlaying)
                {
                    StartProgressTimer();
                    _statusBar.SetStatus(null);
                }
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            _app.Invoke(() => _statusBar.SetStatus(exception.Message));
        }
    }

    private async Task StopPlaybackAsync()
    {
        try
        {
            StopProgressTimer();
            await _audioPlayer.StopAsync();
            _app.Invoke(() =>
            {
                _nowPlayingPane.SetMessage("Reproducción detenida");
                _statusBar.SetStatus(null);
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            _app.Invoke(() => _statusBar.SetStatus(exception.Message));
        }
    }

    private Task PlayQueueItemAsync(Song? song)
    {
        if (song is null)
        {
            _statusBar.SetStatus("No hay más canciones en esa dirección.");
            return Task.CompletedTask;
        }

        return StartSongAsync(song);
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

    private void StartProgressTimer()
    {
        _progressTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void StopProgressTimer()
    {
        _progressTimer.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
    }

    private async Task UpdateProgressAsync()
    {
        if (_stopping || Interlocked.Exchange(ref _progressUpdateInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            (TimeSpan? position, TimeSpan? duration) = await _audioPlayer.GetProgressAsync();
            _app.Invoke(() =>
            {
                if (!_stopping)
                {
                    _nowPlayingPane.SetProgress(position, duration);
                }
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _progressUpdateInFlight, 0);
        }
    }
}