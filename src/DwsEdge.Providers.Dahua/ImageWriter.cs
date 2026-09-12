using System;
using System.IO;
using DwsEdge.Core.Model;

namespace DwsEdge.Providers.Dahua
{
    /// <summary>
    /// 把 CapturedImage 落盘。
    ///
    /// 刻意不依赖 System.Drawing / TurboJpeg，保证最小模型"零额外依赖"：
    ///   - 相机直接出 JPEG（cfg 里 outImgType="1"）→ 原样写文件，最快、最省 CPU；
    ///   - 原始灰度 / 24bpp BGR → 自己写 BMP，够调试用。
    ///
    /// 生产环境建议换成 SDK 自带的 TurboJpegWrapper 或 SkiaSharp：
    /// 压缩率更高（14 张图的场景能省一半以上磁盘和上行带宽）。
    /// </summary>
    internal static class ImageWriter
    {
        public static ImageRef Write(CapturedImage image, bool isJpeg, int channels, string directory, string fileNameWithoutExtension, ImageKind kind, string deviceId)
        {
            if (image == null || image.Data == IntPtr.Zero || image.DataSize <= 0)
            {
                return null;
            }

            Directory.CreateDirectory(directory);

            ImageRef result = new ImageRef();
            result.Kind = kind;
            result.DeviceId = deviceId;
            result.Width = image.Width;
            result.Height = image.Height;

            if (isJpeg)
            {
                result.Path = Path.Combine(directory, fileNameWithoutExtension + ".jpg");
                result.Format = "jpg";
                File.WriteAllBytes(result.Path, image.ToBytes());
            }
            else
            {
                result.Path = Path.Combine(directory, fileNameWithoutExtension + ".bmp");
                result.Format = "bmp";
                WriteBmp(image, channels, result.Path);
            }

            FileInfo fi = new FileInfo(result.Path);
            result.Bytes = (int)fi.Length;
            return result;
        }

        private static void WriteBmp(CapturedImage image, int channels, string path)
        {
            int rowBytes = image.Width * channels;
            int padding = (4 - (rowBytes % 4)) % 4;
            int stride = rowBytes + padding;
            int paletteBytes = channels == 1 ? 256 * 4 : 0;
            int pixelOffset = 14 + 40 + paletteBytes;
            int pixelBytes = stride * image.Height;

            byte[] data = image.ToBytes();

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                // BITMAPFILEHEADER
                w.Write((byte)'B');
                w.Write((byte)'M');
                w.Write(14 + 40 + paletteBytes + pixelBytes);
                w.Write((short)0);
                w.Write((short)0);
                w.Write(pixelOffset);

                // BITMAPINFOHEADER
                w.Write(40);
                w.Write(image.Width);
                w.Write(image.Height);
                w.Write((short)1);
                w.Write((short)(channels == 1 ? 8 : 24));
                w.Write(0);
                w.Write(pixelBytes);
                w.Write(2835);   // 水平分辨率（像素/米，约 72 DPI）
                w.Write(2835);
                w.Write(channels == 1 ? 256 : 0);
                w.Write(0);

                if (channels == 1)
                {
                    // 灰度调色板
                    for (int i = 0; i < 256; i++)
                    {
                        w.Write((byte)i);
                        w.Write((byte)i);
                        w.Write((byte)i);
                        w.Write((byte)0);
                    }
                }

                // 像素数据：BMP 默认自下而上存储，且每行按 4 字节对齐
                byte[] row = new byte[stride];
                for (int y = image.Height - 1; y >= 0; y--)
                {
                    Buffer.BlockCopy(data, y * rowBytes, row, 0, rowBytes);
                    for (int p = rowBytes; p < stride; p++)
                    {
                        row[p] = 0;
                    }
                    w.Write(row);
                }
            }
        }
    }
}
