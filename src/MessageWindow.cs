using System;
using System.Windows.Forms;

namespace FocusKeep
{
    /// <summary>一个不可见的原生窗口，只用来接收 WM_HOTKEY。</summary>
    internal sealed class MessageWindow : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;

        public event EventHandler HotkeyPressed;

        public MessageWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "FocusKeep.HotkeySink";
            cp.Style = 0;
            cp.ExStyle = 0;
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                EventHandler handler = HotkeyPressed;
                if (handler != null) handler(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
