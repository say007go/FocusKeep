using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FocusKeep
{
    /// <summary>
    /// 全局键盘锁：用户态低级键盘钩子（WH_KEYBOARD_LL）。
    ///
    /// 设计约束（都是踩过的坑）：
    ///  1. 钩子回调必须极快——Windows 有 LowLevelHooksTimeout，回调超时会**静默卸载钩子**。
    ///     所以回调里只做"比对组合键"，解锁动作 Post 回 UI 线程。
    ///  2. 委托必须存字段，否则被 GC 回收后钩子失效。
    ///  3. 进程退出/崩溃时系统会自动摘钩子，所以不会把用户永久锁死。
    ///  4. Ctrl+Alt+Del 走安全桌面，用户态钩子收不到，天然挡不住（这是 Windows 的设计）。
    /// </summary>
    internal sealed class KeyboardLock : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;      // Alt
        private const int VK_U = 0x55;
        private const int VK_L = 0x4C;

        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private static KeyboardLock _current;   // 静态引用，保证钩子回调存活

        // 钩子吞掉按键之后，系统的异步键状态（GetAsyncKeyState）在部分机器上不会更新，
        // 所以这里自己根据钩子收到的事件维护修饰键状态，两种来源取"或"。
        private static bool _ctrlDown;
        private static bool _altDown;
        private static bool _shiftDown;

        private readonly SynchronizationContext _ui;
        private readonly object _gate = new object();
        private LowLevelKeyboardProc _proc;
        private IntPtr _hook = IntPtr.Zero;
        private Timer _timeoutTimer;
        private DateTime _deadlineUtc;
        private string _lastReason = string.Empty;

        public KeyboardLock(SynchronizationContext ui)
        {
            _ui = ui ?? new SynchronizationContext();
        }

        public bool IsLocked
        {
            get { lock (_gate) { return _hook != IntPtr.Zero; } }
        }

        public string LastReason
        {
            get { lock (_gate) { return _lastReason; } }
        }

        public event EventHandler Unlocked;

        public bool Lock(int maxMinutes)
        {
            lock (_gate)
            {
                if (_hook != IntPtr.Zero) return false;

                // 清掉上一轮可能残留的修饰键状态（例如锁定前按着 Shift）
                _ctrlDown = false;
                _altDown = false;
                _shiftDown = false;

                _proc = HookProc;               // 保留引用，防止 GC
                _current = this;
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

                if (_hook == IntPtr.Zero)
                {
                    _proc = null;
                    _current = null;
                    return false;
                }

                int minutes = Config.Clamp(maxMinutes, 1, 480);
                _deadlineUtc = DateTime.UtcNow.AddMinutes(minutes);
                _timeoutTimer = new Timer(OnTimeout, null, 1000, 1000);
                return true;
            }
        }

        public void Unlock(string reason)
        {
            bool changed = false;

            lock (_gate)
            {
                if (_hook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                    changed = true;
                }
                _current = null;
                _proc = null;

                if (_timeoutTimer != null)
                {
                    _timeoutTimer.Dispose();
                    _timeoutTimer = null;
                }
                _lastReason = reason ?? string.Empty;
            }

            if (changed)
            {
                EventHandler handler = Unlocked;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        /// <summary>可从任意线程调用：统一 Post 回 UI 线程再解锁。</summary>
        internal void RequestUnlock(string reason)
        {
            _ui.Post(delegate
            {
                if (IsLocked) Unlock(reason);
            }, null);
        }

        private void OnTimeout(object state)
        {
            lock (_gate)
            {
                if (_hook == IntPtr.Zero) return;
                if (DateTime.UtcNow < _deadlineUtc) return;
            }
            RequestUnlock("到达最长锁定时间");
        }

        // ---------------- 钩子 ----------------

        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            int message = wParam.ToInt32();
            KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            int vkCode = (int)data.vkCode;

            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
            {
                TrackModifier(vkCode, true);

                KeyboardLock self = _current;
                if (self != null && self.IsUnlockCombo(vkCode))
                {
                    self.RequestUnlock("解锁组合键");
                    return (IntPtr)1;
                }
            }
            else if (message == WM_KEYUP || message == WM_SYSKEYUP)
            {
                TrackModifier(vkCode, false);
            }

            // 锁定期间：按下与抬起一概吞掉，任何程序都收不到按键
            return (IntPtr)1;
        }

        private static void TrackModifier(int vkCode, bool down)
        {
            switch (vkCode)
            {
                case VK_CONTROL:
                case VK_LCONTROL:
                case VK_RCONTROL:
                    _ctrlDown = down;
                    break;

                case VK_MENU:
                case VK_LMENU:
                case VK_RMENU:
                    _altDown = down;
                    break;

                case VK_SHIFT:
                case VK_LSHIFT:
                case VK_RSHIFT:
                    _shiftDown = down;
                    break;
            }
        }

        /// <summary>
        /// 两个解锁组合：
        ///   Ctrl + Alt + Shift + U —— 主组合，不容易误触；
        ///   Ctrl + Alt + L         —— 锁定热键的"再按一次"，符合菜单里显示的按键直觉。
        /// </summary>
        private bool IsUnlockCombo(int vkCode)
        {
            bool ctrl = IsDown(VK_CONTROL) || _ctrlDown;
            bool alt = IsDown(VK_MENU) || _altDown;
            bool shift = IsDown(VK_SHIFT) || _shiftDown;

            if (!ctrl || !alt) return false;
            if (vkCode == VK_U && shift) return true;
            if (vkCode == VK_L && !shift) return true;
            return false;
        }

        private static bool IsDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        public void Dispose()
        {
            Unlock("程序退出");
        }
    }
}
