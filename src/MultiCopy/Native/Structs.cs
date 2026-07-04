using System.Runtime.InteropServices;

namespace MultiCopy.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

/// <summary>
/// WH_KEYBOARD_LL 回调收到的结构。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KBDLLHOOKSTRUCT
{
    public int VkCode;
    public int ScanCode;
    public int Flags;
    public int Time;
    public IntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int Dx;
    public int Dy;
    public uint MouseData;
    public uint DwFlags;
    public uint Time;
    public IntPtr DwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT
{
    public ushort WVk;
    public ushort WScan;
    public uint DwFlags;
    public uint Time;
    public IntPtr DwExtraInfo;
}

[StructLayout(LayoutKind.Explicit)]
internal struct INPUTUNION
{
    [FieldOffset(0)] public MOUSEINPUT Mi;
    [FieldOffset(0)] public KEYBDINPUT Ki;
}

/// <summary>
/// SendInput 用的 INPUT 结构（含 union）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct INPUT
{
    public uint Type;
    public INPUTUNION U;

    public static int Size => Marshal.SizeOf<INPUT>();
}
