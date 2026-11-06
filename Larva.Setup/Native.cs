using System.Runtime.InteropServices;

namespace Larva.Setup;

/// <summary>The Windows calls the wizard draws itself with (user32, gdi32, comctl32, shell32): no UI framework, so it stays a small native program.</summary>
internal static unsafe partial class Native
{
    public const uint WS_OVERLAPPED = 0, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000, WS_BORDER = 0x00800000;
    public const uint WS_EX_CLIENTEDGE = 0x200, WS_EX_CONTROLPARENT = 0x10000;
    public const uint BS_PUSHBUTTON = 0, BS_DEFPUSHBUTTON = 1, BS_AUTOCHECKBOX = 3, BS_MULTILINE = 0x2000;
    public const uint ES_AUTOHSCROLL = 0x80, SS_LEFT = 0, SS_NOPREFIX = 0x80, SS_EDITCONTROL = 0x2000;
    public const uint WM_DESTROY = 0x2, WM_PAINT = 0xF, WM_CLOSE = 0x10, WM_SETFONT = 0x30, WM_COMMAND = 0x111, WM_CTLCOLORSTATIC = 0x138, WM_APP = 0x8000;
    public const uint BM_GETCHECK = 0xF0, BM_SETCHECK = 0xF1, BM_SETSTYLE = 0xF4, PBM_SETPOS = 0x402, PBM_SETRANGE32 = 0x406;
    public const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNORMAL = 1;
    public const int TRANSPARENT = 1, DT_LEFT = 0, DT_WORDBREAK = 0x10, DT_SINGLELINE = 0x20, DT_VCENTER = 4, DT_CENTER = 1, DT_NOPREFIX = 0x800;
    public const uint MB_OK = 0, MB_YESNO = 4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONINFORMATION = 0x40;
    public const int IDYES = 6;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int x, y;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BROWSEINFOW
    {
        public nint hwndOwner, pidlRoot, pszDisplayName, lpszTitle;
        public uint ulFlags;
        public nint lpfn, lParam;
        public int iImage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INITCOMMONCONTROLSEX
    {
        public uint dwSize, dwICC;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")] public static partial ushort RegisterClassEx(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] public static partial nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] public static partial int GetMessage(MSG* message, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll", EntryPoint = "IsDialogMessageW")] public static partial int IsDialogMessage(nint hwnd, MSG* message);
    [LibraryImport("user32.dll")] public static partial int TranslateMessage(MSG* message);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] public static partial nint DispatchMessage(MSG* message);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] public static partial int ShowWindow(nint hwnd, int command);
    [LibraryImport("user32.dll")] public static partial int UpdateWindow(nint hwnd);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")] public static partial nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] public static partial int PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] public static partial int DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(nint hwnd, string text, string caption, uint type);
    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")] public static partial nint LoadCursor(nint instance, nint name);
    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")] public static partial nint LoadIcon(nint instance, nint name);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForSystem();
    [LibraryImport("user32.dll")] public static partial int AdjustWindowRectEx(RECT* rect, uint style, int menu, uint exStyle);
    [LibraryImport("user32.dll")] public static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] public static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* paint);
    [LibraryImport("user32.dll")] public static partial int EndPaint(nint hwnd, PAINTSTRUCT* paint);
    [LibraryImport("user32.dll")] public static partial int FillRect(nint hdc, RECT* rect, nint brush);
    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawText(nint hdc, string text, int length, RECT* rect, uint format);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetWindowText(nint hwnd, string text);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")] public static partial int GetWindowText(nint hwnd, char* buffer, int max);
    [LibraryImport("user32.dll")] public static partial int EnableWindow(nint hwnd, int enable);
    [LibraryImport("user32.dll")] public static partial int InvalidateRect(nint hwnd, RECT* rect, int erase);
    [LibraryImport("user32.dll")] public static partial nint SetFocus(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int PrintWindow(nint hwnd, nint hdc, uint flags);
    [LibraryImport("user32.dll")] public static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int ReleaseDC(nint hwnd, nint hdc);
    [LibraryImport("user32.dll")] public static partial int GetClientRect(nint hwnd, RECT* rect);
    [LibraryImport("user32.dll")] public static partial int SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")] public static partial int PeekMessage(MSG* message, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] public static partial int GetWindowRect(nint hwnd, RECT* rect);
    [LibraryImport("user32.dll")] public static partial int MoveWindow(nint hwnd, int x, int y, int width, int height, int repaint);
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW")] public static partial nint LoadImage(nint instance, nint name, uint type, int width, int height, uint flags);
    [LibraryImport("user32.dll")] public static partial int DrawIconEx(nint hdc, int x, int y, nint icon, int width, int height, uint step, nint flicker, uint flags);
    [LibraryImport("gdi32.dll")] public static partial int Ellipse(nint hdc, int left, int top, int right, int bottom);
    [LibraryImport("gdi32.dll")] public static partial nint GetStockObject(int index);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [LibraryImport("gdi32.dll")] public static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] public static partial nint CreatePen(int style, int width, uint color);
    [LibraryImport("gdi32.dll")] public static partial int DeleteObject(nint handle);
    [LibraryImport("gdi32.dll")] public static partial nint SelectObject(nint hdc, nint handle);
    [LibraryImport("gdi32.dll")] public static partial int SetBkMode(nint hdc, int mode);
    [LibraryImport("gdi32.dll")] public static partial uint SetTextColor(nint hdc, uint color);
    [LibraryImport("gdi32.dll")] public static partial int MoveToEx(nint hdc, int x, int y, nint previous);
    [LibraryImport("gdi32.dll")] public static partial int LineTo(nint hdc, int x, int y);
    [LibraryImport("gdi32.dll")] public static partial int RoundRect(nint hdc, int left, int top, int right, int bottom, int width, int height);
    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] public static partial int DeleteDC(nint hdc);
    [LibraryImport("gdi32.dll")] public static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* info, uint usage, void** bits, nint section, uint offset);

    [LibraryImport("comctl32.dll")] public static partial int InitCommonControlsEx(INITCOMMONCONTROLSEX* controls);
    [LibraryImport("shell32.dll", EntryPoint = "SHBrowseForFolderW")] public static partial nint SHBrowseForFolder(BROWSEINFOW* info);
    [LibraryImport("shell32.dll", EntryPoint = "SHGetPathFromIDListW")] public static partial int SHGetPathFromIDList(nint pidl, char* path);
    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint ShellExecute(nint hwnd, string? operation, string file, string? parameters, string? directory, int show);
    [LibraryImport("ole32.dll")] public static partial int CoInitializeEx(nint reserved, uint model);
    [LibraryImport("ole32.dll")] public static partial void CoTaskMemFree(nint memory);
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")] public static partial nint GetModuleHandle(nint name);
    [LibraryImport("kernel32.dll")] public static partial int AttachConsole(int process);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeout(nint window, uint message, nint wParam, string lParam, uint flags, uint timeout, out nint result);

    /// <summary>Tells running programs (Explorer, new terminals) that the environment changed.</summary>
    public static void BroadcastEnvironment() => SendMessageTimeout(0xFFFF, 0x001A, 0, "Environment", 0x0002, 5000, out _);

    [LibraryImport("shell32.dll")] private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    /// <summary>Tells Explorer that file types changed (their icons are drawn again).</summary>
    public static void AssociationsChanged() => SHChangeNotify(0x08000000, 0, 0, 0);

    [LibraryImport("advapi32.dll", EntryPoint = "RegLoadAppKeyW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegLoadAppKey(string file, out nint key, uint access, uint options, uint reserved);
    [LibraryImport("advapi32.dll")] public static partial int RegOverridePredefKey(nint key, nint replacement);
    [LibraryImport("advapi32.dll")] public static partial int RegCloseKey(nint key);

    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
}
