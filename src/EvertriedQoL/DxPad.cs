using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace EvertriedQoL
{
    /// <summary>
    /// Gamepad input for the native Linux build. The game reads pads through Unity's legacy joystick
    /// axes, whose numbering is the Windows XInput one. On Linux the same physical control lands on a
    /// different axis depending on the driver (the right trigger shows up as the game's d-pad X, the
    /// d-pad hat as its d-pad Y, triggers rest at -1, ...), so menus only respond to the left stick and
    /// the d-pad is swapped or dead. This class reads the pads from evdev instead, using the kernel's
    /// driver-independent codes, follows whichever pad was used last, and answers the game's own
    /// questions (a ControllerKeyCode button, a named axis) with the correct value.
    /// If no pad can be read everything falls through to Unity's original behaviour.
    /// The patcher injects these entry points into Assembly-CSharp.dll.
    /// </summary>
    public static class DxPad
    {
        // Semantic buttons, in the order of the game's ControllerKeyCode enum.
        private const int A = 0, B = 1, X = 2, Y = 3, LB = 4, RB = 5, Select = 6, Start = 7, LStick = 8, RStick = 9;
        private const int DUp = 10, DRight = 11, DDown = 12, DLeft = 13, LTrigger = 14, RTrigger = 15;
        private const int Count = 16;

        private static readonly string[] Names =
        {
            "A", "B", "X", "Y", "LB", "RB", "Select", "Start", "LStick", "RStick",
            "DPadUp", "DPadRight", "DPadDown", "DPadLeft", "LT", "RT",
        };

        // linux/input-event-codes.h
        private const int BtnNorth = 0x133, BtnWest = 0x134, BtnTl = 0x136, BtnTr = 0x137, BtnTl2 = 0x138, BtnTr2 = 0x139;
        private const int BtnSelect = 0x13a, BtnStart = 0x13b, BtnThumbL = 0x13d, BtnThumbR = 0x13e;
        private const int BtnDpadUp = 0x220, BtnDpadDown = 0x221, BtnDpadLeft = 0x222, BtnDpadRight = 0x223;
        private const int AbsX = 0x00, AbsY = 0x01, AbsZ = 0x02, AbsRx = 0x03, AbsRy = 0x04, AbsRz = 0x05;
        private const int AbsHat0X = 0x10, AbsHat0Y = 0x11;

        private const float StickDeadzone = 0.19f; // same as the game's InputManager axes
        private const float TriggerPress = 0.5f;
        private const float RescanSeconds = 2f;

        private static readonly List<EvdevPad> Pads = new List<EvdevPad>();
        private static readonly Dictionary<string, bool> Rejected = new Dictionary<string, bool>();
        private static EvdevPad active;
        private static int frame = -1;
        private static float nextScan;
        private static bool scanFailed;

        private static readonly bool[] Current = new bool[Count];
        private static readonly bool[] Previous = new bool[Count];
        private static readonly bool[] Pressed = new bool[Count];
        private static readonly bool[] Released = new bool[Count];
        private static bool anyPressed;
        private static float lx, ly, rx, ry, dx, dy, trigger;

        /// <summary>True when a gamepad is being read and the game's pad queries should be answered here.</summary>
        public static bool Active()
        {
            Ensure();
            return active != null;
        }

        /// <summary>Replacement for ControllerInputs.GetControllerValue(int buttonIndex, ButtonState).</summary>
        public static bool Button(int semantic, ControllerInputs.ButtonState state)
        {
            Ensure();
            if (active == null || semantic < 0 || semantic >= Count)
            {
                return false;
            }
            switch (state)
            {
                case ControllerInputs.ButtonState.Pressed:
                    return Pressed[semantic];
                case ControllerInputs.ButtonState.Held:
                    return Current[semantic] && !Pressed[semantic];
                default:
                    return Released[semantic];
            }
        }

        /// <summary>Replacement for Input.GetAxisRaw(string).</summary>
        public static float GetAxisRaw(string name)
        {
            return Axis(name, true);
        }

        /// <summary>Replacement for Input.GetAxis(string).</summary>
        public static float GetAxis(string name)
        {
            return Axis(name, false);
        }

        /// <summary>Replacement for Input.anyKeyDown: also true when a pad button, d-pad or trigger went down.</summary>
        public static bool AnyKeyDown()
        {
            if (Input.anyKeyDown)
            {
                return true;
            }
            Ensure();
            return active != null && anyPressed;
        }

        private static float Axis(string name, bool raw)
        {
            Ensure();
            if (active != null && name != null)
            {
                // The game picks the axis name by glyph set (e.g. a PS4 variant of the vertical d-pad); every
                // variant means the same physical control here, so match on the prefix.
                if (name.StartsWith("HorizontalLStick")) { return lx; }
                if (name.StartsWith("VerticalLStick")) { return ly; }
                if (name.StartsWith("HorizontalRStick")) { return rx; }
                if (name.StartsWith("VerticalRStick")) { return ry; }
                if (name.StartsWith("HorizontalDPad")) { return dx; }
                if (name.StartsWith("VerticalDPad")) { return dy; }
                if (name.StartsWith("Triggers")) { return trigger; }
            }
            return raw ? Input.GetAxisRaw(name) : Input.GetAxis(name);
        }

        // ---- once per frame -------------------------------------------------------------------------

        private static void Ensure()
        {
            int now = Time.frameCount;
            if (now == frame)
            {
                return;
            }
            frame = now;
            DxHost.EnsureCreated();

            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + RescanSeconds;
                Scan();
            }
            Poll();
        }

        private static void Scan()
        {
            if (scanFailed)
            {
                return;
            }
            try
            {
                for (int n = 0; n < 32; n++)
                {
                    string sys = "/sys/class/input/event" + n + "/device/name";
                    if (!File.Exists(sys))
                    {
                        continue;
                    }
                    string path = "/dev/input/event" + n;
                    string name = File.ReadAllText(sys).Trim();
                    string key = path + "|" + name;
                    if (Rejected.ContainsKey(key) || IsOpen(path))
                    {
                        continue;
                    }
                    EvdevPad pad = EvdevPad.TryOpen(path, name);
                    if (pad == null)
                    {
                        Rejected[key] = true;
                        continue;
                    }
                    Pads.Add(pad);
                    Debug.Log("[EvertriedQoL] Gamepad found: " + pad);
                    if (active == null)
                    {
                        Activate(pad);
                    }
                }
            }
            catch (System.Exception e)
            {
                // e.g. P/Invoke unavailable: give up quietly and let Unity's own input run.
                scanFailed = true;
                Debug.Log("[EvertriedQoL] Gamepad scan disabled: " + e.GetType().Name + " " + e.Message);
            }
        }

        private static bool IsOpen(string path)
        {
            for (int i = 0; i < Pads.Count; i++)
            {
                if (Pads[i].Path == path)
                {
                    return true;
                }
            }
            return false;
        }

        private static void Activate(EvdevPad pad)
        {
            if (active == pad)
            {
                return;
            }
            active = pad;
            Debug.Log("[EvertriedQoL] Active gamepad: " + pad + (Layout.PositionalFaceButtons(pad) ? " (positional face buttons)" : " (Xbox-labelled face buttons)"));
            // The game picked its axis names before a pad was active; pick them again now that one is.
            try
            {
                InputHandler handler = AutomatedSingleton<InputHandler>.Instance;
                if (handler != null)
                {
                    handler.OnControllerModeChange(ScriptableSingleton<InputSettings>.Instance.ControllerType);
                }
            }
            catch (System.Exception e)
            {
                Debug.Log("[EvertriedQoL] Could not refresh the axis names: " + e.Message);
            }
        }

        private static void Poll()
        {
            EvdevPad newest = null;
            for (int i = Pads.Count - 1; i >= 0; i--)
            {
                EvdevPad pad = Pads[i];
                pad.ClearEdges();
                bool meaningful;
                if (!pad.Drain(out meaningful))
                {
                    Debug.Log("[EvertriedQoL] Gamepad removed: " + pad);
                    Pads.RemoveAt(i);
                    pad.Close();
                    if (active == pad)
                    {
                        active = Pads.Count > 0 ? Pads[Pads.Count - 1] : null;
                        ResetState();
                    }
                    continue;
                }
                if (meaningful)
                {
                    newest = pad;
                }
            }
            if (newest != null && newest != active)
            {
                Activate(newest);
            }
            if (active == null)
            {
                ResetState();
                return;
            }
            Compute(active);
        }

        private static void ResetState()
        {
            for (int i = 0; i < Count; i++)
            {
                Previous[i] = false;
                Current[i] = false;
                Pressed[i] = false;
                Released[i] = false;
            }
            anyPressed = false;
            lx = ly = rx = ry = dx = dy = trigger = 0f;
        }

        private static float Dead(float value)
        {
            return System.Math.Abs(value) < StickDeadzone ? 0f : value;
        }

        private static void Compute(EvdevPad pad)
        {
            bool positional = Layout.PositionalFaceButtons(pad);
            int codeX = positional ? BtnWest : BtnNorth;
            int codeY = positional ? BtnNorth : BtnWest;

            // Sticks: evdev Y grows downwards, the game expects up to be positive.
            lx = Dead(pad.Normalized(AbsX));
            ly = -Dead(pad.Normalized(AbsY));
            rx = Dead(pad.Normalized(AbsRx));
            ry = -Dead(pad.Normalized(AbsRy));

            // D-pad: hat axes (xpad, Steam virtual pad, hid-playstation) or buttons (hid-nintendo).
            dx = pad.Normalized(AbsHat0X);
            dy = -pad.Normalized(AbsHat0Y);
            if (pad.Keys[BtnDpadLeft]) { dx = -1f; }
            if (pad.Keys[BtnDpadRight]) { dx = 1f; }
            if (pad.Keys[BtnDpadUp]) { dy = 1f; }
            if (pad.Keys[BtnDpadDown]) { dy = -1f; }

            float lt = pad.Unipolar(AbsZ);
            float rt = pad.Unipolar(AbsRz);
            bool ltDown = lt > TriggerPress || pad.Keys[BtnTl2];
            bool rtDown = rt > TriggerPress || pad.Keys[BtnTr2];
            // The game's combined trigger axis: right trigger +1, left trigger -1.
            trigger = (rtDown ? 1f : rt) - (ltDown ? 1f : lt);

            for (int i = 0; i < Count; i++)
            {
                Previous[i] = Current[i];
            }
            Current[A] = pad.Keys[EvdevPad.BtnSouth];
            Current[B] = pad.Keys[0x131];
            Current[X] = pad.Keys[codeX];
            Current[Y] = pad.Keys[codeY];
            Current[LB] = pad.Keys[BtnTl];
            Current[RB] = pad.Keys[BtnTr];
            Current[Select] = pad.Keys[BtnSelect];
            Current[Start] = pad.Keys[BtnStart];
            Current[LStick] = pad.Keys[BtnThumbL];
            Current[RStick] = pad.Keys[BtnThumbR];
            Current[DUp] = dy > 0.5f;
            Current[DRight] = dx > 0.5f;
            Current[DDown] = dy < -0.5f;
            Current[DLeft] = dx < -0.5f;
            Current[LTrigger] = ltDown;
            Current[RTrigger] = rtDown;

            anyPressed = false;
            for (int i = 0; i < Count; i++)
            {
                Pressed[i] = Current[i] && !Previous[i];
                Released[i] = !Current[i] && Previous[i];
            }
            // Taps shorter than one frame still count as a press.
            Pressed[A] |= pad.KeyWentDown[EvdevPad.BtnSouth];
            Pressed[B] |= pad.KeyWentDown[0x131];
            Pressed[X] |= pad.KeyWentDown[codeX];
            Pressed[Y] |= pad.KeyWentDown[codeY];
            Pressed[LB] |= pad.KeyWentDown[BtnTl];
            Pressed[RB] |= pad.KeyWentDown[BtnTr];
            Pressed[Select] |= pad.KeyWentDown[BtnSelect];
            Pressed[Start] |= pad.KeyWentDown[BtnStart];
            Pressed[LStick] |= pad.KeyWentDown[BtnThumbL];
            Pressed[RStick] |= pad.KeyWentDown[BtnThumbR];

            for (int i = 0; i < Count; i++)
            {
                if (Pressed[i])
                {
                    anyPressed = true;
                    if (File.Exists("/tmp/evertried-dx-debug"))
                    {
                        Debug.Log("[EvertriedQoL] pad " + Names[i] + " pressed");
                    }
                }
            }
        }
    }

    /// <summary>Per-driver differences in which evdev code a face button uses.</summary>
    internal static class Layout
    {
        private const ushort Sony = 0x054c;
        private const ushort Nintendo = 0x057e;

        /// <summary>
        /// True when the driver names buttons by position (BTN_WEST = left face button, BTN_NORTH = top),
        /// which is what hid-playstation and hid-nintendo do. xpad and Steam's virtual Xbox pad instead
        /// report the Xbox labels (BTN_X = 0x133 for the left button, BTN_Y = 0x134 for the top one).
        /// </summary>
        public static bool PositionalFaceButtons(EvdevPad pad)
        {
            return pad.Vendor == Sony || pad.Vendor == Nintendo;
        }
    }
}
