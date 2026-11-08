namespace Shitie.Windows;

internal enum CopyIntent { None, Copy, Cut }

// Only C/X/Insert/Delete shortcuts are classified; no typed text is retained.
internal sealed class CopyGesture : IDisposable
{
    private readonly object _gate = new();
    private readonly Native.HookProc _callback;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private IntPtr _hook;
    private uint _threadId, _source;
    private long _timestamp;
    private bool _cut;
    private int _controlKeys, _shiftKeys, _altKeys;
    public bool Available => _hook != IntPtr.Zero;
    public CopyGesture()
    {
        _callback = Handle;
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            Native.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            _hook = Native.SetWindowsHookEx(13, _callback, Native.GetModuleHandle(null), 0);
            _ready.Set();
            while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "Shitie copy shortcut classifier" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
    }
    private IntPtr Handle(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<Native.KeyboardData>(data).Key;
            var down = message.ToInt64() is 0x100 or 0x104;
            void Modifier(ref int state, uint generic, uint left, uint right)
            {
                var bit = key == left ? 1 : key == right ? 2 : key == generic ? 4 : 0;
                if (bit != 0) { if (down) state |= bit; else state &= ~bit; }
            }
            Modifier(ref _controlKeys, 0x11, 0xA2, 0xA3);
            Modifier(ref _shiftKeys, 0x10, 0xA0, 0xA1);
            Modifier(ref _altKeys, 0x12, 0xA4, 0xA5);
            if (!down) return Native.CallNextHookEx(_hook, code, message, data);
            var ctrl = _controlKeys != 0;
            var shift = _shiftKeys != 0;
            var alt = _altKeys != 0;
            bool copy = !alt && ((ctrl && key is 0x43 or 0x2D) || (!ctrl && shift && key == 0x2E));
            bool cut = !alt && ((ctrl && key == 0x58) || (!ctrl && shift && key == 0x2E));
            if (copy || cut)
            {
                lock (_gate) { _cut = cut; _timestamp = Environment.TickCount64; _source = Native.ProcessId(Native.GetForegroundWindow()); }
            }
        }
        return Native.CallNextHookEx(_hook, code, message, data);
    }
    public CopyIntent Consume(uint source)
    {
        lock (_gate)
        {
            var recent = _timestamp != 0 && Environment.TickCount64 - _timestamp < 2200 && source == _source;
            var result = recent ? (_cut ? CopyIntent.Cut : CopyIntent.Copy) : CopyIntent.None;
            _timestamp = 0;
            return result;
        }
    }
    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, 0x12, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
