using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AicNoMosaic
{
    /// <summary>
    /// 从 &lt;游戏根目录&gt;\AliceInCradle_Data\globalgamemanagers
    /// （Unity SerializedFile）解析 PlayerSettings.bundleVersion —— 即运行时
    /// Application.version。自上游 C++ version.h 逐行移植：不依赖硬编码偏移，
    /// 按容器结构逐级校验求得；任何一步校验失败一律返回"未知"兜底。
    /// </summary>
    internal static class GameVersion
    {
        private const string DataFolder = "AliceInCradle_Data";
        private const string FileName = "globalgamemanagers";
        private const int MaxFileBytes = 64 * 1024 * 1024;

        public static string FromFolder(string gameFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(gameFolder)) return "";
                string path = Path.Combine(gameFolder, DataFolder, FileName);
                if (!File.Exists(path)) return "";
                var fi = new FileInfo(path);
                if (fi.Length < 64 || fi.Length > MaxFileBytes) return "";
                byte[] b = File.ReadAllBytes(path);
                return ExtractVersion(b);
            }
            catch { return ""; }
        }

        /// <summary>解析失败时按上游 GetVer 的方法 2 降级：取文件夹名下划线后的部分。</summary>
        public static string FromFolderWithFallback(string gameFolder)
        {
            string v = FromFolder(gameFolder);
            if (!string.IsNullOrEmpty(v)) return v;

            if (string.IsNullOrEmpty(gameFolder)) return "未知";
            string trimmed = gameFolder.TrimEnd('/', '\\');
            if (trimmed.Length == 0) return "未知";
            int lastSlash = trimmed.LastIndexOfAny(new[] { '/', '\\' });
            string name = lastSlash >= 0 ? trimmed.Substring(lastSlash + 1) : trimmed;

            int first = name.IndexOf('_');
            if (first < 0) return "未知";
            if (name.IndexOf('_', first + 1) >= 0) return "未知";
            if (first + 1 >= name.Length) return "未知";
            return name.Substring(first + 1);
        }

        // ============================== 字节读取 ==============================

        private static uint U32BE(byte[] b, int p)
        {
            return ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3];
        }

        private static uint U32LE(byte[] b, int p)
        {
            return b[p] | ((uint)b[p + 1] << 8) | ((uint)b[p + 2] << 16) | ((uint)b[p + 3] << 24);
        }

        private static int I32LE(byte[] b, int p) { return unchecked((int)U32LE(b, p)); }

        private static ulong U64BE(byte[] b, int p)
        {
            return ((ulong)U32BE(b, p) << 32) | U32BE(b, p + 4);
        }

        private static ulong U64LE(byte[] b, int p)
        {
            ulong v = 0;
            for (int i = 7; i >= 0; --i) v = (v << 8) | b[p + i];
            return v;
        }

        private static bool InRange(int size, int off, int len)
        {
            return off >= 0 && len >= 0 && off <= size && len <= size - off;
        }

        private static int AlignUp(int v, int a) { return (v + (a - 1)) & ~(a - 1); }

        // ============================ 字符串判定 =============================

        private static bool PrintableAscii(byte[] b, int off, int len)
        {
            for (int i = 0; i < len; i++)
            {
                if (b[off + i] < 0x20 || b[off + i] > 0x7E) return false;
            }
            return true;
        }

        // 版本样式: 1~4 段数字以 '.' 分隔，可选尾部单字母 + 可选数字
        // 命中: "1.0" "0.29b" "0.29b2" "1.2.3"   不命中: "Very Low" "x" "0"
        private static bool LooksLikeVersion(byte[] b, int off, int len)
        {
            if (len < 3 || len > 24) return false;
            int i = 0;
            int groups = 0;
            for (;;)
            {
                int st = i;
                while (i < len && b[off + i] >= '0' && b[off + i] <= '9') i++;
                int digits = i - st;
                if (digits < 1 || digits > 4) return false;
                if (++groups > 4) return false;
                if (i < len && b[off + i] == '.') { i++; continue; }
                break;
            }
            if (groups < 2) return false;
            if (i < len && ((b[off + i] >= 'a' && b[off + i] <= 'z') || (b[off + i] >= 'A' && b[off + i] <= 'Z')))
            {
                i++;
                while (i < len && b[off + i] >= '0' && b[off + i] <= '9') i++;
            }
            return i == len;
        }

        // 包名样式 com.Company.Product（至少两段 '.'，且本身不是版本串）
        private static bool LooksLikeBundleId(byte[] b, int off, int len)
        {
            if (len < 5 || len > 160) return false;
            if (LooksLikeVersion(b, off, len)) return false;
            int dots = 0;
            for (int i = 0; i < len; i++)
            {
                byte c = b[off + i];
                if (c == '.') { dots++; continue; }
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
                    return false;
            }
            return dots >= 2;
        }

        private static string WidenAscii(byte[] b, int off, int len)
        {
            return Encoding.ASCII.GetString(b, off, len);
        }

        // ============================ 序列化串扫描 ===========================

        private struct Candidate { public int Rel; public string Value; }

        // 扫描 [base, base+size) 内所有"格式良好"的 Unity 序列化字符串。
        // 校验: 4 字节对齐起点 + 长度 1..32 + 全可打印 ASCII + 对齐填充字节必须为 0
        private static void ScanSerializedStrings(byte[] b, int baseOff, int size,
            List<Candidate> versions, List<string> ids)
        {
            if (!InRange(b.Length, baseOff, size)) return;
            for (int off = 0; off + 4 <= size; off += 4)
            {
                uint lenU = U32LE(b, baseOff + off);
                if (lenU < 1 || lenU > 32) continue;
                int len = (int)lenU;
                if (off + 4 + len > size) continue;
                if (!PrintableAscii(b, baseOff + off + 4, len)) continue;

                int pad = AlignUp(len, 4);
                if (off + 4 + pad > size || pad > 32) continue;
                bool zeroPad = true;
                for (int k = off + 4 + len; k < off + 4 + pad; k++)
                    if (b[baseOff + k] != 0) { zeroPad = false; break; }
                if (!zeroPad) continue;

                int s = baseOff + off + 4;
                if (ids != null && LooksLikeBundleId(b, s, len))
                    ids.Add(WidenAscii(b, s, len));
                if (versions != null && LooksLikeVersion(b, s, len))
                {
                    Candidate c;
                    c.Rel = off;
                    c.Value = WidenAscii(b, s, len);
                    versions.Add(c);
                }
            }
        }

        // ============================ 对象表校验 =============================

        private struct ObjEntry { public long ByteStart; public uint ByteSize; public int ClassId; }

        // 只用文件自身的结构约束判定"这里是不是真的对象表"（与内容无关）
        private static bool ValidateObjectTable(byte[] b, int countPos,
            ulong dataOffset, ulong fileSize, List<int> classIds,
            List<ObjEntry> objects, ulong tailSlack)
        {
            objects.Clear();
            if (!InRange(b.Length, countPos, 4)) return false;
            if ((ulong)countPos + 4 > dataOffset) return false;

            int count = I32LE(b, countPos);
            if (count < 1 || count > 200000) return false;

            int first = AlignUp(countPos + 4, 8);
            if (first > (long)dataOffset) return false;
            ulong tableBytes = (ulong)(first - countPos) + ((ulong)count * 24UL);
            if (tableBytes > dataOffset - (ulong)countPos) return false;
            if (!InRange(b.Length, first, count * 24)) return false;

            ulong dataSize = fileSize - dataOffset;
            long prev = 0;
            int p = first;
            for (int i = 0; i < count; i++)
            {
                ObjEntry o;
                p += 8;                                   // int64 pathID（用不到）
                o.ByteStart = unchecked((long)U64LE(b, p)); p += 8;
                o.ByteSize = U32LE(b, p); p += 4;
                int typeId = I32LE(b, p); p += 4;

                if (o.ByteStart < 0 || o.ByteStart < prev) return false;
                if (o.ByteStart - prev > 64) return false;   // 相邻间隙不能过大
                if ((ulong)o.ByteStart + o.ByteSize > dataSize) return false;
                prev = o.ByteStart + o.ByteSize;

                o.ClassId = -1;
                if (classIds != null && typeId >= 0 && typeId < classIds.Count)
                    o.ClassId = classIds[typeId];
                objects.Add(o);
            }
            if (prev > (long)dataSize) return false;
            return (ulong)((long)dataSize - prev) <= tailSlack;
        }

        // ====================== 字段布局表（按引擎版本） =====================

        // 引擎版本前缀 -> bundleVersion 的 4 字节长度前缀在 PlayerSettings 对象内的相对偏移。
        // 换 Unity 版本时若校验失败会自动降级为对象内扫描，缺条目也不会返回错值。
        private static int? FindLayoutOffset(string engineVersion)
        {
            if (engineVersion != null && engineVersion.StartsWith("2022.3", StringComparison.Ordinal))
                return 612;   // 实测: 长度前缀在对象内 612，字符串本体在 616
            return null;
        }

        // 按已知相对偏移取串并做全量校验
        private static string ReadFieldAt(byte[] b, int baseOff, int size, int rel)
        {
            if (!InRange(b.Length, baseOff, size)) return null;
            if (rel + 4 > size) return null;
            uint lenU = U32LE(b, baseOff + rel);
            if (lenU < 1 || lenU > 32) return null;
            int len = (int)lenU;
            if (rel + 4 + len > size) return null;

            if (!PrintableAscii(b, baseOff + rel + 4, len) || !LooksLikeVersion(b, baseOff + rel + 4, len))
                return null;

            int pad = AlignUp(len, 4);
            if (rel + 4 + pad > size || pad > 32) return null;
            for (int k = rel + 4 + len; k < rel + 4 + pad; k++)
                if (b[baseOff + k] != 0) return null;

            return WidenAscii(b, baseOff + rel + 4, len);
        }

        // ============================== 核心提取 =============================

        private static string ExtractVersion(byte[] b)
        {
            int n = b.Length;
            if (n < 64) return null;

            // ---- L1: 文件头 48 字节（大端）----
            uint formatVersion = U32BE(b, 8);
            ulong metadataSize = U64BE(b, 16);
            ulong fileSize = U64BE(b, 24);
            ulong dataOffset = U64BE(b, 32);

            if (formatVersion < 17 || formatVersion > 24) return null;
            if (fileSize != (ulong)n) return null;
            if (metadataSize > (ulong)n) return null;
            if (dataOffset != 48UL + (ulong)AlignUp((int)metadataSize, 16)) return null;

            // ---- L2: 元数据头（小端）----
            int e = 48;
            while (e < n && b[e] != 0) e++;
            if (e >= n || e - 48 > 64) return null;
            string engineVersion = Encoding.ASCII.GetString(b, 48, e - 48);

            int pos = AlignUp(e + 1, 4);
            if (!InRange(n, pos, 9)) return null;
            pos += 4;                                   // int32 targetPlatform
            bool enableTypeTree = b[pos] != 0; pos += 1;
            int typeCount = I32LE(b, pos); pos += 4;
            if (typeCount < 0 || typeCount > 200000) return null;

            int typeTableStart = pos;

            // ---- L3a: 类型表（只取 classID 列表；失败也不影响后续）----
            List<int> classIds = null;
            bool haveClassIds = false;
            if (!enableTypeTree)
            {
                classIds = new List<int>(Math.Min(typeCount, 100000));
                int p = pos;
                bool ok = true;
                for (int i = 0; i < typeCount; i++)
                {
                    if (!InRange(n, p, 23)) { ok = false; break; }
                    int cid = I32LE(b, p);
                    p += 4 + 1 + 2 + 16;
                    if (cid != 114) { /* 普通条目 */ }
                    else
                    {
                        if (!InRange(n, p, 16)) { ok = false; break; }
                        p += 16;
                    }
                    classIds.Add(cid);
                }
                if (ok) { haveClassIds = true; pos = p; }
                else classIds = null;
            }

            // ---- L3b: 定位对象表（先按位置推算，再用结构判据回查）----
            var objects = new List<ObjEntry>();
            ulong[] slacks = { 0UL, 8192UL };
            int limit = (int)Math.Min(dataOffset, (ulong)int.MaxValue);
            int scanEnd = Math.Min(limit, typeTableStart + (2 << 20));   // 回查范围限 2MB

            bool located = false;
            for (int k = 0; k < 2 && !located; k++)
            {
                if (haveClassIds && ValidateObjectTable(b, pos, dataOffset, fileSize, classIds, objects, slacks[k]))
                {
                    located = true;
                    break;
                }
                objects.Clear();
                for (int cand = typeTableStart; cand + 4 <= scanEnd; cand++)
                {
                    if (ValidateObjectTable(b, cand, dataOffset, fileSize, haveClassIds ? classIds : null, objects, slacks[k]))
                    {
                        located = true;
                        break;
                    }
                    objects.Clear();
                }
            }
            if (!located) return null;

            // ---- L4: 定位 PlayerSettings 优先 classID==129；类型表可用时按内容特征兜底 ----
            int psIndex = -1;
            for (int i = 0; i < objects.Count; i++)
                if (objects[i].ClassId == 129) { psIndex = i; break; }

            if (psIndex < 0)
            {
                int best = int.MaxValue;
                for (int i = 0; i < objects.Count; i++)
                {
                    if (objects[i].ByteSize == 0 || objects[i].ByteStart < 0) continue;
                    int baseOff = (int)dataOffset + (int)objects[i].ByteStart;
                    if (!InRange(n, baseOff, (int)objects[i].ByteSize)) continue;
                    var v = new List<Candidate>();
                    var ids = new List<string>();
                    ScanSerializedStrings(b, baseOff, (int)objects[i].ByteSize, v, ids);
                    if (v.Count == 0 || ids.Count == 0) continue;
                    if (v.Count < best) { best = v.Count; psIndex = i; }
                }
            }
            if (psIndex < 0 || objects[psIndex].ByteSize == 0) return null;

            int psBase = (int)dataOffset + (int)objects[psIndex].ByteStart;
            int psSize = (int)objects[psIndex].ByteSize;
            if (!InRange(n, psBase, psSize)) return null;

            // ---- L5: 定位字段（布局表 + 校验；失败则对象内扫描）----
            int? rel = FindLayoutOffset(engineVersion);
            if (rel.HasValue)
            {
                string val = ReadFieldAt(b, psBase, psSize, rel.Value);
                if (!string.IsNullOrEmpty(val)) return val;
            }

            var cands = new List<Candidate>();
            ScanSerializedStrings(b, psBase, psSize, cands, null);
            if (cands.Count == 0) return null;
            // 观测: bundleVersion 是该对象内最后一个版本样串
            return cands[cands.Count - 1].Value;
        }
    }
}
