using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;
internal sealed class EditFields
{
    internal AutomationElement Username;
    internal AutomationElement Password;
    internal int Count;

    internal static EditFields Find(IntPtr target)
    {
        var fields = new EditFields();
        AutomationElement root = AutomationElement.FromHandle(target);
        if (root == null)
            return fields;
        var edits = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        var plain = new List<AutomationElement>();
        var passwords = new List<AutomationElement>();
        foreach (AutomationElement element in edits)
        {
            var data = element.Current;
            if (!data.IsEnabled || data.IsOffscreen || !data.IsKeyboardFocusable)
                continue;
            if (data.IsPassword)
                passwords.Add(element);
            else
                plain.Add(element);
        }

        fields.Count = plain.Count + passwords.Count;
        if (passwords.Count == 1 && plain.Count == 1)
        {
            fields.Username = plain[0];
            fields.Password = passwords[0];
        }
        return fields;
    }
}

internal static class Native
{
    internal delegate void ForegroundCallback(IntPtr hook, uint type, IntPtr window, int objectId,
        int childId, uint thread, uint time);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr window, int id, int modifiers, int key);
    [DllImport("user32.dll")]
    internal static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, ForegroundCallback callback,
        uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    internal static bool TypeIntoForeground(IntPtr target, string text)
    {
        const uint up = 2;
        const uint unicode = 4;
        var keys = new List<Input> { Key(0x11, 0, 0), Key(0x41, 0, 0), Key(0x41, 0, up), Key(0x11, 0, up) };
        foreach (char character in text)
        {
            if (char.IsControl(character)) return false;
            keys.Add(Key(0, character, unicode));
            keys.Add(Key(0, character, unicode | up));
        }
        return CanSendTo(target) && Send(keys.ToArray());
    }

    internal static bool TabInForeground(IntPtr target, bool reverse)
    {
        const ushort tab = 0x09;
        return CanSendTo(target) && Send(reverse ? new[] { Key(0x10, 0, 0), Key(tab, 0, 0), Key(tab, 0, 2), Key(0x10, 0, 2) } : new[] { Key(tab, 0, 0), Key(tab, 0, 2) });
    }

    private static bool CanSendTo(IntPtr target)
    {
        return GetForegroundWindow() == target && GetAsyncKeyState(0x10) >= 0 &&
            GetAsyncKeyState(0x11) >= 0 && GetAsyncKeyState(0x12) >= 0;
    }

#if QA
    internal static bool PressTestKey(ushort key)
    {
        return Send(new[] { Key(key, 0, 0), Key(key, 0, 2) });
    }

    internal static bool PressTestHotkey(int key)
    {
        // Used exclusively by the local, data-free self-test fixture.
        const uint up = 2;
        ushort vk = (ushort)key;
        return Send(new[]
        {
            Key(0x12, 0, 0), Key(vk, 0, 0), Key(vk, 0, up), Key(0x12, 0, up)
        });
    }

    internal static bool ClickTestButton()
    {
        return Send(new[]
        {
            new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = 2 } } },
            new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = 4 } } }
        });
    }
#endif

    private static bool Send(Input[] inputs)
    {
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) == (uint)inputs.Length;
    }

    private static Input Key(ushort key, ushort scan, uint flags)
    {
        return new Input { Type = 1, Data = new InputUnion { Keyboard =
            new KeyboardInput { Key = key, Scan = scan, Flags = flags } } };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Key;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public UIntPtr Extra;
    }
}
