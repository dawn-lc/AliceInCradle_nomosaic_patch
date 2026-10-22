using System;
using System.IO;

namespace AicNoMosaic
{
    /// <summary>
    /// 读写游戏自带的 _debug.txt 调试开关（自上游 C++ debug_config.h 移植）。
    /// 只修改键值数字本身；注释、空行、缩进、其它键所在行逐字节原样保留。
    /// 原子写回：先写同目录临时文件，再整体替换。
    /// </summary>
    internal static class DebugConfig
    {
        public const int KeyCount = 6;

        // 顺序与复选框一一对应
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
            public bool Valid;   // false = 文件里没有这个键（值保持 0、不参与写入）
        }

        /// <summary>读取六个键的值。文件缺失/占用返回 false；缺个别键也算读取成功（对应 Valid=false）。</summary>
        public static bool TryRead(string path, Entry[] entries)
        {
            if (entries == null || entries.Length != KeyCount) return false;
            for (int i = 0; i < KeyCount; i++)
            {
                entries[i].Value = 0;
                entries[i].Valid = false;
            }

            byte[] buf;
            try
            {
                if (!File.Exists(path)) return false;
                buf = File.ReadAllBytes(path);
            }
            catch { return false; }

            for (int i = 0; i < KeyCount; i++)
            {
                int value, s, e;
                if (FindKey(buf, KeyNames[i], out value, out s, out e) == 1)
                {
                    entries[i].Value = value;
                    entries[i].Valid = true;
                }
            }
            return true;
        }

        /// <summary>
        /// 写六个键的值。只覆盖一位数值；缺失键（Valid=false）跳过、多位数值/找不到键跳过。
        /// 若没有任何需要改动则原样返回 true（一个字节都不写）。
        /// </summary>
        public static bool TryWrite(string path, Entry[] entries)
        {
            if (entries == null || entries.Length != KeyCount) return false;

            byte[] buf;
            try
            {
                if (!File.Exists(path)) return false;
                buf = File.ReadAllBytes(path);
            }
            catch { return false; }

            // 高 24 位 = 偏移，低 8 位 = 新数字（编码进 int 数组，避免额外结构体）
            int[] todo = new int[KeyCount];
            int count = 0;
            int wanted = 0;

            for (int i = 0; i < KeyCount; i++)
            {
                if (!entries[i].Valid) continue;
                wanted++;

                int value = entries[i].Value;
                if (value < 0) value = 0;
                if (value > 9) continue;                 // 只支持一位数（本文件的值都是 0/1）

                int s, e, v;
                if (FindKey(buf, KeyNames[i], out v, out s, out e) != 1) continue;
                if (e != s + 1) continue;                // 原值不是一位数，替换会改变长度 → 跳过

                todo[count++] = (s << 8) | (value & 0xFF);
            }

            if (count == 0) return wanted == 0;

            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                int off = todo[i] >> 8;
                if (buf[off] != (byte)('0' + (todo[i] & 0xFF))) { changed = true; break; }
            }
            if (!changed) return true;                    // 本来就是这些值

            for (int i = 0; i < count; i++)
            {
                int off = todo[i] >> 8;
                buf[off] = (byte)('0' + (todo[i] & 0xFF));
            }

            // 原子写回：同目录临时文件 + 整体替换
            string tmp = path + ".tmp_aic_dbg";
            try
            {
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(buf, 0, buf.Length);
                }
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

        // ------------------------------------------------------------------
        //  底层：一趟扫描定位 "键名 空白 数字"（与上游 AicFindKey 逐行等价）
        // ------------------------------------------------------------------

        private static bool IsSpace(byte c)
        {
            return c == ' ' || c == '\t' || c == 11 || c == 12 || c == '\r';
        }

        private static bool IsDigit(byte c) { return c >= (byte)'0' && c <= (byte)'9'; }

        /// <summary>
        /// 返回 1 = 找到（value/start/end）；0 = 没找到或该行没有合法值；-1 = 参数错误。
        /// 行格式: &lt;键名&gt; &lt;空白&gt; &lt;数字&gt; [空白或 // 注释 …]
        /// </summary>
        private static int FindKey(byte[] buf, string key, out int value, out int start, out int end)
        {
            value = 0; start = 0; end = 0;
            if (buf == null || string.IsNullOrEmpty(key)) return -1;

            int n = buf.Length;
            int keyLen = key.Length;
            int i = 0;

            while (i < n)
            {
                // 1) 取出一行（不含换行符）
                int lineStart = i;
                while (i < n && buf[i] != (byte)'\n') i++;
                int lineEnd = i;
                if (lineEnd > lineStart && buf[lineEnd - 1] == (byte)'\r') lineEnd--;
                if (i < n) i++;

                // 2) 跳过行首空白
                int p = lineStart;
                while (p < lineEnd && IsSpace(buf[p])) p++;

                // 3) 空行 / 整行注释
                if (p >= lineEnd) continue;
                if (buf[p] == (byte)'/' && p + 1 < lineEnd && buf[p + 1] == (byte)'/') continue;

                // 4) 键名完全匹配，且后面是空白或行尾（避免 mighty2 命中 mighty）
                if (lineEnd - p < keyLen) continue;
                bool match = true;
                for (int k = 0; k < keyLen; k++)
                {
                    char kc = key[k];
                    if (kc > 0x7F || buf[p + k] != (byte)kc) { match = false; break; }
                }
                if (!match) continue;
                int q = p + keyLen;
                if (q < lineEnd && !IsSpace(buf[q])) continue;

                // 5) 跳过键名与值之间的空白
                while (q < lineEnd && IsSpace(buf[q])) q++;
                if (q >= lineEnd) return 0;              // 只有键名，缺值

                // 6) 值：连续数字，可带一个负号
                int valueStart = q;
                if (buf[q] == (byte)'-') q++;
                if (q >= lineEnd || !IsDigit(buf[q])) return 0;

                int val = 0;
                while (q < lineEnd && IsDigit(buf[q]))
                {
                    val = val * 10 + (buf[q] - (byte)'0');
                    q++;
                }
                int valueEnd = q;

                // 7) 值后面必须是空白、行尾或注释（否则不是纯值，例如 0x1F）
                if (q < lineEnd && !IsSpace(buf[q]) && buf[q] != (byte)'/') return 0;

                value = buf[valueStart] == (byte)'-' ? -val : val;
                start = valueStart;
                end = valueEnd;
                return 1;
            }
            return 0;
        }
    }
}
