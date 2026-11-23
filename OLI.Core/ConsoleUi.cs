using System.Runtime.InteropServices;
using System.Text;

namespace OLI.Core;

/// <summary>
/// Makes a Windows console able to show Cyrillic.
///
/// A console started with an OEM code page (866/437) — or with a raster font that has no
/// Cyrillic glyphs — prints every Russian letter as "?". This helper switches the output to
/// UTF-8, asks Windows for a TrueType font (Consolas) and enables VT escape sequences, so
/// the OLI server window shows readable Russian text and colour instead of question marks.
/// </summary>
public static class ConsoleUi
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const int CodePageUtf8 = 65001;

    public static void Prepare(string title = "OLI Server")
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Redirected output (pipes, CI) — keep going: the console itself needs no setup.
        }

        if (!OperatingSystem.IsWindows()) return;

        try
        {
            Console.Title = title;
            SetConsoleOutputCP(CodePageUtf8);
            SetConsoleCP(CodePageUtf8);
            UseTrueTypeFont();
            EnableAnsi();
        }
        catch
        {
            // Older Windows or no console window: keep going with plain ASCII.
        }
    }

    /// <summary>Waits for Enter so a double-clicked exe does not vanish with its error message.</summary>
    public static void PauseIfNeeded(string? hint = null)
    {
        try
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected) return;
            Console.WriteLine();
            Console.WriteLine(hint ?? "Нажмите Enter, чтобы закрыть окно…");
            Console.ReadLine();
        }
        catch
        {
            // No console: nothing to wait for.
        }
    }

    private static void UseTrueTypeFont()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
        if (!GetCurrentConsoleFontEx(handle, false, out var info)) return;

        // "Terminal" / raster fonts cannot draw Cyrillic — ask for Consolas instead.
        var face = info.FaceName ?? "";
        if (face.Contains("Consolas", StringComparison.OrdinalIgnoreCase)) return;

        info.FaceName = "Consolas";
        info.FontFamily = 54;   // FF_MODERN | TMPF_VECTOR | TMPF_TRUETYPE
        info.FontWeight = 400;
        info.FontSize = new Coord { X = 0, Y = 18 };
        info.Font = 0;
        SetCurrentConsoleFontEx(handle, false, ref info);
    }

    private static void EnableAnsi()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
        if (!GetConsoleMode(handle, out var mode)) return;
        SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    // ---------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConsoleFontInfoEx
    {
        public uint Size;
        public uint Font;
        public Coord FontSize;
        public uint FontFamily;
        public uint FontWeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCP(uint codePage);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCurrentConsoleFontEx(IntPtr console, bool maximumWindow, out ConsoleFontInfoEx info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCurrentConsoleFontEx(IntPtr console, bool maximumWindow, ref ConsoleFontInfoEx info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr console, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr console, uint mode);
}
