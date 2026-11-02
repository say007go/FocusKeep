using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace FocusKeep
{
    /// <summary>
    /// 原生 Win32 弹出菜单。
    ///
    /// 相比 WinForms 的 ContextMenuStrip，原生菜单由系统绘制：
    /// Windows 11 上自动是圆角、系统主题色、系统动画的真实"系统菜单"，
    /// 深色模式下也跟着系统走，而且勾选区/间距由系统统一处理，不会出现自绘的错位。
    /// </summary>
    internal sealed class NativeMenu : IDisposable
    {
        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint MF_CHECKED = 0x00000008;
        private const uint MF_GRAYED = 0x00000001;
        private const uint MF_POPUP = 0x00000010;

        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint WM_NULL = 0x0000;

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr item, string text);

        [DllImport("user32.dll")]
        private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attachTo, uint attach, bool attachFlag);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        private readonly IntPtr _handle;
        private readonly List<NativeMenu> _children = new List<NativeMenu>();
        private bool _disposed;

        public NativeMenu()
        {
            _handle = CreatePopupMenu();
        }

        public void Add(int id, string text, bool isChecked, bool enabled)
        {
            uint flags = MF_STRING;
            if (isChecked) flags |= MF_CHECKED;
            if (!enabled) flags |= MF_GRAYED;
            AppendMenu(_handle, flags, new UIntPtr((uint)id), text);
        }

        public void Add(int id, string text)
        {
            Add(id, text, false, true);
        }

        public void AddDisabled(string text)
        {
            Add(0, text, false, false);
        }

        public void AddSeparator()
        {
            AppendMenu(_handle, MF_SEPARATOR, UIntPtr.Zero, null);
        }

        public NativeMenu AddSubMenu(string text)
        {
            NativeMenu child = new NativeMenu();
            _children.Add(child);
            AppendMenu(_handle, MF_POPUP, new UIntPtr((uint)child._handle.ToInt64()), text);
            return child;
        }

        /// <summary>默认项在原生菜单里会加粗显示（用来强调"解锁键盘"）。</summary>
        public void SetDefaultItem(int id)
        {
            SetMenuDefaultItem(_handle, (uint)id, 0);
        }

        /// <summary>弹出菜单并返回被选中的命令 id（取消返回 0）。</summary>
        public int Show(IntPtr owner, Point screen)
        {
            if (_handle == IntPtr.Zero) return 0;

            // 关菜单之前用户正在用的窗口，菜单结束后要还回去，
            // 否则焦点会留在我们那个屏幕外的宿主窗口上，用户接着打字会打空。
            IntPtr previous = GetForegroundWindow();

            // 托盘程序没有前台窗口，必须先把 owner 顶到前台，
            // 否则菜单点外面不会消失、ESC 也收不到（Windows 的既定行为）。
            bool attached = ForceForeground(owner);

            int command;
            try
            {
                command = TrackPopupMenuEx(_handle,
                    TPM_RETURNCMD | TPM_RIGHTBUTTON, screen.X, screen.Y, owner, IntPtr.Zero);
            }
            finally
            {
                // 挂接要等菜单真正结束后再解除，否则菜单会立刻失活并消失
                if (attached) DetachForeground();
            }

            PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (previous != IntPtr.Zero && previous != owner) SetForegroundWindow(previous);

            return command;
        }

        private static uint _attachedThread;

        /// <summary>
        /// 把窗口顶到前台。
        ///
        /// 系统有"前台锁定"：非前台进程直接 SetForegroundWindow 会被静默忽略，
        /// 菜单就成了"点不出去、按 ESC 也没反应"的悬空窗口。
        /// 标准做法是把自己的线程输入队列挂到当前前台线程上，再设前台；
        /// 挂接必须等菜单结束后再解除，否则菜单一出现就会失活。
        /// </summary>
        private static bool ForceForeground(IntPtr window)
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == window) return false;

            uint processId;
            uint foregroundThread = foreground == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foreground, out processId);
            uint currentThread = GetCurrentThreadId();

            bool attached = foregroundThread != 0
                && foregroundThread != currentThread
                && AttachThreadInput(currentThread, foregroundThread, true);

            _attachedThread = attached ? foregroundThread : 0;
            SetForegroundWindow(window);
            return attached;
        }

        private static void DetachForeground()
        {
            if (_attachedThread == 0) return;

            uint thread = _attachedThread;
            _attachedThread = 0;
            AttachThreadInput(GetCurrentThreadId(), thread, false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 子菜单由父菜单统一销毁，这里只销毁根句柄
            _children.Clear();
            if (_handle != IntPtr.Zero) DestroyMenu(_handle);
        }
    }
}
