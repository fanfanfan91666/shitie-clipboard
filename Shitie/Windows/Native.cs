namespace Shitie.Windows;

internal static class Native
{
    internal const int WmClipboardUpdate = 0x031D, WmHotkey = 0x0312;
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32")] internal static extern IntPtr GetClipboardOwner();
    [DllImport("user32")] internal static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseClipboard();
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EmptyClipboard();
    [DllImport("user32", SetLastError = true)] internal static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32", SetLastError = true)] internal static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32")] internal static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32", SetLastError = true)] internal static extern IntPtr GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32")] internal static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32")] internal static extern nuint GlobalSize(IntPtr handle);
    [DllImport("kernel32")] internal static extern IntPtr GlobalFree(IntPtr handle);
    [DllImport("shell32", CharSet = CharSet.Unicode)] internal static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint size);
    [DllImport("user32")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32")] internal static extern int GetMessage(out Message message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PeekMessage(out Message message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] internal static extern short GetKeyState(int key);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("dwmapi")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] internal struct Message { public IntPtr Hwnd; public uint Id; public IntPtr WParam, LParam; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardData { public uint Key, ScanCode, Flags, Time; public IntPtr Extra; }
    internal static uint ProcessId(IntPtr hwnd) { GetWindowThreadProcessId(hwnd, out var id); return id; }
    internal static string ProcessName(IntPtr hwnd)
    {
        try { return Process.GetProcessById((int)ProcessId(hwnd)).ProcessName; }
        catch { return "应用"; }
    }
}
