using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AicNoMosaic
{
    /// <summary>
    /// 读写游戏自带的 _debug.txt 调试开关。
    /// 功能与上游 C++ debug_config.h 等价：只改目标键的数值字符，
    /// 注释、空行、缩进、未知键所在行全部逐字节原样保留。
    ///
    /// 实现为纯 C# 字符串处理（无正则、无 unsafe）：
    /// 文件按 Latin-1 解码 —— 单字节 0..255 与 char 1:1 对应，解码/编码无损，
    /// 任何字节序列（含 UTF-8 中文注释、任意二进制尾巴）都能原样 round-trip。
    /// 键定位规则（与上游一致）：
    ///   * 逐行扫描；行 = 键名 + 空白 + 数值（可负号）+（可选）行内 // 注释；
    ///   * 忽略空行与以 // 开头的整行注释；
    ///   * 键名必须整词匹配（右侧是空白或行尾），避免 mighty2 误命中 mighty；
    ///   * 数值必须干净（后面只能跟空白或 //），否则该行视为无值；
    ///   * 键值只能是一位数字 0..9 时才会被改写（同长替换，不移动字节）。
    /// </summary>
    internal static class DebugConfig
    {
        public const int KeyCount = 6;

        // 顺序与 UI 复选框一一对应
        public static readonly string[] KeyNames =
        {
            "<DEBUG>", "mighty", "nodamage", "weak", "allskill", "announce",
        };

        // 各键中文说明（给调试窗口显示）
        public static readonly string[] KeyTexts =
        {
            "开启调试（调试功能总开关）",
            "诺艾尔攻击力极高",
            "诺艾尔不受到伤害",
            "受到伤害立即晕厥",
            "解锁全部魔法技能",
            "启动游戏时显示报错",
        };

        public struct Entry
        {
            public int Value;
            public bool Valid;   // false = 文件里没有这个键（不参与写入）
        }

        // Latin-1（ISO-8859-1，codepage 28591）：单字节 0..255 与 char 一一对应，
        // 解码/编码无损 —— 任意字节序列都能原样 round-trip，这正是"逐字节保留"的关键。
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        /// <summary>读取六个键的值。文件缺失/占用返回 false；缺个别键也算成功（对应 Valid=false）。</summary>
        public static bool TryRead(string path, Entry[] entries)
        {
            if (entries == null || entries.Length != KeyCount) return false;
            for (int i = 0; i < KeyCount; i++)
            {
                entries[i].Value = 0;
                entries[i].Valid = false;
            }

            string text;
            try
            {
                if (!File.Exists(path)) return false;
                text = File.ReadAllText(path, Latin1);   // Latin-1，字节↔char 1:1
            }
            catch { return false; }

            for (int i = 0; i < KeyCount; i++)
            {
                string value;
                if (TryFindValue(text, KeyNames[i], out value) && value.Length == 1 && IsDigit(value[0]))
                {
                    entries[i].Value = value[0] - '0';
                    entries[i].Valid = true;
                }
            }
            return true;
        }

        /// <summary>
        /// 写六个键的值（只覆盖原有的单个数字字符；缺失键/多位数/负数/非数字值都跳过）。
        /// 没有任何实际改动时不写文件，保持 mtime 不变。
        /// </summary>
        public static bool TryWrite(string path, Entry[] entries)
        {
            if (entries == null || entries.Length != KeyCount) return false;

            string text;
            try
            {
                if (!File.Exists(path)) return false;
                text = File.ReadAllText(path, Latin1);
            }
            catch { return false; }

            // 收集全部 (值偏移 → 新字符) 改写点，之后按偏移一次拼装，避免边改边找
            var edits = new List<KeyValuePair<int, char>>(KeyCount);
            int wanted = 0;

            for (int k = 0; k < KeyCount; k++)
            {
                if (!entries[k].Valid) continue;
                wanted++;

                int value = entries[k].Value;
                if (value < 0) value = 0;
                if (value > 9) continue;                       // 只支持一位数字（本文件的值都是 0/1）

                int start;
                if (!TryFindValueStart(text, KeyNames[k], out start)) continue;

                string current = ReadNumericToken(text, start);
                if (current.Length != 1 || !IsDigit(current[0])) continue;   // 负数/多位 → 同长替换不成立

                char newValue = (char)('0' + value);
                if (text[start] == newValue) continue;         // 已是目标值

                edits.Add(new KeyValuePair<int, char>(start, newValue));
            }

            if (edits.Count == 0)
            {
                // 两种情况都算成功：
                //   * 所有目标键已是期望值（无差异 → 免写，保 mtime）
                //   * 调用方没有任何键要写（全 Valid=false）
                // 只有"有键要改但一个都没改成"（键缺失/值不可同长替换）才失败。
                return true;
            }

            // 按偏移升序（编辑天然按 k 升序且各键偏移互异）拼装新文本
            var sb = new StringBuilder(text.Length);
            int copied = 0;
            foreach (var edit in edits)
            {
                if (edit.Key > copied) sb.Append(text, copied, edit.Key - copied);
                sb.Append(edit.Value);
                copied = edit.Key + 1;                         // 跳过原数字字符
            }
            if (copied < text.Length) sb.Append(text, copied, text.Length - copied);

            // 原子写回：同目录临时文件 + File.Replace
            string tmp = path + ".tmp_aic_dbg";
            try
            {
                File.WriteAllText(tmp, sb.ToString(), Latin1);
                if (File.Exists(path)) File.Replace(tmp, path, null, true);
                else File.Move(tmp, path);
                return true;
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                return false;
            }
        }

        // ==================================================================
        //  行扫描核心（纯字符串处理，无正则）
        // ==================================================================

        /// <summary>找到 key 对应数值的首字符偏移；找不到返回 false。</summary>
        private static bool TryFindValueStart(string text, string key, out int valueStart)
        {
            valueStart = -1;
            if (string.IsNullOrEmpty(key)) return false;

            int n = text.Length;
            int lineStart = 0;

            while (lineStart <= n)
            {
                // 行范围：[lineStart, contentEnd)（不含 \r\n）
                int newline = lineStart >= n ? -1 : text.IndexOf('\n', lineStart);
                int contentEnd = newline < 0 ? n : newline;
                if (contentEnd > lineStart && text[contentEnd - 1] == '\r') contentEnd--;

                int p = SkipSpaces(text, lineStart, contentEnd);

                // 空行 / 整行注释：跳过
                if (p >= contentEnd || IsLineComment(text, p, contentEnd))
                {
                    if (AdvanceLine(lineStart, newline, n, out lineStart)) return false;
                    continue;
                }

                // 键名整词匹配 + 数值定位
                if (MatchesKey(text, p, contentEnd, key))
                {
                    int q = SkipSpaces(text, p + key.Length, contentEnd);

                    // "mighty   "（只有键名没有值）→ 该行视为无值
                    // 数值：[q] 可为负号，其后须有数字；数字后须紧跟 空白/行尾/行内注释
                    int value = q;
                    int afterNeg = text[q] == '-' ? q + 1 : q;
                    if (afterNeg < contentEnd && IsDigit(text[afterNeg]))
                    {
                        int afterNumber = afterNeg;
                        while (afterNumber < contentEnd && IsDigit(text[afterNumber])) afterNumber++;

                        bool clean = afterNumber >= contentEnd
                            || IsSpace(text[afterNumber])
                            || IsLineComment(text, afterNumber, contentEnd);

                        if (clean)
                        {
                            valueStart = value;
                            return true;
                        }
                        // 0x1F 之类 → 非纯值，继续扫后续行
                    }
                    // 缺值/坏值 → 继续扫后续行（文件里同键可能只出现一次，此规则与上游一致）
                }

                if (AdvanceLine(lineStart, newline, n, out lineStart)) return false;
            }
            return false;
        }

        /// <summary>查找 key 的第一个干净数值 token；找不到 value 为 null 且返回 false。</summary>
        private static bool TryFindValue(string text, string key, out string value)
        {
            value = null;
            int start;
            if (!TryFindValueStart(text, key, out start)) return false;
            value = ReadNumericToken(text, start);
            return true;
        }

        /// <summary>从 valueStart 读取连续数值 token（含首位负号）。</summary>
        private static string ReadNumericToken(string text, int start)
        {
            int end = text[start] == '-' ? start + 1 : start;
            while (end < text.Length && IsDigit(text[end])) end++;
            return text.Substring(start, end - start);
        }

        /// <summary>p 处是否为 "//"。</summary>
        private static bool IsLineComment(string text, int p, int contentEnd)
        {
            return p + 1 < contentEnd && text[p] == '/' && text[p + 1] == '/';
        }

        /// <summary>text[p..contentEnd) 是否以 key 开头，且 key 右侧是空白或行尾（整词匹配）。</summary>
        private static bool MatchesKey(string text, int p, int contentEnd, string key)
        {
            int keyLen = key.Length;
            if (contentEnd - p < keyLen) return false;
            for (int i = 0; i < keyLen; i++)
            {
                if (text[p + i] != key[i]) return false;
            }
            int after = p + keyLen;
            return after >= contentEnd || IsSpace(text[after]);
        }

        /// <summary>推进到下一行；已无后续行返回 true（调用方 return）。</summary>
        private static bool AdvanceLine(int lineStart, int newline, int total, out int next)
        {
            if (newline < 0 || newline >= total)
            {
                next = total;
                return true;    // 最后一行已处理
            }
            next = newline + 1;
            return false;
        }

        /// <summary>跳过空白并返回首个非空白位置（可能等于 end）。</summary>
        private static int SkipSpaces(string text, int start, int end)
        {
            while (start < end && IsSpace(text[start])) start++;
            return start;
        }

        private static bool IsSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\v' || c == '\f' || c == '\r';
        }

        private static bool IsDigit(char c) { return c >= '0' && c <= '9'; }
    }
}
