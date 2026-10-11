using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GoToSleep
{
    static class Native
    {
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);

        static readonly string[] ShellClasses = { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW" };

        // Visible, top-level app windows from other processes (not the taskbar/desktop or tool windows).
        public static bool IsOtherAppWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd) || GetWindow(hWnd, 4 /* GW_OWNER */) != IntPtr.Zero) return false;
            if ((GetWindowLong(hWnd, -20 /* GWL_EXSTYLE */) & WS_EX_TOOLWINDOW) != 0) return false;
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == (uint)Process.GetCurrentProcess().Id) return false;
            var cls = new StringBuilder(64);
            GetClassName(hWnd, cls, cls.Capacity);
            return Array.IndexOf(ShellClasses, cls.ToString()) < 0;
        }

        // Async so a hung or busy game can't freeze the lock screen. Minimizing an exclusive-fullscreen game
        // makes it give up the display.
        public static void Minimize(IntPtr hWnd)
        {
            ShowWindowAsync(hWnd, 6 /* SW_MINIMIZE */);
        }

        // Un-minimizes (if needed) and focuses a window, if it still exists.
        public static void Restore(IntPtr hWnd)
        {
            if (!IsWindow(hWnd)) return;
            if (IsIconic(hWnd)) ShowWindowAsync(hWnd, 9 /* SW_RESTORE */);
            ForceForeground(hWnd);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

        // True when a game owns a display in exclusive fullscreen (QUNS_RUNNING_D3D_FULL_SCREEN). Borderless doesn't count.
        public static bool ExclusiveFullscreenRunning()
        {
            int state;
            return SHQueryUserNotificationState(out state) == 0 && state == 3;
        }

        public static Rectangle? WindowBounds(IntPtr hWnd)
        {
            RECT r;
            if (hWnd == IntPtr.Zero || !GetWindowRect(hWnd, out r)) return null;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        public static void ForceForeground(IntPtr hWnd)
        {
            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint me = GetCurrentThreadId();
            bool attach = fgThread != 0 && fgThread != me;
            if (attach) AttachThreadInput(me, fgThread, true);
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
            if (attach) AttachThreadInput(me, fgThread, false);
        }

        public static void TopMost(IntPtr hWnd)
        {
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    // Swallows the shortcuts that would let you get around the lock screen (Win, Alt+Tab, Alt+F4, Ctrl+Esc, ...).
    // Ctrl+Alt+Del can't be blocked by any app; Task Manager is disabled separately for that.
    static class KeyboardBlocker
    {
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode, scanCode, flags, time;
            public IntPtr extraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        const int WH_KEYBOARD_LL = 13;
        const uint LLKHF_ALTDOWN = 0x20;
        static readonly HookProc Proc = Callback;
        static IntPtr hook = IntPtr.Zero;

        public static void Enable()
        {
            if (hook != IntPtr.Zero) return;
            using (var p = Process.GetCurrentProcess())
            using (var m = p.MainModule)
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, Proc, Native.GetModuleHandle(m.ModuleName), 0);
        }

        public static void Disable()
        {
            if (hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                var key = (Keys)k.vkCode;
                bool alt = (k.flags & LLKHF_ALTDOWN) != 0;
                bool ctrl = (Native.GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0;
                if (key == Keys.LWin || key == Keys.RWin ||
                    (alt && (key == Keys.Tab || key == Keys.Escape || key == Keys.F4 || key == Keys.Space)) ||
                    (ctrl && key == Keys.Escape))
                    return new IntPtr(1);
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }
    }

    static class TaskManagerPolicy
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";

        public static void Set(bool disabled)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (disabled) k.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
                    else if (k.GetValue("DisableTaskMgr") != null) k.DeleteValue("DisableTaskMgr", false);
                }
            }
            catch (Exception ex) { Log.Write("Task Manager policy: " + ex.Message); }
        }

        public static void KillRunning()
        {
            foreach (var p in Process.GetProcessesByName("Taskmgr"))
            {
                try { p.Kill(); } catch { }
                p.Dispose();
            }
        }
    }

    static class Power
    {
        public static void Shutdown()
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
        }

        public static void Sleep()
        {
            Application.SetSuspendState(PowerState.Suspend, false, false);
        }
    }

    // Mutes the default speakers through the Core Audio API.
    static class Audio
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints();
            void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            void RegisterControlChangeNotify();
            void UnregisterControlChangeNotify();
            void GetChannelCount();
            void SetMasterVolumeLevel();
            void SetMasterVolumeLevelScalar();
            void GetMasterVolumeLevel();
            void GetMasterVolumeLevelScalar();
            void SetChannelVolumeLevel();
            void SetChannelVolumeLevelScalar();
            void GetChannelVolumeLevel();
            void GetChannelVolumeLevelScalar();
            void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        static IAudioEndpointVolume Endpoint()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device;
            enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device);
            var iid = typeof(IAudioEndpointVolume).GUID;
            object result;
            device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out result);
            return (IAudioEndpointVolume)result;
        }

        public static bool IsMuted()
        {
            try
            {
                bool muted;
                Endpoint().GetMute(out muted);
                return muted;
            }
            catch { return false; }
        }

        public static bool SetMute(bool mute)
        {
            try
            {
                var context = Guid.Empty;
                Endpoint().SetMute(mute, ref context);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Mute failed: " + ex.Message);
                return false;
            }
        }
    }
}
