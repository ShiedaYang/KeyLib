using System;
using System.Drawing;
using System.Runtime.InteropServices;

internal sealed class PopupInput : IDisposable
{
    internal const int MessageId = 0x8042;
    internal const int Cancel = 1;
    internal const int Confirm = 2;
    private const int Released = 3;
    internal const int Previous = 4;
    internal const int Next = 5;
    private const int Escape = 0x1b;
    private const int Enter = 0x0d;
    private const int Up = 0x26;
    private const int Down = 0x28;
    private readonly IntPtr receiver;
    private readonly Hook mouseCallback;
    private readonly Hook keyCallback;
    private IntPtr mouseHook;
    private IntPtr keyHook;
    private IntPtr panel;
    private IntPtr target;
    private int serial;
    private int held;

    internal bool ArrowSelection { get; set; }

    internal PopupInput(IntPtr receiver)
    {
        this.receiver = receiver;
        mouseCallback = Mouse;
        keyCallback = Keyboard;
    }

    internal bool Start(IntPtr popup, IntPtr foreground, int token)
    {
        panel = popup;
        target = foreground;
        serial = token;
        IntPtr module = GetModuleHandle(null);
        if (mouseHook == IntPtr.Zero) mouseHook = SetWindowsHookEx(14, mouseCallback, module, 0);
        if (keyHook == IntPtr.Zero) keyHook = SetWindowsHookEx(13, keyCallback, module, 0);
        return mouseHook != IntPtr.Zero && keyHook != IntPtr.Zero;
    }

    internal void Stop()
    {
        ArrowSelection = false;
        panel = target = IntPtr.Zero;
        Remove(ref mouseHook);
        if (held == 0) Remove(ref keyHook);
    }

    internal bool IsReleasedMessage(int code)
    {
        if (code != Released) return false;
        if (panel == IntPtr.Zero && held == 0) Remove(ref keyHook);
        return true;
    }

    private IntPtr Mouse(int code, IntPtr message, IntPtr data)
    {
        int input = message.ToInt32();
        if (code >= 0 && panel != IntPtr.Zero && (input == 0x201 || input == 0x204 || input == 0x207 || input == 0x20b))
        {
            var point = new Point(Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4));
            if (GetAncestor(Native.WindowFromPoint(point), 2) != panel)
                PostMessage(receiver, MessageId, (IntPtr)Cancel, (IntPtr)serial);
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(IntPtr.Zero, code, message, data);
        int key = Marshal.ReadInt32(data);
        int action;
        switch (key)
        {
            case Enter: action = Confirm; break;
            case Escape: action = Cancel; break;
            case Up: action = Previous; break;
            case Down: action = Next; break;
            default: return CallNextHookEx(IntPtr.Zero, code, message, data);
        }
        int mask = 1 << action;
        int input = message.ToInt32();
        if ((held & mask) != 0)
        {
            if (input == 0x101 || input == 0x105)
            {
                held &= ~mask;
                if (held == 0) PostMessage(receiver, MessageId, (IntPtr)Released, IntPtr.Zero);
            }
            return (IntPtr)1;
        }

        if (panel == IntPtr.Zero || target != Native.GetForegroundWindow() || input != 0x100 ||
            ((action == Previous || action == Next) && !ArrowSelection) ||
            GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x10) < 0 || GetAsyncKeyState(0x12) < 0 ||
            GetAsyncKeyState(0x5b) < 0 || GetAsyncKeyState(0x5c) < 0)
            return CallNextHookEx(IntPtr.Zero, code, message, data);

        held |= mask;
        PostMessage(receiver, MessageId, (IntPtr)action, (IntPtr)serial);
        return (IntPtr)1;
    }

    private static void Remove(ref IntPtr hook)
    {
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    public void Dispose()
    {
        panel = target = IntPtr.Zero;
        held = 0;
        Remove(ref mouseHook);
        Remove(ref keyHook);
    }

    private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr value, IntPtr token);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
}
