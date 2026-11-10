using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Windows;

/// <summary>Enumerates WinForms screens and their effective DPI.</summary>
public sealed class WindowsMonitorProvider : IMonitorProvider
{
    private const uint MonitorDefaultToNearest = 2;
    private const int EffectiveDpi = 0;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var screens = Screen.AllScreens;
        var monitors = new List<MonitorInfo>(screens.Length);
        for (var index = 0; index < screens.Length; index++)
        {
            var screen = screens[index];
            monitors.Add(new MonitorInfo
            {
                Id = $"mon_{index}",
                Name = screen.Primary ? "Primary" : screen.DeviceName,
                Bounds = new Rect
                {
                    X = screen.Bounds.X,
                    Y = screen.Bounds.Y,
                    Width = screen.Bounds.Width,
                    Height = screen.Bounds.Height,
                },
                Scale = GetScale(screen),
                Primary = screen.Primary,
            });
        }

        return monitors;
    }

    private static double GetScale(Screen screen)
    {
        try
        {
            var center = new Point(
                screen.Bounds.Left + (screen.Bounds.Width / 2),
                screen.Bounds.Top + (screen.Bounds.Height / 2));
            var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero
                && GetDpiForMonitor(monitor, EffectiveDpi, out var dpiX, out _) == 0
                && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return 1.0;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
