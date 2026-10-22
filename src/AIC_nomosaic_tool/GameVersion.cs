using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AicNoMosaic
{
    /// <summary>
    /// 从 &lt;游戏根目录&gt;\AliceInCradle_Data\globalgamemanagers
    /// （Unity SerializedFile）解析 PlayerSettings.bundleVersion —— 即运行时
    /// Application.version。
    ///
    /// 解析不依赖硬编码偏移：按容器结构逐级校验（头部自洽性 → 引擎版本 →
    /// 类型表 classID → 对象表 → PlayerSettings → bundleVersion），任何一步
    /// 校验失败一律返回"未知"兜底。
    /// </summary>
    internal static class GameVersion
    {
        private const string DataFolder = "AliceInCradle_Data";
        private const string FileName = "globalgamemanagers";
        private const long MaxFileBytes = 64 * 1024 * 1024;

        public static string FromFolder(string gameFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(gameFolder)) return "";
                string path = Path.Combine(gameFolder, DataFolder, FileName);
                if (!File.Exists(path)) return "";
                var fi = new FileInfo(path);
                if (fi.Length < 64 || fi.Length > MaxFileBytes) return "";
                return ExtractVersion(File.ReadAllBytes(path));
            }
            catch { return ""; }
        }

        // ============================ 容器头 ================================

        /// <summary>容器头解析结果（L1 大端整数 + 元数据头引擎串）。</summary>
        internal readonly struct HeadInfo
        {
            public readonly bool Ok;              // 头部结构自洽（公式成立且引擎串解出）
            public readonly uint FormatVersion;
            public readonly ulong MetadataSize;
            public readonly ulong FileSize;       // 头部声明的文件大小（不是实际长度）
            public readonly ulong DataOffset;
            public readonly string EngineVersion; // 如 "2022.3.62f2"

            public HeadInfo(bool ok, uint formatVersion, ulong metadataSize,
                ulong fileSize, ulong dataOffset, string engineVersion)
            {
                Ok = ok;
                FormatVersion = formatVersion;
                MetadataSize = metadataSize;
                FileSize = fileSize;
                DataOffset = dataOffset;
                EngineVersion = engineVersion;
            }
        }

        /// <summary>
        /// 只解析容器头（48 字节 L1 + 元数据头里的引擎串），不触碰数据区。
        /// 供 ExtractVersion 使用，也可只喂"文件头片段"做诊断/测试（如 128B 样本）。
        /// </summary>
        internal static HeadInfo ParseHead(byte[] b)
        {
            if (b.Length < 64) return default;

            // L1 头：大端整数。formatVersion@8 / metadataSize@16 / fileSize@24 / dataOffset@32
            uint formatVersion = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8));
            ulong metadataSize = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(16));
            ulong fileSize = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(24));
            ulong dataOffset = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(32));

            bool selfConsistent =
                formatVersion >= 17U && formatVersion <= 24U
                && metadataSize <= fileSize
                && dataOffset == 48UL + (ulong)AlignUp(unchecked((int)metadataSize), 16);
            if (!selfConsistent) return default;

            // 元数据头首个字段：NUL 结尾的引擎版本串（如 "2022.3.62f2"）
            int nul = Array.IndexOf(b, (byte)0, 48);
            if (nul < 0 || nul - 48 > 64) return default;
            string engineVersion = Encoding.ASCII.GetString(b, 48, nul - 48);

            return new HeadInfo(true, formatVersion, metadataSize, fileSize, dataOffset, engineVersion);
        }

        // ============================== 核心提取 =============================

        private static string ExtractVersion(byte[] b)
        {
            if (b.Length < 64) return null;

            // ---- L1+L2 前半：容器头（结构性校验），声明大小必须等于实际长度 ----
            HeadInfo head = ParseHead(b);
            if (!head.Ok || head.FileSize != (ulong)b.Length) return null;   // 截断/伪装文件

            // ---- L2 后半：类型表入口 ----
            int pos = AlignUp(48 + head.EngineVersion.Length + 1, 4);
            pos += 4;                                   // int32 targetPlatform（用不到）
            bool enableTypeTree = b[pos] != 0;
            int typeCount = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pos + 1));
            if (typeCount < 0 || typeCount > 200000) return null;
            int typeTableStart = pos + 5;

            // ---- L3a: 类型表（只取 classID 列表；失败也不影响后续）----
            // 条目 = int32 classID + uint8 isStripped + int16 scriptTypeIndex + 16B 哈希；
            // classID==114 的条目（脚本引用）额外多 16 字节。
            List<int> classIds = null;
            if (!enableTypeTree && TryReadClassIds(b, typeTableStart, typeCount, out classIds, out int afterTypes))
                pos = afterTypes;

            // ---- L3b: 定位对象表（先按位置推算，再用结构判据回查）----
            var objects = new List<ObjInfo>();
            ulong[] slacks = { 0UL, 8192UL };
            int scanEnd = Math.Min((int)Math.Min(head.DataOffset, int.MaxValue), typeTableStart + (2 << 20));

            bool located = false;
            for (int k = 0; k < 2 && !located; k++)
            {
                if (classIds != null && TryReadObjectTable(b, pos, head.DataOffset, head.FileSize, classIds, objects, slacks[k]))
                { located = true; break; }

                objects.Clear();
                for (int cand = typeTableStart; cand + 4 <= scanEnd; cand++)
                {
                    if (TryReadObjectTable(b, cand, head.DataOffset, head.FileSize, classIds, objects, slacks[k]))
                    { located = true; break; }
                    objects.Clear();
                }
            }
            if (!located) return null;

            // ---- L4: 定位 PlayerSettings（优先 classID==129；否则按内容特征兜底）----
            ObjInfo? ps = FindPlayerSettings(b, (int)head.DataOffset, objects);
            if (ps == null || ps.Value.Size == 0) return null;

            // ---- L5: bundleVersion 字符串 ----
            // 快路：已知引擎布局（2022.3 实测长度前缀在对象内 612）；失败自动降级为扫描。
            var objData = new ReadOnlySpan<byte>(b, (int)head.DataOffset + ps.Value.Start, ps.Value.Size);
            string quick = TryReadVersionField(objData, FindLayoutOffset(head.EngineVersion));
            if (quick != null) return quick;

            // 慢路：观测 bundleVersion 是 PlayerSettings 对象内最后一个版本样式串
            var (versions, _) = CollectSerializedStrings(objData, wantVersions: true, wantBundleIds: false);
            return versions.Count > 0 ? versions[versions.Count - 1].Value : null;
        }

        // ============================ 类型表 ================================

        private static bool TryReadClassIds(byte[] b, int start, int typeCount,
            out List<int> classIds, out int next)
        {
            classIds = new List<int>(Math.Min(typeCount, 100000));
            next = start;
            int p = start;
            for (int i = 0; i < typeCount; i++)
            {
                if (p + 23 > b.Length) return false;
                int classId = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
                p += 4 + 1 + 2 + 16;                 // classID + isStripped + scriptTypeIndex + 哈希
                if (classId == 114) p += 16;         // 脚本引用条目：本地哈希 16B
                classIds.Add(classId);
            }
            next = p;
            return true;
        }

        // ============================ 对象表 ================================

        private readonly struct ObjInfo
        {
            public readonly int Start;               // 数据区内偏移
            public readonly int Size;
            public readonly int ClassId;             // 类型表不可用时为 -1

            public ObjInfo(int start, int size, int classId)
            { Start = start; Size = size; ClassId = classId; }
        }

        /// <summary>
        /// 只用文件自身的结构约束判定"这里是不是真的对象表"（与内容无关）：
        /// 数量合理、表不越过数据区、byteStart 单调不减且间隙 &lt;64、对象都落在数据区内。
        /// </summary>
        private static bool TryReadObjectTable(byte[] b, int countPos,
            ulong dataOffset, ulong fileSize, List<int> classIds,
            List<ObjInfo> objects, ulong tailSlack)
        {
            objects.Clear();
            if (countPos < 0 || countPos + 4 > b.Length || countPos + 4 > (long)dataOffset) return false;

            int count = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(countPos));
            if (count < 1 || count > 200000) return false;

            int first = AlignUp(countPos + 4, 8);
            if (first > b.Length || count * 24 > b.Length - first) return false;
            if ((ulong)(first - countPos) + (ulong)count * 24UL > dataOffset - (ulong)countPos) return false;

            ulong dataSize = fileSize - dataOffset;
            int p = first;
            long prev = 0;
            for (int i = 0; i < count; i++)
            {
                p += 8;                                                        // int64 pathID（用不到）
                long byteStart = unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)));
                uint byteSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 8));
                int typeId = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 12));
                p += 20;

                if (byteStart < 0 || byteStart < prev || byteStart - prev > 64) return false;
                if ((ulong)byteStart + byteSize > dataSize) return false;
                prev = byteStart + byteSize;

                int classId = classIds != null && typeId >= 0 && typeId < classIds.Count
                    ? classIds[typeId]
                    : -1;
                objects.Add(new ObjInfo((int)byteStart, unchecked((int)byteSize), classId));
            }

            return prev <= (long)dataSize && (ulong)((long)dataSize - prev) <= tailSlack;
        }

        // ================== PlayerSettings 定位与版本串 ======================

        private static ObjInfo? FindPlayerSettings(byte[] b, int dataOffset, List<ObjInfo> objects)
        {
            foreach (var o in objects)
                if (o.ClassId == 129) return o;

            // 类型表不可用时按内容特征兜底：对象内同时含版本样式串与包名样式串，
            // 取版本候选最少者（PlayerSettings 只有几个候选）。
            ObjInfo? best = null;
            int bestCount = int.MaxValue;
            foreach (var o in objects)
            {
                if (o.Size == 0 || o.Start < 0 || (long)dataOffset + o.Start + o.Size > b.Length) continue;
                var slice = new ReadOnlySpan<byte>(b, dataOffset + o.Start, o.Size);
                var (versions, bundleIds) = CollectSerializedStrings(slice, wantVersions: true, wantBundleIds: true);
                if (versions.Count == 0 || bundleIds.Count == 0) continue;
                if (versions.Count < bestCount) { bestCount = versions.Count; best = o; }
            }
            return best;
        }

        // 引擎版本前缀 -> bundleVersion 的长度前缀在 PlayerSettings 对象内的相对偏移。
        // 换 Unity 版本时校验失败会自动降级为对象内扫描，缺条目也不会返回错值。
        private static int? FindLayoutOffset(string engineVersion)
        {
            return engineVersion != null && engineVersion.StartsWith("2022.3", StringComparison.Ordinal)
                ? 612                     // 实测: 长度前缀在对象内 612，字符串本体在 616
                : (int?)null;
        }

        /// <summary>按已知相对偏移读取 bundleVersion 串（做完整校验）。</summary>
        private static string TryReadVersionField(ReadOnlySpan<byte> obj, int? rel)
        {
            if (rel == null || rel.Value + 4 > obj.Length) return null;

            uint lenU = BinaryPrimitives.ReadUInt32LittleEndian(obj.Slice(rel.Value));
            if (lenU < 1U || lenU > 32U || (long)rel.Value + 4 + lenU > (long)obj.Length) return null;

            int len = (int)lenU;
            var body = obj.Slice(rel.Value + 4, len);
            if (!IsPrintableAscii(body) || !LooksLikeVersion(Ascii(body))) return null;

            int padded = AlignUp(len, 4);
            if (rel.Value + 4 + padded > obj.Length) return null;
            foreach (byte padByte in obj.Slice(rel.Value + 4 + len, padded - len))
                if (padByte != 0) return null;

            return Ascii(body);
        }

        // ============================ 序列化串扫描 ===========================

        private readonly struct StrCandidate
        {
            public readonly int Offset;           // 对象内相对偏移（指向长度前缀）
            public readonly string Value;

            public StrCandidate(int offset, string value) { Offset = offset; Value = value; }
        }

        private static (List<StrCandidate> versions, List<string> bundleIds) CollectSerializedStrings(
            ReadOnlySpan<byte> obj, bool wantVersions, bool wantBundleIds)
        {
            var versions = wantVersions ? new List<StrCandidate>() : null;
            var bundleIds = new List<string>();

            for (int off = 0; off + 4 <= obj.Length; off += 4)
            {
                uint lenU = BinaryPrimitives.ReadUInt32LittleEndian(obj.Slice(off));
                if (lenU < 1U || lenU > 32U || off + 4 + (long)lenU > (long)obj.Length) continue;
                int len = (int)lenU;

                var body = obj.Slice(off + 4, len);
                if (!IsPrintableAscii(body)) continue;

                int padded = AlignUp(len, 4);
                if (off + 4 + padded > obj.Length) continue;
                bool zeroPad = true;
                foreach (byte padByte in obj.Slice(off + 4 + len, padded - len))
                    if (padByte != 0) { zeroPad = false; break; }
                if (!zeroPad) continue;

                string value = Ascii(body);
                if (wantVersions && LooksLikeVersion(value))
                    versions.Add(new StrCandidate(off, value));
                if (wantBundleIds && LooksLikeBundleId(value))
                    bundleIds.Add(value);
            }
            return (versions, bundleIds);
        }

        // ============================= 字符串判定 ============================

        private static bool IsPrintableAscii(ReadOnlySpan<byte> s)
        {
            foreach (byte c in s)
                if (c < 0x20 || c > 0x7E) return false;
            return true;
        }

        // 版本样式: 1~4 段数字以 '.' 分隔，可选尾部单字母 + 数字
        // 命中: "1.0" "0.29b" "0.29b2" "1.2.3"   不命中: "Very Low" "x" "0"
        private static bool LooksLikeVersion(string s)
        {
            if (s.Length < 3 || s.Length > 24) return false;

            var parts = s.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return false;

            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length < 1 || parts[i].Length > 4) return false;   // 每段 1~4 位
                string part = parts[i];

                if (i < parts.Length - 1)
                {
                    if (!IsDigits(part)) return false;             // 中间段必须纯数字
                    continue;
                }

                // 末段: 数字 + 可选单字母 + 可选数字（"29" / "29b" / "29b2"）
                int digits = 0;
                while (digits < part.Length && IsAsciiDigit(part[digits])) digits++;
                if (digits == 0) return false;
                if (digits == part.Length) return true;

                if (digits + 1 > part.Length - digits) return false; // 只允许 1 个字母
                if (!IsAsciiLetter(part[digits])) return false;
                int trailing = part.Length - (digits + 1);
                return trailing == 0 || (trailing == 1 && IsAsciiDigit(part[digits + 1]));
            }
            return true;
        }

        // 包名样式 com.Company.Product：至少两段 '.'，本身不是版本串
        private static bool LooksLikeBundleId(string s)
        {
            if (s.Length < 5 || s.Length > 160 || LooksLikeVersion(s)) return false;
            int dots = 0;
            foreach (char c in s)
            {
                if (c == '.') { dots++; continue; }
                if (!(IsAsciiLetterOrDigit(c) || c == '_' || c == '-')) return false;
            }
            return dots >= 2;
        }

        private static bool IsDigits(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s)
                if (!IsAsciiDigit(c)) return false;
            return true;
        }

        private static int AlignUp(int v, int align) => (v + (align - 1)) & ~(align - 1);

        // net48 兼容：char.IsAsciiXxx 是 .NET 7+ API；ASCII 区间判断语义等价
        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        private static bool IsAsciiLetter(char c)
            => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        private static bool IsAsciiLetterOrDigit(char c)
            => IsAsciiLetter(c) || IsAsciiDigit(c);

        /// <summary>net48 无 Span 重载：ASCII span → 字符串（小串，拷贝代价可接受）</summary>
        private static string Ascii(ReadOnlySpan<byte> body)
            => Encoding.ASCII.GetString(body.ToArray());
    }
}
