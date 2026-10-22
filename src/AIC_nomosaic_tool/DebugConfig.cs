using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AicNoMosaic
{
    /// <summary>
    /// 读写游戏自带的 _debug.txt 调试开关。
    ///
    /// 文件格式约定（实测 ver0.22f+ 均如此）：
    ///   * UTF-8 无 BOM
    ///   * Unix LF 换行
    ///   * 行 = 键名 + 空白 + 数值 +（可选）行内或下方 // 注释
    ///
    /// 行为（游戏解析器只读 "键 数值" 行首 token，其余文本纯属给人看的文档）：
    ///   * 读：逐行整词匹配键名取数值；未知键（nosnd/_player/benchmark…）忽略；
    ///     注释下一行形式（"mighty 0\n// Enter 1 to..."）不影响读取；
    ///   * 写：按整行重建目标键 —— 改写为规范行 "&lt;键名&gt; 0/1"；未知键/注释/空行
    ///     原样保留在原位置；文件里不存在的键追加到末尾。
    ///     输出统一 UTF-8 无 BOM + LF；无实际改动时不写文件（保 mtime）。
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

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>读取六个键的值。文件缺失/占用返回 false；缺个别键也算成功（对应 Valid=false）。</summary>
        public static bool TryRead(string path, Entry[] entries)
        {
            if (entries == null || entries.Length != KeyCount) return false;
            for (int i = 0; i < KeyCount; i++)
            {
                entries[i].Value = 0;
                entries[i].Valid = false;
            }

            string[] lines;
            try
            {
                if (!File.Exists(path)) return false;
                lines = File.ReadAllLines(path, Utf8NoBom);   // 同时兼容 LF / CRLF
            }
            catch { return false; }

            foreach (string line in lines)
            {
                string key, value;
                if (!TryParseEntry(line, out key, out value)) continue;

                int index = IndexOfKey(key);
                if (index < 0) continue;                       // 未知键 → 忽略

                int parsed;
                if (int.TryParse(value, out parsed))
                {
                    entries[index].Value = parsed;
                    entries[index].Valid = true;
                }
            }
            return true;
        }

        /// <summary>
        /// 写六个键的值。采用"重建行"策略：
        ///   * 目标键（Valid=true）改写为规范行 "&lt;键名&gt; 0/1"（原来的行内文档/对齐注释由游戏文档说明，可省）；
        ///   * 未知键/空行/注释行原样保留在原位置（文本按 UTF-8 解码为字符串，再以 UTF-8 写回，非 ASCII 注释无损）；
        ///   * 文件里本来没有的 Valid 键追加到末尾。
        /// 输出统一 UTF-8 无 BOM + LF；无实际改动时不写文件（保 mtime）。
        /// </summary>
        public static bool TryWrite(string path, Entry[] entries)
        {
            if (entries == null || entries.Length != KeyCount) return false;

            // 是否有任何要写的键：没有则不产生 IO（保 mtime）
            bool any = false;
            for (int i = 0; i < KeyCount; i++)
                if (entries[i].Valid) { any = true; break; }
            if (!any) return true;

            string[] lines;
            try
            {
                if (!File.Exists(path)) return false;
                lines = File.ReadAllLines(path, Utf8NoBom);
            }
            catch { return false; }

            var result = new List<string>(lines.Length + KeyCount);
            var handled = new bool[KeyCount];
            bool changed = false;

            foreach (string original in lines)
            {
                // 解析这一行：目标键 → 重建；其它（未知键/注释/空行）原样
                string key, valueToken;
                int index;
                if (TryParseEntry(original, out key, out valueToken)
                    && (index = IndexOfKey(key)) >= 0
                    && entries[index].Valid)
                {
                    string rewritten = KeyNames[index] + " " + (entries[index].Value != 0 ? "1" : "0");
                    if (rewritten != original.TrimEnd('\r')) changed = true;
                    result.Add(rewritten);
                    handled[index] = true;
                }
                else
                {
                    result.Add(original.TrimEnd('\r'));        // 去 CR：统一 LF 输出
                }
            }

            for (int i = 0; i < KeyCount; i++)
            {
                if (entries[i].Valid && !handled[i])
                {
                    result.Add(KeyNames[i] + " " + (entries[i].Value != 0 ? "1" : "0"));
                    changed = true;
                }
            }

            if (!changed) return true;                          // 无差异 → 免写，保 mtime

            // 原子写回：同目录临时文件 + File.Replace；\n 显式拼接保证 LF
            string tmp = path + ".tmp_aic_dbg";
            try
            {
                File.WriteAllText(tmp, string.Join("\n", result) + "\n", Utf8NoBom);
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
        //  行解析（纯字符串处理，无正则）
        // ==================================================================

        /// <summary>
        /// 尝试把一行解析成 (键名, 数值Token)。
        /// 规则：去首尾空白 → 空行/以 "//" 开头的行为 false → 第一个 token 是键名，
        /// 第二个 token 是数值；没有第二个 token（纯键名行）为 false。
        /// 行内注释从 "//" 起，已在 token 切分时自然剥离。
        /// </summary>
        private static bool TryParseEntry(string line, out string key, out string value)
        {
            key = null;
            value = null;

            if (line == null) return false;
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)) return false;

            int comment = trimmed.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) trimmed = trimmed.Substring(0, comment).Trim();
            if (trimmed.Length == 0) return false;

            int firstSpace = IndexOfSpace(trimmed);
            if (firstSpace < 0) return false;                  // 只有键名没有值

            key = trimmed.Substring(0, firstSpace);
            value = trimmed.Substring(firstSpace + 1).Trim();
            return value.Length > 0;
        }

        private static int IndexOfSpace(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\t') return i;
            }
            return -1;
        }

        private static int IndexOfKey(string key)
        {
            for (int i = 0; i < KeyNames.Length; i++)
                if (string.Equals(KeyNames[i], key, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }
    }
}
