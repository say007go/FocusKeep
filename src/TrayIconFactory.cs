using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FocusKeep
{
    /// <summary>
    /// 托盘图标：四张 256px 设计稿编译进 exe（内嵌资源），运行时按托盘实际尺寸缩放。
    ///
    /// 槽位 = (屏幕常亮 ? 1 : 0) | (键盘锁定 ? 2 : 0)：
    ///   0 灰色（已关闭）  1 蓝绿（常亮中）  2 灰色+角标  3 蓝绿+角标
    /// </summary>
    internal static class TrayIconFactory
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static readonly string[] ResourceNames = new string[]
        {
            "FocusKeep.res.icon-off.png",
            "FocusKeep.res.icon-on.png",
            "FocusKeep.res.icon-off-lock.png",
            "FocusKeep.res.icon-on-lock.png"
        };

        private static readonly Image[] Cache = new Image[4];

        public static Icon Create(bool screenOn, bool locked)
        {
            // 至少按 64px 出图：Win11 托盘在 200% 缩放下用的位置比 SmallIconSize 大，
            // 只给 32px 会被原样画小一圈。给大图让外壳自己缩，反而更清晰。
            int size = 64;
            try
            {
                int small = SystemInformation.SmallIconSize.Width;
                if (small > size && small <= 256) size = small;
            }
            catch (Exception) { }

            using (Bitmap bitmap = Render(screenOn, locked, size))
            {
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        /// <summary>给自检/预览用：直接拿到某个尺寸的位图。</summary>
        internal static Bitmap RenderPreview(bool screenOn, bool locked, int size)
        {
            return Render(screenOn, locked, size);
        }

        private static Bitmap Render(bool screenOn, bool locked, int size)
        {
            Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);

                Image art = Load((screenOn ? 1 : 0) | (locked ? 2 : 0));
                if (art != null) g.DrawImage(art, new Rectangle(0, 0, size, size));
                else DrawFallback(g, size, screenOn, locked);
            }

            return bitmap;
        }

        /// <summary>内嵌资源只解码一次，之后一直复用。</summary>
        private static Image Load(int slot)
        {
            if (Cache[slot] != null) return Cache[slot];

            try
            {
                Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceNames[slot]);
                if (stream == null) return null;

                using (stream)
                {
                    // 复制一份，避免 Image 依赖已经关闭的流
                    using (Image original = Image.FromStream(stream))
                    {
                        Cache[slot] = new Bitmap(original);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("加载内置图标失败：" + ex.Message);
            }

            return Cache[slot];
        }

        /// <summary>资源缺失时的兜底：一个纯色球 + 简单图形，保证托盘不会是空白。</summary>
        private static void DrawFallback(Graphics g, int size, bool screenOn, bool locked)
        {
            float k = size / 32f;
            Color back = screenOn ? Color.FromArgb(45, 212, 191) : Color.FromArgb(148, 163, 184);

            using (SolidBrush brush = new SolidBrush(back))
            {
                g.FillEllipse(brush, 0.5f * k, 0.5f * k, size - 1f * k, size - 1f * k);
            }

            using (SolidBrush white = new SolidBrush(Color.White))
            using (GraphicsPath screen = RoundedRect(new RectangleF(8.5f * k, 8.6f * k, 15f * k, 11f * k), 2.8f * k))
            {
                g.FillPath(white, screen);
                g.FillRectangle(white, 14.6f * k, 19.5f * k, 2.8f * k, 2.5f * k);
                g.FillRectangle(white, 10.9f * k, 21.8f * k, 10.2f * k, 2.2f * k);
            }

            if (!locked) return;

            using (SolidBrush red = new SolidBrush(Color.FromArgb(220, 60, 50)))
            {
                g.FillEllipse(red, size - 14f * k, size - 14f * k, 13f * k, 13f * k);
            }
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));

            path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }
}
