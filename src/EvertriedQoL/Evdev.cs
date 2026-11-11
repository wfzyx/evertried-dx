using System;
using System.IO;
using System.Runtime.InteropServices;

namespace EvertriedQoL
{
    /// <summary>
    /// A Linux gamepad exposed through evdev (/dev/input/eventN). The kernel reports gamepads with
    /// standard button/axis codes whatever the driver (xpad, hid-playstation, hid-nintendo, Steam's
    /// virtual pad, ...), unlike Unity's legacy joystick axis numbers which differ per driver.
    /// </summary>
    internal sealed class EvdevPad
    {
        // linux/input-event-codes.h
        public const int EvKey = 1;
        public const int EvAbs = 3;
        public const int BtnSouth = 0x130;
        public const int AbsCount = 0x40;
        public const int KeyCount = 0x300;

        private const int OReadOnly = 0;
        private const int ONonBlock = 0x800;
        private const int EAgain = 11;
        private const int EventSize = 24; // struct input_event on 64-bit: timeval(16) + type(2) + code(2) + value(4)

        public readonly string Path;
        public readonly string Name;
        public readonly ushort Vendor;
        public readonly ushort Product;

        public readonly bool[] Keys = new bool[KeyCount];
        public readonly int[] AbsRaw = new int[AbsCount];
        public readonly int[] AbsMin = new int[AbsCount];
        public readonly int[] AbsMax = new int[AbsCount];
        public readonly bool[] HasAbs = new bool[AbsCount];

        // Edges seen while draining events, so taps shorter than a frame are not lost.
        public readonly bool[] KeyWentDown = new bool[KeyCount];
        public readonly bool[] KeyWentUp = new bool[KeyCount];

        private int fd;
        private readonly byte[] buffer = new byte[EventSize * 64];

        private EvdevPad(string path, string name, ushort vendor, ushort product, int fd)
        {
            Path = path;
            Name = name;
            Vendor = vendor;
            Product = product;
            this.fd = fd;
        }

        public bool IsOpen => fd >= 0;

        /// <summary>Opens the device if it is a gamepad (has BTN_SOUTH and a left stick or d-pad).</summary>
        public static EvdevPad TryOpen(string path, string name)
        {
            int handle = Native.open(path, OReadOnly | ONonBlock);
            if (handle < 0)
            {
                return null;
            }

            byte[] keyBits = new byte[KeyCount / 8 + 8];
            if (Native.ioctl(handle, Native.EviocgBit(EvKey, keyBits.Length), keyBits) < 0 || !Bit(keyBits, BtnSouth))
            {
                Native.close(handle);
                return null;
            }

            byte[] id = new byte[8];
            ushort vendor = 0;
            ushort product = 0;
            if (Native.ioctl(handle, Native.EviocgId, id) >= 0)
            {
                vendor = BitConverter.ToUInt16(id, 2);
                product = BitConverter.ToUInt16(id, 4);
            }

            EvdevPad pad = new EvdevPad(path, name, vendor, product, handle);

            byte[] absBits = new byte[8];
            Native.ioctl(handle, Native.EviocgBit(EvAbs, absBits.Length), absBits);
            byte[] info = new byte[24];
            for (int code = 0; code < AbsCount; code++)
            {
                if (!Bit(absBits, code) || Native.ioctl(handle, Native.EviocgAbs(code), info) < 0)
                {
                    continue;
                }
                pad.HasAbs[code] = true;
                pad.AbsRaw[code] = BitConverter.ToInt32(info, 0);
                pad.AbsMin[code] = BitConverter.ToInt32(info, 4);
                pad.AbsMax[code] = BitConverter.ToInt32(info, 8);
            }

            return pad;
        }

        private static bool Bit(byte[] bits, int index)
        {
            int byteIndex = index >> 3;
            return byteIndex < bits.Length && (bits[byteIndex] & (1 << (index & 7))) != 0;
        }

        public void ClearEdges()
        {
            Array.Clear(KeyWentDown, 0, KeyWentDown.Length);
            Array.Clear(KeyWentUp, 0, KeyWentUp.Length);
        }

        /// <summary>Reads every pending event. Returns false when the device is gone.</summary>
        public bool Drain(out bool meaningful)
        {
            meaningful = false;
            while (fd >= 0)
            {
                long count = (long)Native.read(fd, buffer, (IntPtr)buffer.Length);
                if (count < 0)
                {
                    if (Marshal.GetLastWin32Error() == EAgain)
                    {
                        return true;
                    }
                    Close();
                    return false;
                }
                if (count == 0)
                {
                    return true;
                }

                for (int offset = 0; offset + EventSize <= count; offset += EventSize)
                {
                    int type = BitConverter.ToUInt16(buffer, offset + 16);
                    int code = BitConverter.ToUInt16(buffer, offset + 18);
                    int value = BitConverter.ToInt32(buffer, offset + 20);
                    if (type == EvKey && code < KeyCount)
                    {
                        bool down = value != 0;
                        if (value == 1)
                        {
                            KeyWentDown[code] = true;
                            meaningful = true;
                        }
                        else if (value == 0 && Keys[code])
                        {
                            KeyWentUp[code] = true;
                        }
                        Keys[code] = down;
                    }
                    else if (type == EvAbs && code < AbsCount && HasAbs[code])
                    {
                        AbsRaw[code] = value;
                        if (Math.Abs(Normalized(code)) > 0.5f)
                        {
                            meaningful = true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>Axis position in [-1, 1] (centred axes) with the raw range taken from the driver.</summary>
        public float Normalized(int code)
        {
            if (code >= AbsCount || !HasAbs[code])
            {
                return 0f;
            }
            float min = AbsMin[code];
            float max = AbsMax[code];
            if (max <= min)
            {
                return 0f;
            }
            float mid = (min + max) * 0.5f;
            float half = (max - min) * 0.5f;
            float value = (AbsRaw[code] - mid) / half;
            return value < -1f ? -1f : (value > 1f ? 1f : value);
        }

        /// <summary>Axis position in [0, 1] for one-sided axes such as analog triggers.</summary>
        public float Unipolar(int code)
        {
            if (code >= AbsCount || !HasAbs[code])
            {
                return 0f;
            }
            float min = AbsMin[code];
            float max = AbsMax[code];
            if (max <= min)
            {
                return 0f;
            }
            float value = (AbsRaw[code] - min) / (max - min);
            return value < 0f ? 0f : (value > 1f ? 1f : value);
        }

        public void Close()
        {
            if (fd >= 0)
            {
                Native.close(fd);
                fd = -1;
            }
        }

        public override string ToString()
        {
            return Name + " (" + Hex4(Vendor) + ":" + Hex4(Product) + ") " + Path;
        }

        // The game's stripped mscorlib has no ushort.ToString(string).
        private static string Hex4(int value)
        {
            const string digits = "0123456789abcdef";
            char[] text = new char[4];
            for (int i = 3; i >= 0; i--)
            {
                text[i] = digits[value & 0xf];
                value >>= 4;
            }
            return new string(text);
        }
    }

    internal static class Native
    {
        private const string Libc = "libc.so.6";

        [DllImport(Libc, SetLastError = true)]
        public static extern int open(string path, int flags);

        [DllImport(Libc)]
        public static extern int close(int fd);

        [DllImport(Libc, SetLastError = true)]
        public static extern IntPtr read(int fd, byte[] buf, IntPtr count);

        [DllImport(Libc)]
        public static extern int ioctl(int fd, ulong request, byte[] data);

        private static ulong Ioc(int dir, int type, int nr, int size)
        {
            return ((ulong)(uint)dir << 30) | ((ulong)(uint)size << 16) | ((ulong)(uint)type << 8) | (ulong)(uint)nr;
        }

        // linux/input.h
        public static readonly ulong EviocgId = Ioc(2, 'E', 0x02, 8);
        public static ulong EviocgBit(int ev, int len) { return Ioc(2, 'E', 0x20 + ev, len); }
        public static ulong EviocgAbs(int abs) { return Ioc(2, 'E', 0x40 + abs, 24); }

        private static ulong Ioc(int dir, char type, int nr, int size)
        {
            return Ioc(dir, (int)type, nr, size);
        }
    }
}
