using System;

namespace DwsEdge.Core.Model
{
    /// <summary>
    /// 图片种类。
    /// </summary>
    public enum ImageKind
    {
        Original = 1,   // 包裹原图
        Waybill = 2,    // 面单抠图
        PerCamera = 3,  // 单相机图
        Panorama = 4    // 全景 / 拼接图
    }

    /// <summary>
    /// 图片引用：只带路径和元信息，绝不带图像字节。
    /// 这是"图像不出采集宿主"这条规则的落点，也是将来拆设备/跨机的前提。
    /// </summary>
    public sealed class ImageRef
    {
        public ImageKind Kind;

        /// <summary>相机标识（大华回调里的 CameraID，通常是 IP 或图片路径）。</summary>
        public string DeviceId;

        /// <summary>已落盘的绝对路径。</summary>
        public string Path;

        /// <summary>jpg / bmp。</summary>
        public string Format;

        public int Width;
        public int Height;
        public int Bytes;

        public override string ToString()
        {
            return Kind + ":" + Path;
        }
    }
}
