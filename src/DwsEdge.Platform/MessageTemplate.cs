using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DwsEdge.Platform
{
    /// <summary>
    /// 下游报文模板（B4）。
    ///
    /// 语法：字段名写在花括号里，其余字符原样输出；支持转义 \r \n \t \\，
    /// 所以一行模板里就能写分隔符和换行。例：
    ///
    ///     {code}|{time}|{camera}|{weight}|{volume}|{traceId}\r\n
    ///
    /// 写错的字段名不会被静默丢掉：渲染时原样保留，Validate() 会报出来
    /// （界面"模板预览"会提示），避免现场以为发出去了其实字段是空的。
    /// </summary>
    public static class MessageTemplate
    {
        /// <summary>可用字段（界面提示用）。</summary>
        public static readonly string[] Fields =
        {
            "traceId", "code", "codes", "codeCount", "noread",
            "time", "timestamp", "camera", "deviceId",
            "weight", "length", "width", "height", "volume",
            "image", "imageCount", "position",
            "dispatchAttempts", "head"      // head = 报文序号占位（测试报文体面一点）
        };

        /// <summary>用一条包裹记录渲染报文；record 为空时只做转义还原（预览无数据时用）。</summary>
        public static string Render(string template, ParcelRecord record)
        {
            if (string.IsNullOrEmpty(template))
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];

                if (c == '\\' && i + 1 < template.Length)
                {
                    sb.Append(UnescapeChar(template[i + 1]));
                    i += 2;
                    continue;
                }

                if (c == '{')
                {
                    int end = template.IndexOf('}', i + 1);
                    if (end > i)
                    {
                        string name = template.Substring(i + 1, end - i - 1).Trim();
                        string value = record == null ? null : Value(name, record);
                        if (value == null)
                        {
                            // 未知字段或没有数据：原样保留，预览里一眼能看出来
                            sb.Append(template, i, end - i + 1);
                        }
                        else
                        {
                            sb.Append(value);
                        }
                        i = end + 1;
                        continue;
                    }
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>校验模板：返回问题列表（空=没问题）。</summary>
        public static List<string> Validate(string template)
        {
            List<string> problems = new List<string>();
            if (string.IsNullOrEmpty(template))
            {
                problems.Add("模板不能为空");
                return problems;
            }

            HashSet<string> known = new HashSet<string>(Fields, StringComparer.OrdinalIgnoreCase);
            int i = 0;
            while (i < template.Length)
            {
                if (template[i] == '\\' && i + 1 < template.Length)
                {
                    i += 2;
                    continue;
                }

                if (template[i] == '{')
                {
                    int end = template.IndexOf('}', i + 1);
                    if (end < 0)
                    {
                        problems.Add("有没闭合的 {");
                        break;
                    }

                    string name = template.Substring(i + 1, end - i - 1).Trim();
                    if (!known.Contains(name))
                    {
                        problems.Add("未知字段：{" + name + "}");
                    }
                    i = end + 1;
                    continue;
                }

                if (template[i] == '}')
                {
                    problems.Add("有多余的 }");
                }
                i++;
            }

            return problems;
        }

        /// <summary>把模板里的转义还原。</summary>
        public static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    sb.Append(UnescapeChar(text[i + 1]));
                    i++;
                    continue;
                }
                sb.Append(text[i]);
            }
            return sb.ToString();
        }

        private static char UnescapeChar(char c)
        {
            switch (c)
            {
                case 'r': return '\r';
                case 'n': return '\n';
                case 't': return '\t';
                case '\\': return '\\';
                default: return c;
            }
        }

        /// <summary>取字段值；返回 null 表示"不是已知字段"。</summary>
        private static string Value(string name, ParcelRecord record)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            switch (name.ToLowerInvariant())
            {
                case "traceid":
                    return record.traceId ?? string.Empty;
                case "code":
                    return FirstCode(record);
                case "codes":
                    return record.codes == null ? string.Empty : string.Join(",", record.codes.ToArray());
                case "codecount":
                    return record.codeCount.ToString(CultureInfo.InvariantCulture);
                case "noread":
                    return record.codeCount == 0 ? "1" : "0";
                case "time":
                    return record.time ?? string.Empty;
                case "timestamp":
                    return record.capturedAtMs.ToString(CultureInfo.InvariantCulture);
                case "camera":
                case "deviceid":
                    return record.deviceId ?? string.Empty;
                case "weight":
                    return record.weightGrams > 0
                        ? record.weightGrams.ToString(CultureInfo.InvariantCulture)
                        : string.Empty;
                case "length":
                    return Num(record.lengthMm);
                case "width":
                    return Num(record.widthMm);
                case "height":
                    return Num(record.heightMm);
                case "volume":
                    return Num(record.volumeMm3);
                case "image":
                    return record.firstImagePath ?? string.Empty;
                case "imagecount":
                    return record.imageCount.ToString(CultureInfo.InvariantCulture);
                case "position":
                    return FirstPosition(record);
                case "dispatchattempts":
                    return record.dispatchAttempts.ToString(CultureInfo.InvariantCulture);
                case "head":
                    return string.Empty;
                default:
                    return null;
            }
        }

        private static string FirstCode(ParcelRecord record)
        {
            if (record.codes != null && record.codes.Count > 0)
            {
                return record.codes[0] ?? string.Empty;
            }
            return string.Empty;
        }

        private static string FirstPosition(ParcelRecord record)
        {
            if (record.codeDetails != null)
            {
                for (int i = 0; i < record.codeDetails.Count; i++)
                {
                    if (!string.IsNullOrEmpty(record.codeDetails[i].position))
                    {
                        return record.codeDetails[i].position;
                    }
                }
            }
            return string.Empty;
        }

        private static string Num(double value)
        {
            return value > 0 ? value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
        }
    }
}
