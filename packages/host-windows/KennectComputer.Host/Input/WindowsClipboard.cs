using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KennectComputer.Host.Input;

/// <summary>
/// The production <see cref="IClipboard"/>, backed by the Win32 clipboard APIs
/// (DESIGN §6.2). It uses <c>OpenClipboard</c>/<c>GetClipboardData</c>/
/// <c>SetClipboardData</c> directly rather than <c>System.Windows.Forms</c>,
/// so it works from any thread the host calls it on and needs no STA.
///
/// Clipboard ownership is process-wide and short-lived in other applications,
/// so opening is retried briefly before failing. A failure to read or write
/// throws <see cref="Win32Exception"/> instead of reporting an empty clipboard,
/// which would let a paste action overwrite the user's real clipboard content.
/// </summary>
public sealed class WindowsClipboard : IClipboard
{
    private const uint CfText = 1;
    private const uint CfOemText = 7;
    private const uint CfUnicodeText = 13;
    private const uint CfLocale = 16;
    private const uint GmemMoveable = 0x0002;
    private const int OpenAttempts = 10;
    private const int OpenRetryDelayMilliseconds = 10;

    /// <summary>
    /// Enumerates the clipboard formats. Any format outside the plain-text set
    /// (an image, copied files, HTML, …) cannot survive a text-only
    /// save/restore, and text without CF_UNICODETEXT is ANSI-only, which
    /// <see cref="GetText"/> would read as no text and then destroy. An empty
    /// clipboard is safe: the restore writes an empty text clipboard.
    /// </summary>
    public bool HasUnsupportedContent()
    {
        BeginOpen();
        try
        {
            var hasUnicodeText = GetClipboardData(CfUnicodeText) != nint.Zero;
            var sawFormat = false;

            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                sawFormat = true;
                if (format is not (CfText or CfOemText or CfUnicodeText or CfLocale))
                {
                    return true;
                }
            }

            var error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new Win32Exception(
                    error,
                    "Could not enumerate the clipboard formats.");
            }

            return !hasUnicodeText && sawFormat;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public string? GetText()
    {
        BeginOpen();
        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == nint.Zero)
            {
                return null;
            }

            var pointer = GlobalLock(handle);
            if (pointer == nint.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not lock the clipboard text.");
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var handle = AllocateMoveableString(text);
        try
        {
            BeginOpen();
            try
            {
                if (!EmptyClipboard())
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not empty the clipboard.");
                }

                if (SetClipboardData(CfUnicodeText, handle) == nint.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not set the clipboard text.");
                }

                // A successful SetClipboardData transfers ownership of the
                // global memory to the clipboard; freeing it here would corrupt
                // the clipboard contents.
                handle = nint.Zero;
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            if (handle != nint.Zero)
            {
                GlobalFree(handle);
            }
        }
    }

    private static void BeginOpen()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (OpenClipboard(nint.Zero))
            {
                return;
            }

            Thread.Sleep(OpenRetryDelayMilliseconds);
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            "Could not open the clipboard.");
    }

    /// <summary>
    /// Allocates a movable, null-terminated UTF-16 copy of
    /// <paramref name="text"/> for <c>SetClipboardData</c>.
    /// </summary>
    private static nint AllocateMoveableString(string text)
    {
        var bytes = ((nuint)text.Length + 1) * sizeof(char);
        var handle = GlobalAlloc(GmemMoveable, bytes);
        if (handle == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not allocate clipboard memory.");
        }

        var pointer = GlobalLock(handle);
        if (pointer == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            GlobalFree(handle);
            throw new Win32Exception(error, "Could not lock the new clipboard text.");
        }

        try
        {
            Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);
}
