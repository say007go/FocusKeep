using System;
using System.Drawing;
using System.Windows.Forms;

namespace FocusKeep
{
    /// <summary>
    /// 屏幕外的一个 1×1 宿主窗口。
    ///
    /// 托盘程序没有可见主窗口，而 Win32 弹出菜单要求"拥有者窗口"处于前台，
    /// 否则点菜单外面它不会消失。这个窗口平时看不见、不进 Alt+Tab、不抢焦点，
    /// 只用来在弹菜单时临时拿到前台身份。
    /// </summary>
    internal sealed class HostWindow : Form
    {
        public HostWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-32000, -32000, 1, 1);
            Text = "FocusKeep.Host";
        }

        /// <summary>显示时不激活，避免启动瞬间把用户的窗口顶掉。</summary>
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab
                return cp;
            }
        }
    }
}
