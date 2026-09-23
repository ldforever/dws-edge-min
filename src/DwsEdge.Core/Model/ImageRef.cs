using System;

using System.Collections.Generic;

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

        /// <summary>
        /// 这张图上要画的框（绿框）。坐标是归一化的，见 ImageBox 的说明。
        /// 没有框就是空列表 —— 无码（noread）的包裹照样有图，只是没有框。
        /// </summary>
        public List<ImageBox> Boxes = new List<ImageBox>();

        public override string ToString()
        {
            return Kind + ":" + Path;
        }
    }
}
