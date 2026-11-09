using System.Runtime.InteropServices;
using Sub.NETUI.Configuration;
using Sub.NETUI.Daemon;
using Sub.NETUI.Playback;
using Sub.NETUI.Subsonic;
using Sub.NETUI.UI;
using Terminal.Gui.App;

namespace Sub.NETUI;

internal static class Program
{
    private static readonly TimeSpan SessionFlushTimeout = TimeSpan.FromSeconds(3);

    private static async Task<int> Main(string[] args)
    {
        if (args is ["daemon", "stop"])
        {
            return await DaemonControl.StopAsync();
        }

        if (args is ["daemon", .. var options])
        {
            if (options.Contains("--detach"))
            {
                ProcessDetacher.Detach();
            }

            return await RunDaemonAsync();
        }

        await RunTuiAsync();
        return 0;
    }

    private static async Task<int> RunDaemonAsync()
    {
        if (!AppSettingsLoader.TryLoad(out var settings, out string? error))
        {
            await Console.Error.WriteLineAsync($"Sub.NETUI daemon: {error ?? "settings are not configured."}");
            return 1;
        }

        using var httpClient = new HttpClient();
        using var audioPlayer = new MpvAudioPlayer();
        using IMediaSession mediaSession = await MprisMediaSession.CreateAsync();
        var subsonicClient = new SubsonicClient(httpClient, settings!);
        using var playback = new PlaybackController(audioPlayer, new PlaybackQueue(), mediaSession, subsonicClient);
        using var sessionSync = new NavidromeSessionSync(playback, subsonicClient);
        using var daemon = new PlaybackDaemon(
            playback,
            DaemonEndpoint.SocketPath,
            DaemonEndpoint.LockPath,
            DaemonEndpoint.PidPath);

        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        try
        {
            _ = sessionSync.RestoreAsync();
            await daemon.RunAsync();
            await sessionSync.FlushAsync(SessionFlushTimeout);
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
                                              or System.Net.Sockets.SocketException or ArgumentException)
        {
            await Console.Error.WriteLineAsync($"Sub.NETUI daemon: {exception.Message}");
            return 1;
        }

        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            daemon.RequestShutdown();
        }
    }

    private static async Task RunTuiAsync()
    {
        using var app = Application.Create();
        app.Init();

        using var httpClient = new HttpClient();
        if (!AppSettingsLoader.TryLoad(out var settings, out _))
        {
            using var loginWindow = new LoginWindow(app, httpClient);
            app.Run(loginWindow);

            settings = loginWindow.Settings;

            if (settings is null)
            {
                return;
            }
        }

        var subsonicClient = new SubsonicClient(httpClient, settings!);
        var resources = new Stack<IDisposable>();
        NavidromeSessionSync? sessionSync = null;
        try
        {
            IPlaybackService playback;
            string? fallbackReason = null;
            try
            {
                var daemonClient = await DaemonPlaybackClient.ConnectAsync(DaemonEndpoint.SocketPath);
                resources.Push(daemonClient);
                playback = daemonClient;
            }
            catch (Exception exception) when (exception is TimeoutException or IOException
                                                  or System.Net.Sockets.SocketException or InvalidOperationException
                                                  or ArgumentException or System.ComponentModel.Win32Exception)
            {
                fallbackReason = exception.Message;
                var controller = await CreateInProcessPlaybackAsync(subsonicClient, resources);
                sessionSync = new NavidromeSessionSync(controller, subsonicClient);
                resources.Push(sessionSync);
                _ = sessionSync.RestoreAsync();
                playback = controller;
            }

            RunMainWindow(app, subsonicClient, playback, fallbackReason);
            if (sessionSync is not null)
            {
                await sessionSync.FlushAsync(SessionFlushTimeout);
            }
        }
        finally
        {
            while (resources.TryPop(out IDisposable? resource))
            {
                resource.Dispose();
            }
        }
    }

    private static async Task<PlaybackController> CreateInProcessPlaybackAsync(
        SubsonicClient subsonicClient,
        Stack<IDisposable> resources)
    {
        var audioPlayer = new MpvAudioPlayer();
        resources.Push(audioPlayer);
        IMediaSession mediaSession = await MprisMediaSession.CreateAsync();
        resources.Push(mediaSession);
        var playback = new PlaybackController(audioPlayer, new PlaybackQueue(), mediaSession, subsonicClient);
        resources.Push(playback);
        return playback;
    }

    private static void RunMainWindow(
        IApplication app,
        SubsonicClient subsonicClient,
        IPlaybackService playback,
        string? fallbackReason)
    {
        using var mainWindow = new MainWindow(app, subsonicClient, playback);
        if (fallbackReason is not null)
        {
            mainWindow.Title = $"{mainWindow.Title} (in-process playback: {fallbackReason})";
        }

        try
        {
            app.Run(mainWindow);
        }
        finally
        {
            mainWindow.StopBackgroundWork();
        }
    }
}
