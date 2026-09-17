using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DwsEdge.Core.Model
{
    /// <summary>
    /// 统一追踪号规则（所有 provider 共用，不要再各自实现）：
    ///
    ///     providerId|deviceId|capturedAtMs|code1,code2,...
    ///
    /// 同一个包裹的多次回调（例如大华先回调条码、再回调条码+重量体积）必须得到相同的追踪号，
    /// 业务平台靠它把多次回调合并成一条包裹记录。
    /// </summary>
    public static class ParcelTrace
    {
        private const string FallbackPrefix = "fallback|";

        public static string Build(string providerId, string deviceId, long capturedAtMs, IList<CodeItem> codes)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(providerId ?? string.Empty).Append('|')
              .Append(deviceId ?? string.Empty).Append('|')
              .Append(capturedAtMs.ToString(CultureInfo.InvariantCulture)).Append('|');

            if (codes != null)
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    CodeItem code = codes[i];
                    if (code == null || string.IsNullOrEmpty(code.Value))
                    {
                        continue;
                    }
                    if (sb[sb.Length - 1] != '|')
                    {
                        sb.Append(',');
                    }
                    sb.Append(code.Value);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 兜底键：事件没带追踪号时使用。可追溯性较弱（只有相机+时间戳），平台会告警并计数。
        /// </summary>
        public static string BuildFallback(string deviceId, long capturedAtMs)
        {
            return FallbackPrefix + (deviceId ?? string.Empty) + "|" + capturedAtMs.ToString(CultureInfo.InvariantCulture);
        }

        public static bool IsFallback(string traceId)
        {
            return !string.IsNullOrEmpty(traceId) && traceId.StartsWith(FallbackPrefix, StringComparison.Ordinal);
        }
    }
}
