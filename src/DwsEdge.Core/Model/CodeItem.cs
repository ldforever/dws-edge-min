using System;

namespace DwsEdge.Core.Model
{
    /// <summary>
    /// 条码类型（厂商无关）。大华的 1D/2D 标记在这里被归一化。
    /// </summary>
    public enum CodeKind
    {
        Unknown = 0,
        OneD = 1,
        TwoD = 2
    }

    /// <summary>
    /// 一条条码结果。
    /// </summary>
    public sealed class CodeItem
    {
        public string Value;
        public CodeKind Kind;

        /// <summary>厂商给出的方位标记（大华可能是 fr/ba 之类），没有则为 null。</summary>
        public string Position;

        public CodeItem()
        {
        }

        public CodeItem(string value, CodeKind kind, string position)
        {
            Value = value;
            Kind = kind;
            Position = position;
        }

        public override string ToString()
        {
            if (string.IsNullOrEmpty(Position))
            {
                return Value;
            }
            return Position + ":" + Value;
        }
    }
}
