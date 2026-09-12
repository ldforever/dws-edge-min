using System;
using System.Runtime.InteropServices;
using LogisticsBaseCSharp;

namespace DwsEdge.Providers.Dahua
{
    /// <summary>
    /// SDK 回调给的图是"非托管内存 + 宽高 + 类型"。底层回调返回后，原始内存就会被释放，
    /// 所以必须在回调线程里立刻深拷贝一份（Clone），再由后台线程慢慢落盘。
    ///
    /// 这一层是"回调零业务"规则的落点：
    ///   - 回调线程：Clone + 入队（微秒级）
    ///   - 工作线程：写文件（毫秒级，可能很慢，但绝不会挡住 SDK）
    /// </summary>
    internal sealed class CapturedImage : IDisposable
    {
        public int Width;
        public int Height;

        /// <summary>
        /// LogisticsAPIStruct.EImageType：0=eImageTypeNormal（灰度），
        /// 1=eImageTypeJpeg（JPEG 压缩流），2=eImageTypeBGR（24bpp BGR）。
        /// </summary>
        public int Type;

        public int DataSize;
        public IntPtr Data;
        public uint Index;

        private bool _disposed;

        /// <summary>从 SDK 的 VslbImage 深拷贝一份（内存为 HGlobal，由本类负责释放）。</summary>
        public static CapturedImage From(LogisticsAPIStruct.VslbImage image)
        {
            if (image.ImageData == IntPtr.Zero || image.dataSize <= 0)
            {
                return null;
            }

            LogisticsAPIStruct.VslbImage copy = image.Clone();

            CapturedImage result = new CapturedImage();
            result.Width = copy.width;
            result.Height = copy.height;
            result.Type = copy.type;
            result.DataSize = copy.dataSize;
            result.Data = copy.ImageData;
            result.Index = image.img_idx;
            return result;
        }

        public byte[] ToBytes()
        {
            if (Data == IntPtr.Zero || DataSize <= 0)
            {
                return null;
            }
            byte[] bytes = new byte[DataSize];
            Marshal.Copy(Data, bytes, 0, DataSize);
            return bytes;
        }

        ~CapturedImage()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Data);
                Data = IntPtr.Zero;
            }

            _disposed = true;
        }
    }
}
