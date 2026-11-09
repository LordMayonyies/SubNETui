using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;

namespace Sub.NETUI.Daemon;

public static class DaemonControl
{
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> StopAsync()
    {
        using Process? process = FindDaemonProcess();
        bool listening = await PlaybackDaemon.IsRunningAsync(DaemonEndpoint.SocketPath);
        if (process is null && !listening)
        {
            Console.WriteLine("Sub.NETUI daemon is not running.");
            return 0;
        }

        var stopwatch = Stopwatch.StartNew();
        if (listening)
        {
            await SendShutdownAsync();
        }

        if (process is null)
        {
            return await WaitForSocketToCloseAsync();
        }

        TimeSpan remaining = GracefulTimeout - stopwatch.Elapsed;
        if (await WaitForExitAsync(process, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
        {
            Console.WriteLine("Sub.NETUI daemon stopped.");
            return 0;
        }

        process.Kill(entireProcessTree: true);
        if (await WaitForExitAsync(process, TimeSpan.FromSeconds(2)))
        {
            File.Delete(DaemonEndpoint.SocketPath);
            File.Delete(DaemonEndpoint.PidPath);
            Console.WriteLine("Sub.NETUI daemon did not respond and was killed.");
            return 0;
        }

        await Console.Error.WriteLineAsync($"Could not stop the Sub.NETUI daemon (pid {process.Id}).");
        return 1;
    }

    private static Process? FindDaemonProcess()
    {
        try
        {
            string text = File.ReadAllText(DaemonEndpoint.PidPath).Trim();
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                return null;
            }

            Process process = Process.GetProcessById(pid);
            using Process current = Process.GetCurrentProcess();
            if (process.ProcessName == current.ProcessName)
            {
                return process;
            }

            process.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task SendShutdownAsync()
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(DaemonEndpoint.SocketPath));
            byte[] command = JsonSerializer.SerializeToUtf8Bytes(
                new DaemonCommand(DaemonCommandType.Shutdown),
                DaemonJsonContext.Default.DaemonCommand);
            await socket.SendAsync(command);
            await socket.SendAsync("\n"u8.ToArray());
            socket.Shutdown(SocketShutdown.Send);

            // The daemon closes the connection once it has processed the command and starts shutting down.
            using var timeout = new CancellationTokenSource(GracefulTimeout);
            byte[] buffer = new byte[4096];
            while (await socket.ReceiveAsync(buffer, timeout.Token) > 0)
            {
            }
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task<int> WaitForSocketToCloseAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < GracefulTimeout)
        {
            if (!await PlaybackDaemon.IsRunningAsync(DaemonEndpoint.SocketPath))
            {
                Console.WriteLine("Sub.NETUI daemon stopped.");
                return 0;
            }

            await Task.Delay(100);
        }

        await Console.Error.WriteLineAsync("Sub.NETUI daemon did not stop within five seconds.");
        return 1;
    }
}
