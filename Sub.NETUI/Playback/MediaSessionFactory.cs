namespace Sub.NETUI.Playback;

internal static class MediaSessionFactory
{
    public static async Task<IMediaSession> CreateAsync()
    {
        if (OperatingSystem.IsLinux())
        {
            return await MprisMediaSession.CreateAsync();
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacNowPlayingSession.Create();
        }

        return new NullMediaSession();
    }
}
