using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace UnityAIBridge
{
    /// <summary>Brings the already-running Unity Editor window to the front.</summary>
    public static class EditorFocusService
    {
        public static JsonValue Focus()
        {
            bool activated = false;
            string error = null;

            try
            {
#if UNITY_EDITOR_LINUX
                activated = LinuxWindowFocus.TryActivate(Process.GetCurrentProcess().Id);
#endif
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            try
            {
                var window = EditorWindow.focusedWindow;
                if (window != null) window.Focus();
            }
            catch { }

            return JsonValue.Obj(
                "ok", true,
                "focused", activated,
                "platform", Application.platform.ToString(),
                "message", activated
                    ? "Brought the Unity Editor window to the front."
                    : "Could not activate the OS window automatically. Switch to Unity from the taskbar.",
                "error", error);
        }

#if UNITY_EDITOR_LINUX
        static class LinuxWindowFocus
        {
            const string Lib = "libX11.so.6";
            const int ClientMessage = 33;
            const int RevertToParent = 1;

            [StructLayout(LayoutKind.Sequential, Size = 192)]
            struct XEvent
            {
                public int type;
                public IntPtr serial;
                public int send_event;
                public IntPtr display;
                public IntPtr window;
                public IntPtr message_type;
                public int format;
                public IntPtr data1;
                public IntPtr data2;
                public IntPtr data3;
                public IntPtr data4;
                public IntPtr data5;
            }

            [DllImport(Lib)] static extern IntPtr XOpenDisplay(string name);
            [DllImport(Lib)] static extern int XCloseDisplay(IntPtr display);
            [DllImport(Lib)] static extern IntPtr XDefaultRootWindow(IntPtr display);
            [DllImport(Lib)] static extern int XQueryTree(IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent, out IntPtr children, out uint count);
            [DllImport(Lib)] static extern int XFetchName(IntPtr display, IntPtr window, out IntPtr name);
            [DllImport(Lib)] static extern int XFree(IntPtr data);
            [DllImport(Lib)] static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
            [DllImport(Lib)] static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr offset, IntPtr length, bool delete, IntPtr requiredType, out IntPtr actualType, out int actualFormat, out IntPtr itemCount, out IntPtr bytesAfter, out IntPtr data);
            [DllImport(Lib)] static extern int XGetGeometry(IntPtr display, IntPtr window, out IntPtr root, out int x, out int y, out uint width, out uint height, out uint borderWidth, out uint depth);
            [DllImport(Lib)] static extern int XMapRaised(IntPtr display, IntPtr window);
            [DllImport(Lib)] static extern int XRaiseWindow(IntPtr display, IntPtr window);
            [DllImport(Lib)] static extern int XSetInputFocus(IntPtr display, IntPtr window, int revertTo, IntPtr time);
            [DllImport(Lib)] static extern int XSendEvent(IntPtr display, IntPtr window, bool propagate, IntPtr eventMask, ref XEvent evt);
            [DllImport(Lib)] static extern int XFlush(IntPtr display);

            public static bool TryActivate(int processId)
            {
                IntPtr display = XOpenDisplay(null);
                if (display == IntPtr.Zero) return false;

                try
                {
                    IntPtr root = XDefaultRootWindow(display);
                    IntPtr rootReturn, parentReturn, children;
                    uint count;
                    if (XQueryTree(display, root, out rootReturn, out parentReturn, out children, out count) == 0)
                        return false;

                    IntPtr target = IntPtr.Zero;
                    ulong bestArea = 0;
                    try
                    {
                        for (uint i = 0; i < count; i++)
                        {
                            IntPtr window = Marshal.ReadIntPtr(children, (int)i * IntPtr.Size);
                            if (GetProcessId(display, window) != processId) continue;

                            string title = GetTitle(display, window);
                            if (title.StartsWith("Unity - ", StringComparison.Ordinal))
                            {
                                target = window;
                                break;
                            }

                            ulong area = GetArea(display, window);
                            if (area > bestArea)
                            {
                                bestArea = area;
                                target = window;
                            }
                        }
                    }
                    finally
                    {
                        if (children != IntPtr.Zero) XFree(children);
                    }

                    if (target == IntPtr.Zero) return false;

                    XMapRaised(display, target);
                    XRaiseWindow(display, target);
                    XSetInputFocus(display, target, RevertToParent, IntPtr.Zero);

                    IntPtr activeAtom = XInternAtom(display, "_NET_ACTIVE_WINDOW", false);
                    if (activeAtom != IntPtr.Zero)
                    {
                        var evt = new XEvent
                        {
                            type = ClientMessage,
                            display = display,
                            window = target,
                            message_type = activeAtom,
                            format = 32,
                            data1 = new IntPtr(2),
                            data2 = IntPtr.Zero,
                            data3 = IntPtr.Zero,
                            data4 = IntPtr.Zero,
                            data5 = IntPtr.Zero
                        };
                        XSendEvent(display, root, false, new IntPtr(0x00180000), ref evt);
                    }

                    XFlush(display);
                    return true;
                }
                finally
                {
                    XCloseDisplay(display);
                }
            }

            static string GetTitle(IntPtr display, IntPtr window)
            {
                IntPtr name;
                if (XFetchName(display, window, out name) == 0 || name == IntPtr.Zero) return string.Empty;
                try { return Marshal.PtrToStringAnsi(name) ?? string.Empty; }
                finally { XFree(name); }
            }

            static int GetProcessId(IntPtr display, IntPtr window)
            {
                IntPtr atom = XInternAtom(display, "_NET_WM_PID", true);
                if (atom == IntPtr.Zero) return 0;

                IntPtr actualType, itemCount, bytesAfter, data;
                int actualFormat;
                int status = XGetWindowProperty(display, window, atom, IntPtr.Zero, new IntPtr(1), false,
                    IntPtr.Zero, out actualType, out actualFormat, out itemCount, out bytesAfter, out data);
                if (status != 0 || data == IntPtr.Zero) return 0;

                try { return Marshal.ReadInt32(data); }
                finally { XFree(data); }
            }

            static ulong GetArea(IntPtr display, IntPtr window)
            {
                IntPtr root;
                int x, y;
                uint width, height, border, depth;
                if (XGetGeometry(display, window, out root, out x, out y, out width, out height, out border, out depth) == 0)
                    return 0;
                return (ulong)width * height;
            }
        }
#endif
    }
}
