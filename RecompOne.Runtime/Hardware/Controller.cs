namespace RecompOne.Runtime.Hardware;

public static class Controller
{
    static int _virtualButtons;
    static int _physicalButtons;
    static int _physicalAxes;
    static int _physicalConnected;

    public const ushort Select = 1 << 0;
    public const ushort L3 = 1 << 1;
    public const ushort R3 = 1 << 2;
    public const ushort Start = 1 << 3;
    public const ushort Up = 1 << 4;
    public const ushort Right = 1 << 5;
    public const ushort Down = 1 << 6;
    public const ushort Left = 1 << 7;
    public const ushort L2 = 1 << 8;
    public const ushort R2 = 1 << 9;
    public const ushort L1 = 1 << 10;
    public const ushort R1 = 1 << 11;
    public const ushort Triangle = 1 << 12;
    public const ushort Circle = 1 << 13;
    public const ushort Cross = 1 << 14;
    public const ushort Square = 1 << 15;

    public static ushort State = 0xFFFF;
    public static byte   RightX = 0x80;
    public static byte   RightY = 0x80;
    public static byte   LeftX = 0x80;
    public static byte   LeftY = 0x80;

    public static ushort State2 = 0xFFFF;
    public static bool   Connected2;
    public static byte   RightX2 = 0x80;
    public static byte   RightY2 = 0x80;
    public static byte   LeftX2 = 0x80;
    public static byte   LeftY2 = 0x80;

    /// <summary>Active-high buttons supplied by a platform virtual controller.</summary>
    public static ushort VirtualButtons =>
        (ushort)System.Threading.Volatile.Read(ref _virtualButtons);

    public static void SetVirtualPadState(ushort pressedButtons) =>
        System.Threading.Volatile.Write(ref _virtualButtons, pressedButtons);

    /// <summary>True when a platform physical gamepad is currently connected.</summary>
    public static bool PhysicalConnected =>
        System.Threading.Volatile.Read(ref _physicalConnected) != 0;

    /// <summary>Active-high buttons from a platform physical gamepad (Android HID, …).</summary>
    public static ushort PhysicalButtons =>
        (ushort)System.Threading.Volatile.Read(ref _physicalButtons);

    public static byte PhysicalLeftX => AxisByte(0);
    public static byte PhysicalLeftY => AxisByte(8);
    public static byte PhysicalRightX => AxisByte(16);
    public static byte PhysicalRightY => AxisByte(24);

    /// <summary>
    /// Host rumble when SDL does not own a pad (Android InputDevice vibrator).
    /// </summary>
    public static Action<byte, byte>? RumbleRequested;

    public static void SetPhysicalPadState(ushort pressedButtons,
        byte leftX, byte leftY, byte rightX, byte rightY, bool connected)
    {
        System.Threading.Volatile.Write(ref _physicalButtons, pressedButtons);
        System.Threading.Volatile.Write(ref _physicalAxes,
            leftX | (leftY << 8) | (rightX << 16) | (rightY << 24));
        System.Threading.Volatile.Write(ref _physicalConnected, connected ? 1 : 0);
    }

    static byte AxisByte(int shift) =>
        (byte)((System.Threading.Volatile.Read(ref _physicalAxes) >> shift) & 0xFF);
}
