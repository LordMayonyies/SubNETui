using Sub.NETUI.Configuration;
using Sub.NETUI.Playback;
using Sub.NETUI.Subsonic;
using Sub.NETUI.UI;
using Terminal.Gui.App;

namespace Sub.NETUI;

internal static class Program
{
    private static void Main(string[] args)
    {
        using IMediaSession mediaSession = MprisMediaSession.CreateAsync().GetAwaiter().GetResult();
        using IApplication app = Application.Create();
        app.Init();

        using var httpClient = new HttpClient();
        if (!AppSettingsLoader.TryLoad(out AppSettings? settings, out _))
        {
            using var loginWindow = new LoginWindow(app, httpClient);
            app.Run(loginWindow);
            settings = loginWindow.Settings;
            if (settings is null)
            {
                return;
            }
        }

        using var audioPlayer = new MpvAudioPlayer();
        var subsonicClient = new SubsonicClient(httpClient, settings!);
        var playbackQueue = new PlaybackQueue();

        using var mainWindow = new MainWindow(app, subsonicClient, audioPlayer, playbackQueue, mediaSession);
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
