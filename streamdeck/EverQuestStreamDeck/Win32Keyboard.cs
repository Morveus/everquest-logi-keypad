namespace EverQuestStreamDeck
{
    using System;
    using System.Runtime.InteropServices;

    // Stream Deck gives a plugin no way to send a keystroke, so this goes to Win32
    // directly.
    //
    // Two things here are not incidental:
    //
    // 1. SCAN CODES, not virtual keys. EverQuest's gem bindings are on the number row,
    //    and the player's layout decides what character that row produces - ALT+& on
    //    AZERTY is the same physical key as ALT+1 on QWERTY. Sending a scan code says
    //    "the second key of the top row", which is what the game actually reads. Sending
    //    VK_1 would send the character and break on any non-US layout.
    //
    // 2. The INPUT struct must be exactly 40 bytes on x64. Declaring it with only the
    //    keyboard union gives 32, SendInput rejects the wrong cbSize, and it fails
    //    returning zero with no exception and no visible symptom - the keys simply do
    //    nothing. That is a full evening lost once already; the explicit Size constant
    //    below is checked at startup.
    internal static class Win32Keyboard
    {
        private const UInt32 InputKeyboard = 1;
        private const UInt32 KeyEventScanCode = 0x0008;
        private const UInt32 KeyEventKeyUp = 0x0002;

        // Set 1 scan codes for the number row, left to right: 1 2 3 4 5 6 7 8 9 0.
        private static readonly UInt16[] NumberRow = { 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B };
        private const UInt16 ScanLeftAlt = 0x38;

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public UInt16 Vk;
            public UInt16 Scan;
            public UInt32 Flags;
            public UInt32 Time;
            public IntPtr ExtraInfo;
        }

        // MOUSEINPUT is declared even though no mouse input is ever sent: it is the
        // largest member of the union, and its size is what makes INPUT 40 bytes on x64
        // instead of 32. Omitting it is the mistake described above.
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public Int32 Dx;
            public Int32 Dy;
            public UInt32 MouseData;
            public UInt32 Flags;
            public UInt32 Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public UInt32 Type;
            public InputUnion Union;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern UInt32 SendInput(UInt32 count, Input[] inputs, Int32 size);

        // True if the struct layout is the one SendInput expects. Checked once at
        // startup so a wrong layout is a log line rather than silently dead keys.
        public static Boolean LayoutIsSane => Marshal.SizeOf<Input>() == (IntPtr.Size == 8 ? 40 : 28);

        // gem is 1-based. Gems 1 to 10 sit on the number row; anything beyond has no
        // default binding in EverQuest, so there is nothing to send.
        public static Boolean SendAltGem(Int32 gem)
        {
            if (gem < 1 || gem > NumberRow.Length) { return false; }
            return SendAltScan(NumberRow[gem - 1]);
        }

        public static Boolean SendAltScan(UInt16 scan)
        {
            var inputs = new[]
            {
                Key(ScanLeftAlt, false),
                Key(scan, false),
                Key(scan, true),
                Key(ScanLeftAlt, true),
            };
            var sent = SendInput((UInt32)inputs.Length, inputs, Marshal.SizeOf<Input>());
            return sent == inputs.Length;
        }

        private static Input Key(UInt16 scan, Boolean up) => new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    Vk = 0,
                    Scan = scan,
                    Flags = KeyEventScanCode | (up ? KeyEventKeyUp : 0),
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };
    }
}
