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
        var api = FakeWindowApi.With(
            normal: new RawWindow(1, 100, "Notepad", "Notepad", new Rect(0, 0, 800, 600)),
            tool: new RawWindow(2, 101, "Tool", "", new Rect(0, 0, 10, 10)) { ExStyle = WsExToolWindow },
            cloaked: new RawWindow(3, 102, "Hidden", "", new Rect(0, 0, 10, 10)) { Cloaked = true },
            invisible: new RawWindow(4, 103, "Hidden", "", new Rect(0, 0, 10, 10)) { Visible = false },
            untitled: new RawWindow(5, 104, "", "", new Rect(0, 0, 10, 10)));

        var list = new WindowEnumerator(api, new WindowRegistry()).List();

        var window = Assert.Single(list);
        Assert.Equal("Notepad", window.Title);
        Assert.Equal("Notepad", window.ProcessName);
        Assert.Equal("w_1", window.WindowId);
        Assert.Equal(100, window.Pid);
        Assert.Equal(800, window.Bounds.Width);
    }

    [Fact]
    public void RegistryInvalidatesAWindowThatDied()
    {
        var registry = new WindowRegistry();
        var info = registry.Register(new RawWindow(1, 100, "Notepad", "Notepad", new Rect(0, 0, 10, 10)));

        Assert.True(registry.TryResolve(info.WindowId, out var resolved));
        Assert.Equal((nint)1, resolved!.Hwnd);
        Assert.Equal(100, resolved.Pid);
        Assert.Equal("Notepad", resolved.ProcessName);

        registry.MarkDeadForTests(1);

        Assert.False(registry.TryResolve(info.WindowId, out _));
    }

    [Fact]
    public void RegistryKeepsTheIdWhenOnlyTheTitleChanges()
    {
        var registry = new WindowRegistry();
        var original = registry.Register(
            new RawWindow(1, 100, "Untitled", "Notepad", new Rect(0, 0, 10, 10)));
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
            var enumerator = new WindowEnumerator(new WindowApi(), new WindowRegistry());
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
                if (!notepad.HasExited)
                {
                    notepad.Kill(entireProcessTree: true);
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
}

internal sealed class FakeWindowApi : IWindowApi
{
    private readonly IReadOnlyList<RawWindow> _windows;

    public FakeWindowApi(params RawWindow[] windows)
    {
        _windows = windows;
    }

    public IReadOnlyList<RawWindow> GetTopLevelWindows() => _windows;

    public static FakeWindowApi With(
        RawWindow? normal = null,
        RawWindow? tool = null,
        RawWindow? cloaked = null,
        RawWindow? invisible = null,
        RawWindow? untitled = null)
        => new([.. new[] { normal, tool, cloaked, invisible, untitled }.OfType<RawWindow>()]);
}
