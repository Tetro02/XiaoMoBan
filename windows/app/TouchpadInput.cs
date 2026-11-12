using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PenNoteBridge
{
    internal sealed class TouchpadInput
    {
        const uint RightDown = 0x0008, RightUp = 0x0010;
        const uint Wheel = 0x0800, HorizontalWheel = 0x1000;
        const ushort Control = 0x11, LeftControl = 0xa2;
        const int WheelDelta = 120;
        double x, y, zoom;

        internal void Reset() { x = y = zoom = 0; }

        internal void Send(CanvasNavigation motion)
        {
            if (motion.Action == 1)
            {
                if (SendInput(2, new[] { Mouse(RightDown, 0), Mouse(RightUp, 0) }, InputSize) == 1)
                    SendInput(1, new[] { Mouse(RightUp, 0) }, InputSize);
                return;
            }
            int vertical = Steps(ref y, motion.PanY * 12);
            int horizontal = Steps(ref x, -motion.PanX * 12);
            int magnification = Steps(ref zoom, Math.Log(motion.Scale) * 8);
            if (vertical == 0 && horizontal == 0 && magnification == 0) return;
            var input = new List<Input>(5);
            if (vertical != 0) input.Add(Mouse(Wheel, vertical * WheelDelta));
            if (horizontal != 0) input.Add(Mouse(HorizontalWheel, horizontal * WheelDelta));
            int controlIndex = -1;
            if (magnification != 0)
            {
                bool pressControl = GetAsyncKeyState(Control) >= 0;
                if (pressControl)
                {
                    controlIndex = input.Count;
                    input.Add(Key(LeftControl, false));
                }
                input.Add(Mouse(Wheel, magnification * WheelDelta));
                if (pressControl) input.Add(Key(LeftControl, true));
            }
            uint sent = SendInput((uint)input.Count, input.ToArray(), InputSize);
            if (controlIndex >= 0 && sent > controlIndex && sent < input.Count)
                SendInput(1, new[] { Key(LeftControl, true) }, InputSize);
        }

        static int Steps(ref double remainder, double amount)
        {
            remainder += amount;
            int steps = (int)remainder;
            remainder -= steps;
            return steps;
        }

        static Input Mouse(uint flags, int data)
        {
            return new Input { Value = new InputValue { Mouse = new MouseInput { Flags = flags, Data = data } } };
        }

        static Input Key(ushort key, bool up)
        {
            return new Input { Kind = 1, Value = new InputValue { Key = new KeyInput { Key = key, Flags = up ? 2u : 0 } } };
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MouseInput
        {
            internal int X, Y, Data;
            internal uint Flags, Time;
            internal IntPtr Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KeyInput
        {
            internal ushort Key, Scan;
            internal uint Flags, Time;
            internal IntPtr Extra;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputValue
        {
            [FieldOffset(0)] internal MouseInput Mouse;
            [FieldOffset(0)] internal KeyInput Key;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Input
        {
            internal uint Kind;
            internal InputValue Value;
        }

        static readonly int InputSize = Marshal.SizeOf(typeof(Input));

        [DllImport("user32.dll")]
        static extern uint SendInput(uint count, [In] Input[] input, int size);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int key);
    }
}
