using System.Runtime.InteropServices;

using KennectComputer.Host.Auth;
using KennectComputer.Host.Ipc;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host;

internal static class Program
{
    private const int PerMonitorAwareV2 = -4;

    private static async Task<int> Main()
    {
        EnablePerMonitorDpiAwareness();

        var tokens = TokenStore.CreateDefault();
        var pipeName = PipeServer.ForUser(Environment.UserName);

        // Every host start rotates the token and republishes discovery metadata.
        tokens.Rotate();
        tokens.WriteSessionInfo(pipeName);

        var windowApi = new WindowApi();
        var windows = new WindowEnumerator(windowApi, new WindowRegistry(windowApi));
        var dispatcher = new JsonRpcDispatcher(tokens, new WindowsMonitorProvider(), new DesktopState(), windows);
        var server = new PipeServer(pipeName, tokens, dispatcher);

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => tokens.DeleteSessionInfo();

        try
        {
            await server.RunAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            tokens.DeleteSessionInfo();
        }

        return 0;
    }

    private static void EnablePerMonitorDpiAwareness()
    {
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(PerMonitorAwareV2));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiAwarenessContext);
}
