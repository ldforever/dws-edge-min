using System.Collections.Generic;

namespace DwsEdge.Core.Model
{
    /// <summary>
    /// 图片上要画的一个框（大华 SDK 给的条码位置；将来别的厂商给别的框也用这个模型）。
    ///
    /// 坐标一律用 **0~1 的归一化值**（左上角为原点）：前端按显示尺寸换算即可。
    /// 不存像素坐标的原因：同一张图会被原图、缩略图、不同分辨率的界面反复缩放，
    /// 归一化之后"图怎么缩放框都不会画偏"，也不用关心 BMP/JPG 的实际宽高。
    /// </summary>
    public sealed class ImageBox
    {
        /// <summary>框里那个条码值；无码 / 未知时为空。</summary>
        public string Code;

        /// <summary>
        /// 多边形顶点，按顺序连线（首尾自动闭合）。
        /// 大华给的是 5 个点、其中首尾重复，这里原样保留，前端直接画折线即可。
        /// </summary>
        public List<ImageBoxPoint> Points = new List<ImageBoxPoint>();
    }

    /// <summary>框上的一个点（归一化坐标，0~1）。</summary>
    public struct ImageBoxPoint
    {
        public double X;
        public double Y;

        public ImageBoxPoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }
}
