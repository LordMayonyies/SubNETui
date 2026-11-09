using System.Runtime.InteropServices;

namespace Sub.NETUI.Daemon;

internal static class ProcessDetacher
{
    public static void Detach()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A new session has no controlling terminal, so closing the TUI's terminal does not send SIGHUP.
        _ = SetSid();
        using var devNull = new FileStream("/dev/null", FileMode.Open, FileAccess.ReadWrite);
        int descriptor = (int)devNull.SafeFileHandle.DangerousGetHandle();
        for (int standardDescriptor = 0; standardDescriptor <= 2; standardDescriptor++)
        {
            _ = Dup2(descriptor, standardDescriptor);
        }
    }

    [DllImport("libc", EntryPoint = "setsid")]
    private static extern int SetSid();

    [DllImport("libc", EntryPoint = "dup2")]
    private static extern int Dup2(int oldDescriptor, int newDescriptor);
}
