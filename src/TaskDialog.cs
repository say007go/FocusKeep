using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FocusKeep
{
    /// <summary>
    /// 系统原生 TaskDialog（Windows Vista 起提供，Windows 11 上是新版样式）。
    /// 主标题 + 正文 + 可展开的详细说明；正文是纯文本（链接放到菜单里点）。
    /// </summary>
    internal static class TaskDialog
    {
        private const uint TDF_ALLOW_DIALOG_CANCELLATION = 0x0008;
        private const uint TDF_SIZE_TO_CONTENT = 0x01000000;

        private const uint TDCBF_OK_BUTTON = 0x0001;

        // MAKEINTRESOURCE(-3) = 信息图标
        private static readonly IntPtr InformationIcon = new IntPtr(0xFFFD);

        // 注意：这个结构体在 Windows SDK 里是 1 字节对齐的（pbSize = 160）。
        // 用默认的 8 字节对齐会变成 176，TaskDialogIndirect 直接返回 E_INVALIDARG。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        private struct TASKDIALOGCONFIG
        {
            public uint cbSize;
            public IntPtr hwndParent;
            public IntPtr hInstance;
            public uint dwFlags;
            public uint dwCommonButtons;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszWindowTitle;
            public IntPtr hMainIcon;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszMainInstruction;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszContent;
            public uint cButtons;
            public IntPtr pButtons;
            public int nDefaultButton;
            public uint cRadioButtons;
            public IntPtr pRadioButtons;
            public int nDefaultRadioButton;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszVerificationText;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszExpandedInformation;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszExpandedControlText;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszCollapsedControlText;
            public IntPtr hFooterIcon;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszFooter;
            public IntPtr pfCallback;
            public IntPtr lpCallbackData;
            public uint cxWidth;
        }

        [DllImport("comctl32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int TaskDialogIndirect(ref TASKDIALOGCONFIG config,
            out int button, out int radioButton, out bool verification);

        /// <summary>弹一个原生 TaskDialog；不可用时回落到 MessageBox。</summary>
        public static void Show(IntPtr owner, string title, string mainInstruction,
            string content, string expandedText, string footer)
        {
            try
            {
                TASKDIALOGCONFIG config = new TASKDIALOGCONFIG();
                config.cbSize = (uint)Marshal.SizeOf(typeof(TASKDIALOGCONFIG));
                config.hwndParent = owner;
                config.dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_SIZE_TO_CONTENT;
                config.dwCommonButtons = TDCBF_OK_BUTTON;
                config.pszWindowTitle = title;
                config.hMainIcon = InformationIcon;
                config.pszMainInstruction = mainInstruction;
                config.pszContent = content;
                config.pszExpandedInformation = expandedText;
                // 注意：这两个字符串按"当前状态"取名——
                // 折叠时显示 CollapsedControlText，展开后显示 ExpandedControlText。
                config.pszExpandedControlText = "收起说明";
                config.pszCollapsedControlText = "展开说明";
                config.pszFooter = footer;

                int button, radio;
                bool verification;
                int result = TaskDialogIndirect(ref config, out button, out radio, out verification);
                if (result < 0)
                {
                    Log.Write("[TaskDialog] 调用失败 HRESULT=0x" + result.ToString("X8"));
                    throw new InvalidOperationException("TaskDialogIndirect 0x" + result.ToString("X8"));
                }
            }
            catch (Exception ex)
            {
                Log.Write("[TaskDialog] 回落到 MessageBox：" + ex.Message);
                MessageBox.Show(content + "\r\n\r\n" + expandedText + "\r\n\r\n" + footer,
                    title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>用默认浏览器打开网址。</summary>
        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("打开链接失败：" + ex.Message); }
        }
    }
}
