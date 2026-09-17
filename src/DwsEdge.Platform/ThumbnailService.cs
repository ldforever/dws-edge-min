using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DwsEdge.Platform
{
    /// <summary>
    /// B7 缩略图：给前端提供"按需取小图"的接口，原图仍然按需取。
    ///
    /// 为什么不引第三方图像库：现场是离线交付，拿不到 NuGet 包（System.Drawing.Common /
    /// ImageSharp 都取不到）。而采集侧写出来的原图是 **BMP**（大华 SDK 的原始图按 BMP 落盘），
    /// BMP 是无压缩位图 —— 读像素、按块平均、再写成 BMP，纯算术就能做，零依赖。
    ///
    /// JPEG 原图（SDK 直接给 JPEG 时）需要 JPEG 解码器才能缩小，离线环境做不到，
    /// 这时接口会**回退成直接返回原图**并在响应头里标注（前端照样能显示，只是没省流量）。
    /// 以后有包源了，接上 System.Drawing.Common / ImageSharp 就能覆盖 JPEG。
    ///
    /// 生成的缩略图缓存在 runtime\cache\thumbs\ 下（按 路径+修改时间+宽度 做键），
    /// 同一张图只会算一次；所有工作都在平台进程里做，**完全不占用采集进程**。
    /// </summary>
    public sealed class ThumbnailService
    {
        private readonly string _cacheDir;
        private readonly Action<string, bool> _log;

        public ThumbnailService(string cacheDir, Action<string, bool> log)
        {
            _cacheDir = cacheDir;
            _log = log;
            Directory.CreateDirectory(_cacheDir);
        }

        public sealed class ThumbResult
        {
            public byte[] bytes;
            public string format;       // bmp / original
            public bool fallback;       // true = 没能缩小，返回的是原图
            public int width;
            public int height;
        }

        /// <summary>取缩略图（BMP 会真的缩小并缓存；其他格式回退原图）。</summary>
        public ThumbResult Get(string sourcePath, int targetWidth)
        {
            if (targetWidth < 16) { targetWidth = 16; }
            if (targetWidth > 2000) { targetWidth = 2000; }

            string ext = Path.GetExtension(sourcePath) ?? string.Empty;
            if (!ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
            {
                return Fallback(sourcePath);
            }

            string cached = CachePath(sourcePath, targetWidth);
            try
            {
                if (File.Exists(cached))
                {
                    byte[] hit = File.ReadAllBytes(cached);
                    Size size = ReadSize(hit);
                    return new ThumbResult { bytes = hit, format = "bmp", width = size.w, height = size.h };
                }

                byte[] source = File.ReadAllBytes(sourcePath);
                ThumbResult result = Scale(source, targetWidth);
                if (result == null)
                {
                    return Fallback(sourcePath);
                }

                // 先写临时文件再改名，避免并发请求读到半个文件
                string tmp = cached + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                File.WriteAllBytes(tmp, result.bytes);
                if (File.Exists(cached))
                {
                    File.Delete(tmp);
                }
                else
                {
                    File.Move(tmp, cached);
                }
                return result;
            }
            catch (Exception ex)
            {
                if (_log != null) { _log("生成缩略图失败（回退原图）：" + ex.Message, true); }
                return Fallback(sourcePath);
            }
        }

        private ThumbResult Fallback(string sourcePath)
        {
            byte[] bytes = File.ReadAllBytes(sourcePath);
            Size size = ReadSize(bytes);
            return new ThumbResult
            {
                bytes = bytes,
                format = "original",
                fallback = true,
                width = size.w,
                height = size.h
            };
        }

        /// <summary>缓存键：路径 + 修改时间 + 目标宽度（原图更新了自动失效）。</summary>
        private string CachePath(string sourcePath, int width)
        {
            DateTime stamp = File.GetLastWriteTimeUtc(sourcePath);
            string key = sourcePath.ToLowerInvariant() + "|" + stamp.Ticks + "|" + width;
            byte[] hash = MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(key));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 8; i++) { sb.Append(hash[i].ToString("x2")); }
            return Path.Combine(_cacheDir, sb.ToString() + "-w" + width + ".bmp");
        }

        #region 纯 C# BMP 读 / 降采样 / 写

        private sealed class Size { public int w; public int h; }

        private static Size ReadSize(byte[] data)
        {
            Size size = new Size();
            if (data.Length >= 26 && data[0] == 'B' && data[1] == 'M')
            {
                size.w = BitConverter.ToInt32(data, 18);
                size.h = Math.Abs(BitConverter.ToInt32(data, 22));
            }
            return size;
        }

        /// <summary>把 BMP 按块平均缩小成新的 24 位 BMP；不支持的格式返回 null（调用方回退）。</summary>
        private ThumbResult Scale(byte[] data, int targetWidth)
        {
            if (data.Length < 54 || data[0] != 'B' || data[1] != 'M')
            {
                return null;
            }

            int dataOffset = BitConverter.ToInt32(data, 10);
            int width = BitConverter.ToInt32(data, 18);
            int heightRaw = BitConverter.ToInt32(data, 22);
            int planes = BitConverter.ToInt16(data, 26);
            int bpp = BitConverter.ToInt16(data, 28);
            int compression = BitConverter.ToInt32(data, 30);

            if (width <= 0 || heightRaw == 0 || planes != 1 || compression != 0)
            {
                return null;   // 只处理无压缩的行式位图
            }
            if (bpp != 24 && bpp != 32)
            {
                return null;   // 常见的是 24/32 位；其他（1/4/8 位调色板）交给回退
            }

            bool topDown = heightRaw < 0;
            int height = Math.Abs(heightRaw);
            int srcStride = ((bpp * width + 31) / 32) * 4;
            if (dataOffset + (long)srcStride * height > data.Length)
            {
                return null;
            }

            int outWidth = Math.Min(targetWidth, width);
            int outHeight = Math.Max(1, (int)Math.Round((double)height * outWidth / width));
            int outStride = ((24 * outWidth + 31) / 32) * 4;
            byte[] outData = new byte[54 + outStride * outHeight];

            double scaleX = (double)width / outWidth;
            double scaleY = (double)height / outHeight;

            for (int y = 0; y < outHeight; y++)
            {
                int sy0 = (int)(y * scaleY);
                int sy1 = Math.Min(height, Math.Max(sy0 + 1, (int)((y + 1) * scaleY)));
                int rowOut = 54 + (outHeight - 1 - y) * outStride;   // BMP 默认自下而上

                for (int x = 0; x < outWidth; x++)
                {
                    int sx0 = (int)(x * scaleX);
                    int sx1 = Math.Min(width, Math.Max(sx0 + 1, (int)((x + 1) * scaleX)));

                    long b = 0, g = 0, r = 0;
                    int count = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        int srcRow = dataOffset + (topDown ? sy : (height - 1 - sy)) * srcStride;
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            int p = srcRow + sx * (bpp / 8);
                            b += data[p];
                            g += data[p + 1];
                            r += data[p + 2];
                            count++;
                        }
                    }

                    if (count == 0) { count = 1; }
                    int o = rowOut + x * 3;
                    outData[o] = (byte)(b / count);
                    outData[o + 1] = (byte)(g / count);
                    outData[o + 2] = (byte)(r / count);
                }
            }

            WriteHeader(outData, outWidth, outHeight, outStride);
            return new ThumbResult { bytes = outData, format = "bmp", width = outWidth, height = outHeight };
        }

        private static void WriteHeader(byte[] buffer, int width, int height, int stride)
        {
            int fileSize = buffer.Length;
            buffer[0] = (byte)'B';
            buffer[1] = (byte)'M';
            BitConverter.GetBytes(fileSize).CopyTo(buffer, 2);
            BitConverter.GetBytes(54).CopyTo(buffer, 10);          // 像素数据偏移
            BitConverter.GetBytes(40).CopyTo(buffer, 14);          // BITMAPINFOHEADER 大小
            BitConverter.GetBytes(width).CopyTo(buffer, 18);
            BitConverter.GetBytes(height).CopyTo(buffer, 22);
            BitConverter.GetBytes((short)1).CopyTo(buffer, 26);     // planes
            BitConverter.GetBytes((short)24).CopyTo(buffer, 28);    // 24 位
            BitConverter.GetBytes(0).CopyTo(buffer, 30);            // 无压缩
            BitConverter.GetBytes(stride * height).CopyTo(buffer, 34);
            BitConverter.GetBytes(2835).CopyTo(buffer, 38);         // 72 DPI
            BitConverter.GetBytes(2835).CopyTo(buffer, 42);
        }

        #endregion
    }
}
