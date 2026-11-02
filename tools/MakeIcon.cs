using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// 把 256x256 的 PNG 生成多尺寸 .ico（exe 的应用图标用）。
// 用法：make-ico.exe <源png> <目标ico>
internal static class MakeIcon
{
    private static readonly int[] Sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    private static void Main(string[] args)
    {
        string source = args.Length > 0 ? args[0] : "src\\res\\icon-on.png";
        string target = args.Length > 1 ? args[1] : "src\\app.ico";

        using (Image image = Image.FromFile(source))
        using (MemoryStream output = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(output))
        {
            writer.Write((ushort)0);              // reserved
            writer.Write((ushort)1);              // type = icon
            writer.Write((ushort)Sizes.Length);   // count

            long directory = output.Position;
            for (int i = 0; i < Sizes.Length; i++) writer.Write(new byte[16]);

            for (int i = 0; i < Sizes.Length; i++)
            {
                byte[] frame = Encode(image, Sizes[i], Sizes[i] >= 64);
                long offset = output.Position;
                writer.Write(frame);

                long tail = output.Position;
                output.Position = directory + i * 16;
                byte dim = (byte)(Sizes[i] >= 256 ? 0 : Sizes[i]);
                writer.Write(dim);
                writer.Write(dim);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)frame.Length);
                writer.Write((uint)offset);
                output.Position = tail;
            }

            File.WriteAllBytes(target, output.ToArray());
        }

        Console.WriteLine("ico -> " + target);
    }

    private static byte[] Encode(Image source, int size, bool png)
    {
        using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(source, new Rectangle(0, 0, size, size));
            }

            if (png)
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }

            return ToDib(bitmap);
        }
    }

    /// <summary>32 位 BGRA 的 DIB（小尺寸用，兼容性最好）。</summary>
    private static byte[] ToDib(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        byte[] pixels = new byte[width * height * 4];
        try
        {
            for (int y = 0; y < height; y++)
            {
                IntPtr row = new IntPtr(data.Scan0.ToInt64() + y * data.Stride);
                Marshal.Copy(row, pixels, (height - 1 - y) * width * 4, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        int maskStride = ((width + 31) / 32) * 4;

        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write(40);
            writer.Write(width);
            writer.Write(height * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(width * height * 4);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(pixels);
            writer.Write(new byte[maskStride * height]);
            writer.Flush();
            return stream.ToArray();
        }
    }
}
