using System.ComponentModel;
using System.Diagnostics;

using KennectComputer.Host.Protocol;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

public sealed class WindowEnumeratorTests
{
    private const long WsExToolWindow = 0x00000080;

    [Fact]
    public void FiltersToolCloakedInvisibleAndUntitledWindows()
    {
        // Every negative fixture has a title, a process name, and visibility on
        // its own; only the property under test excludes it.
        var api = FakeWindowApi.With(
            normal: NotepadWindow(),
            tool: new RawWindow(2, 101, "Tool", "toolproc", new Rect(0, 0, 10, 10)) { ExStyle = WsExToolWindow },
            cloaked: new RawWindow(3, 102, "Cloaked", "cloakedproc", new Rect(0, 0, 10, 10)) { Cloaked = true },
            invisible: new RawWindow(4, 103, "Invisible", "invisibleproc", new Rect(0, 0, 10, 10)) { Visible = false },
            untitled: new RawWindow(5, 104, "", "untitledproc", new Rect(0, 0, 10, 10)));

        var list = new WindowEnumerator(api, new WindowRegistry(api)).List();

        var window = Assert.Single(list);
        Assert.Equal("Notepad", window.Title);
        Assert.Equal("Notepad", window.ProcessName);
        Assert.Equal("w_1", window.WindowId);
        Assert.Equal(100, window.Pid);
        Assert.Equal(800, window.Bounds.Width);
    }

    [Fact]
    public void FiltersToolWindows()
        => AssertOnlyNormal(new RawWindow(2, 101, "Tool", "toolproc", new Rect(0, 0, 10, 10))
        {
            ExStyle = WsExToolWindow,
        });

    [Fact]
    public void FiltersCloakedWindows()
        => AssertOnlyNormal(new RawWindow(3, 102, "Cloaked", "cloakedproc", new Rect(0, 0, 10, 10))
        {
            Cloaked = true,
        });

    [Fact]
    public void FiltersInvisibleWindows()
        => AssertOnlyNormal(new RawWindow(4, 103, "Invisible", "invisibleproc", new Rect(0, 0, 10, 10))
        {
            Visible = false,
        });

    [Fact]
    public void FiltersUntitledWindows()
        => AssertOnlyNormal(new RawWindow(5, 104, "", "untitledproc", new Rect(0, 0, 10, 10)));

    [Fact]
    public void RegistryInvalidatesAWindowThatDied()
    {
        var api = new FakeWindowApi(NotepadWindow());
        var registry = new WindowRegistry(api);
        var info = registry.Register(NotepadWindow());

        Assert.True(registry.TryResolve(info.WindowId, out var resolved));
        Assert.Equal((nint)1, resolved!.Hwnd);
        Assert.Equal(100, resolved.Pid);
        Assert.Equal("Notepad", resolved.ProcessName);

        registry.MarkDeadForTests(1);

        Assert.False(registry.TryResolve(info.WindowId, out _));
    }

    [Fact]
    public void RegistryInvalidatesAWindowWhenTheOsReportsItGone()
    {
        var api = new FakeWindowApi(NotepadWindow());
        var registry = new WindowRegistry(api);
        var info = registry.Register(NotepadWindow());

        Assert.True(registry.TryResolve(info.WindowId, out _));

        // The OS reports the handle is gone; no further windows.list runs.
        api.MarkDead(1);

        Assert.False(registry.TryResolve(info.WindowId, out _));
        // The dead ID is removed, not merely flagged.
        Assert.False(registry.TryResolve(info.WindowId, out _));
    }

    [Fact]
    public void RegistryInvalidatesAWindowWhoseNativeIdentityChanged()
    {
        var api = new FakeWindowApi(NotepadWindow());
        var registry = new WindowRegistry(api);
        var info = registry.Register(NotepadWindow());

        // Same HWND, different PID and class: the handle was recycled.
        api.Replace(new RawWindow(1, 999, "Other", "otherproc", new Rect(0, 0, 10, 10))
        {
            ClassName = "OtherClass",
        });

        Assert.False(registry.TryResolve(info.WindowId, out _));
    }

    [Fact]
    public void RegistryKeepsTheIdWhenOnlyTheTitleChanges()
    {
        var api = new FakeWindowApi(NotepadWindow());
        var registry = new WindowRegistry(api);
        var original = registry.Register(NotepadWindow());
        var renamed = registry.Register(
            new RawWindow(1, 100, "notes.txt - Notepad", "Notepad", new Rect(0, 0, 20, 10)));

        Assert.Equal(original.WindowId, renamed.WindowId);
        Assert.Equal("notes.txt - Notepad", renamed.Title);
        Assert.Equal(20, renamed.Bounds.Width);
        Assert.True(registry.TryResolve(original.WindowId, out _));
    }

    [Fact]
    public void ListsARealNotepadWindow()
    {
        // Windows 11's Notepad is a packaged app: the notepad.exe we launch may
        // hand off to an app process and exit, or activate an already-running
        // instance. The enumerated window can therefore belong to a different
        // PID than the launcher; assert that whatever was launched produced (or
        // activated) an enumerable notepad window whose PID is a live process.
        var existingPids = Process.GetProcessesByName("notepad").Select(process => process.Id).ToHashSet();
        foreach (var process in Process.GetProcessesByName("notepad"))
        {
            process.Dispose();
        }

        using var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = false });
        Assert.NotNull(notepad);

        try
        {
            var api = new WindowApi();
            var enumerator = new WindowEnumerator(api, new WindowRegistry(api));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            WindowInfo? found = null;
            while (DateTime.UtcNow < deadline)
            {
                found = enumerator.List().FirstOrDefault(window =>
                    string.Equals(window.ProcessName, "notepad", StringComparison.OrdinalIgnoreCase)
                    && (window.Pid == notepad.Id || !existingPids.Contains(window.Pid)));
                if (found is not null)
                {
                    break;
                }

                Thread.Sleep(250);
            }

            // A launch that activates an existing instance produces no new PID;
            // accept that window too, but still require a live notepad process.
            if (found is null)
            {
                found = enumerator.List().FirstOrDefault(window =>
                    string.Equals(window.ProcessName, "notepad", StringComparison.OrdinalIgnoreCase));
            }

            Assert.NotNull(found);
            Assert.False(string.IsNullOrWhiteSpace(found!.Title));
            Assert.True(found.Visible);

            using var owner = Process.GetProcessById(found.Pid);
            Assert.Equal("notepad", owner.ProcessName, ignoreCase: true);
        }
        finally
        {
            try
            {
                // Kill only the process this test started, never its tree: on
                // Windows 11 the packaged-Notepad launcher may have handed the
                // window to an app process, and that process can host restored
                // user tabs. If the launcher exited, its app process is left
                // alive deliberately.
                if (!notepad.HasExited)
                {
                    notepad.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }

            notepad.WaitForExit(5000);
        }
    }

    private static RawWindow NotepadWindow()
        => new(1, 100, "Notepad", "Notepad", new Rect(0, 0, 800, 600));

    private static void AssertOnlyNormal(RawWindow filtered)
    {
        var api = new FakeWindowApi(NotepadWindow(), filtered);
        var list = new WindowEnumerator(api, new WindowRegistry(api)).List();

        var window = Assert.Single(list);
        Assert.Equal("Notepad", window.Title);
    }
}

internal sealed class FakeWindowApi : IWindowApi
{
    private readonly object _gate = new();
    private readonly Dictionary<nint, RawWindow> _windows;
    private readonly HashSet<nint> _alive;

    public FakeWindowApi(params RawWindow[] windows)
    {
        _windows = windows.ToDictionary(window => window.Hwnd);
        _alive = [.. _windows.Keys];
    }

    public IReadOnlyList<RawWindow> GetTopLevelWindows()
    {
        lock (_gate)
        {
            return [.. _windows.Values.Where(window => _alive.Contains(window.Hwnd))];
        }
    }

    public RawWindow? TryGetWindow(nint hwnd)
    {
        lock (_gate)
        {
            return _alive.Contains(hwnd) && _windows.TryGetValue(hwnd, out var window)
                ? window
                : null;
        }
    }

    public void MarkDead(nint hwnd)
    {
        lock (_gate)
        {
            _alive.Remove(hwnd);
        }
    }

    public void Replace(RawWindow window)
    {
        lock (_gate)
        {
            _windows[window.Hwnd] = window;
            _alive.Add(window.Hwnd);
        }
    }

    public static FakeWindowApi With(
        RawWindow? normal = null,
        RawWindow? tool = null,
        RawWindow? cloaked = null,
        RawWindow? invisible = null,
        RawWindow? untitled = null)
        => new([.. new[] { normal, tool, cloaked, invisible, untitled }.OfType<RawWindow>()]);
}
