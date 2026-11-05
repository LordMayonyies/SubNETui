namespace Sub.NETUI.Subsonic;

public sealed class SubsonicException(string message, int? code = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int? Code { get; } = code;
}