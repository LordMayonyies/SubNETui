namespace Sub.NETUI.Playback;

internal static class AudioPlayerFactory
{
    /// <summary>
    /// macOS uses the built-in AVPlayer unless <c>SUBNETUI_PLAYER=mpv</c> is set; other systems use mpv.
    /// </summary>
    public static IAudioPlayer Create()
    {
        var preferMpv =
            string.Equals(
                Environment.GetEnvironmentVariable("SUBNETUI_PLAYER"),
                "mpv",
                StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsMacOS() && !preferMpv && AvAudioPlayer.TryInitialize())
        {
            return new AvAudioPlayer();
        }

        return new MpvAudioPlayer();
    }
}