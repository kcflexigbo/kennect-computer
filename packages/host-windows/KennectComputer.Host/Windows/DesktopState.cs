using System.Runtime.InteropServices;
using System.Text;

namespace KennectComputer.Host.Windows;

/// <summary>
/// Detects the lock screen by comparing the currently input desktop with the
/// session's "Default" desktop. When the workstation is locked the input desktop
/// is "Winlogon" (or opening it is denied), so the names differ.
/// </summary>
public sealed class DesktopState : IDesktopState
{
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopSwitchDesktop = 0x0100;
    private const int UserObjectName = 2;

    public bool IsLocked()
    {
        var inputDesktop = OpenInputDesktop(0, false, DesktopReadObjects | DesktopSwitchDesktop);
        if (inputDesktop == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            var defaultDesktop = OpenDesktop("Default", 0, false, DesktopReadObjects | DesktopSwitchDesktop);
            if (defaultDesktop == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var inputName = GetDesktopName(inputDesktop);
                var defaultName = GetDesktopName(defaultDesktop);
                if (inputName is null || defaultName is null)
                {
                    return false;
                }

                return !string.Equals(inputName, defaultName, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                CloseDesktop(defaultDesktop);
            }
        }
        finally
        {
            CloseDesktop(inputDesktop);
        }
    }

    private static string? GetDesktopName(IntPtr desktop)
    {
        var buffer = new StringBuilder(256);
        return GetUserObjectInformation(
            desktop,
            UserObjectName,
            buffer,
            buffer.Capacity * sizeof(char),
            out _)
            ? buffer.ToString()
            : null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenDesktop(string desktop, uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle,
        int index,
        StringBuilder info,
        int length,
        out int lengthNeeded);
}
