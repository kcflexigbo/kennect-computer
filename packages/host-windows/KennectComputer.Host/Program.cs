using System.Runtime.InteropServices;

using KennectComputer.Host.Actions;
using KennectComputer.Host.Auth;
using KennectComputer.Host.Config;
using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Ipc;
using KennectComputer.Host.Policy;
using KennectComputer.Host.Tray;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host;

internal static class Program
{
    private const int PerMonitorAwareV2 = -4;

    private static readonly TimeSpan TrayStartupTimeout = TimeSpan.FromSeconds(10);

    private static async Task<int> Main()
    {
        EnablePerMonitorDpiAwareness();

        var config = ConfigStore.Load(ConfigStore.FindDefaultPath());

        // One host per user session. A second launch signals the running host
        // to show its tray and exits without touching the pipe or token.
        using var singleInstance = new SingleInstance();
        if (!singleInstance.TryAcquire())
        {
            return 0;
        }

        var tokens = TokenStore.CreateDefault();
        var pipeName = PipeServer.ForUser(Environment.UserName);

        // Every host start rotates the token and republishes discovery metadata.
        tokens.Rotate();
        tokens.WriteSessionInfo(pipeName);

        using var shutdown = new CancellationTokenSource();
        var control = new ControlSession();

        var windowApi = new WindowApi();
        var exclusions = new ExclusionPolicy(config);
        // Resolves re-apply the exclusion policy against the live title, so a
        // window that navigates to private content after enumeration loses
        // its ID instead of being captured.
        var registry = new WindowRegistry(windowApi, exclusions);
        var windows = new WindowEnumerator(windowApi, registry, exclusions);

        // The tray and the stop hotkey run on a dedicated STA thread; the pipe
        // server stays on the main async path (R30).
        var trayReady = new TaskCompletionSource<TrayApp>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hotkey = new StopHotkey(
            control,
            new Win32HotkeyRegistrar(),
            config.Control.StopHotkey.Windows);
        var trayThread = StartTrayThread(
            control,
            hotkey,
            singleInstance.ShowTraySignal,
            shutdown,
            trayReady);
        var tray = await WaitForTrayAsync(trayReady).ConfigureAwait(false);

        // "allow-all" is the deliberate opt-out for a single-user machine that
        // does not want the first-use prompt; exclusions, the stop hotkey, and
        // every other guard still apply. The default remains per-app, and
        // without a tray control fails closed while status, windows, and
        // snapshots keep working.
        var allowAll = string.Equals(
            config.Control.AuthorizationMode?.Trim(),
            "allow-all",
            StringComparison.OrdinalIgnoreCase);
        IAppAuthorization authorization = allowAll
            ? new AllowAllAuthorization()
            : tray is null
                ? new AppAuthorization(config, new DenyAllAuthorizationPrompt())
                : new AppAuthorization(
                    config,
                    new WinFormsAuthorizationPrompt(work => tray.InvokeAsync(work)));

        // One foreground seam, walker, snapshotter, and selection store serve
        // both the M1 methods and every M2 action service, so focus, snapshot
        // generations, and node IDs stay consistent across the milestone.
        var foregroundApi = new WindowsForegroundApi();
        var foreground = new ForegroundService(foregroundApi);
        var selectionStore = new SelectionStore();
        var selection = new SelectionService(
            registry,
            authorization,
            selectionStore,
            foreground,
            windows);
        var walker = new WindowsUiaWalker();
        var snapshotter = new UiaSnapshotter(walker);
        var snapshots = new SnapshotStore();
        var preflight = new ActionPreflight(
            foreground,
            selectionStore,
            registry,
            new ExpectEvaluator(walker, foregroundApi));
        var resolver = new WindowsUiaNodeResolver();
        var input = new WindowsInputSender(new SendInputApi());
        var clipboard = new WindowsClipboard();

        var click = new ClickAction(walker, resolver, preflight, input, snapshots);
        var type = new TypeAction(resolver, preflight, input, clipboard, snapshots);
        var key = new KeyAction(preflight, input);
        var scroll = new ScrollAction(resolver, preflight, input, snapshots);
        var wait = new WaitAction(
            walker,
            windows,
            selectionStore,
            registry,
            SystemWaitClock.Instance);
        var readText = new ReadTextAction(resolver, snapshots, selectionStore, registry);
        var find = new FindAction(walker, snapshotter, snapshots, selectionStore, registry);
        var batch = new BatchAction(
            click,
            type,
            key,
            scroll,
            wait,
            readText,
            snapshotter,
            snapshots,
            selectionStore,
            registry);

        var dispatcher = new JsonRpcDispatcher(
            tokens,
            new WindowsMonitorProvider(),
            new DesktopState(),
            windows,
            selection,
            registry,
            snapshotter,
            snapshots,
            control,
            click,
            type,
            key,
            scroll,
            wait,
            readText,
            find,
            batch,
            config.Perception);
        var server = new PipeServer(pipeName, tokens, dispatcher);

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
            // Tray Exit and Ctrl+C both cancel the same token; make sure the
            // tray loop and its hotkey registration are torn down either way.
            tray?.RequestExit();
            trayThread.Join(TimeSpan.FromSeconds(2));
            tokens.DeleteSessionInfo();
        }

        return 0;
    }

    /// <summary>
    /// Starts the STA thread that owns the tray icon and the hidden window the
    /// stop hotkey is registered on.
    /// </summary>
    private static Thread StartTrayThread(
        ControlSession control,
        StopHotkey hotkey,
        EventWaitHandle? showTraySignal,
        CancellationTokenSource shutdown,
        TaskCompletionSource<TrayApp> ready)
    {
        var thread = new Thread(() =>
        {
            TrayApp? tray = null;
            try
            {
                tray = new TrayApp(control, hotkey, showTraySignal, shutdown.Cancel);
                ready.TrySetResult(tray);
                tray.Run();
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
            }
            finally
            {
                // Owned and disposed on the thread that created it.
                tray?.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "Kennect tray and stop hotkey",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private static async Task<TrayApp?> WaitForTrayAsync(TaskCompletionSource<TrayApp> ready)
    {
        try
        {
            return await ready.Task.WaitAsync(TrayStartupTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A tray failure must not take down the host's data path.
            return null;
        }
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
