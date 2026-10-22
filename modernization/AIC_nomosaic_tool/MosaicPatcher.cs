using System;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace AicNoMosaic
{
    /// <summary>
    /// Assembly-CSharp.dll 的去马赛克补丁核心（dnlib 进程内改 IL）。
    /// 目标方法：MosaicShower.FnDrawMosaic —— 把最终返回 0/1 的 ldc 指令翻转到目标值。
    /// 修复了上游实现的几处鲁棒性问题：
    ///   * 遍历全部 ret 补全部位点（原版只看最后一个 ret）；
    ///   * 校验方法返回类型必须是 System.Boolean，防止未来版本语义漂移后改错方法；
    ///   * 类型名大小写不敏感，且用 GetTypes() 连嵌套类型一起搜索；
    ///   * 写出后重新读出自校验。
    /// </summary>
    public static class MosaicPatcher
    {
        internal const string TypeName = "MosaicShower";
        internal const string MethodName = "FnDrawMosaic";

        public enum PatchStatus
        {
            Success,        // 已修改并写出自校验通过
            NotFound,       // 未找到补丁目标（结构不符，疑似非目标文件或版本结构变化）
            AlreadyApplied, // 当前已是目标状态
            VerifyFailed,   // 写出后重新读取校验失败
            IoError,        // 读取/解析/写出失败
        }

        public enum InspectState
        {
            MosaicOn,   // 原始状态：绘制马赛克（未安装补丁）
            MosaicOff,  // 已安装补丁：不绘制马赛克
            Mixed,      // 多个返回点状态不一致（结构异常）
            NotFound,   // 未找到目标方法
            Error,      // 读取/解析失败
        }

        /// <summary>
        /// 对 dllPath 应用补丁：mosaicEnabled=false 去码（安装），true 恢复（卸载）。
        /// 结果写到 outputPath（调用方负责用临时文件 + File.Replace 做原子替换）。
        /// </summary>
        public static PatchStatus Apply(string dllPath, bool mosaicEnabled, string outputPath)
        {
            try
            {
                ModuleDefMD module;
                try { module = ModuleDefMD.Load(dllPath); }
                catch { return PatchStatus.IoError; }

                using (module)
                {
                    bool mosaicOn;
                    var scan = Scan(module, out mosaicOn);
                    if (scan == ScanResult.NotFound) return PatchStatus.NotFound;
                    if (scan == ScanResult.Uniform && mosaicOn == mosaicEnabled) return PatchStatus.AlreadyApplied;

                    Flip(module, mosaicEnabled);
                    try { module.Write(outputPath); }
                    catch { return PatchStatus.IoError; }
                }

                // 写出后重新读出，确认已生效
                try
                {
                    using (ModuleDefMD check = ModuleDefMD.Load(outputPath))
                    {
                        bool on;
                        if (Scan(check, out on) != ScanResult.Uniform || on != mosaicEnabled)
                            return PatchStatus.VerifyFailed;
                    }
                }
                catch { return PatchStatus.VerifyFailed; }

                return PatchStatus.Success;
            }
            catch
            {
                return PatchStatus.IoError;
            }
        }

        /// <summary>只读检查当前补丁状态，不修改任何文件。</summary>
        public static InspectState Inspect(string dllPath)
        {
            try
            {
                using (ModuleDefMD module = ModuleDefMD.Load(dllPath))
                {
                    bool on;
                    switch (Scan(module, out on))
                    {
                        case ScanResult.NotFound: return InspectState.NotFound;
                        case ScanResult.Mixed: return InspectState.Mixed;
                        default: return on ? InspectState.MosaicOn : InspectState.MosaicOff;
                    }
                }
            }
            catch
            {
                return InspectState.Error;
            }
        }

        private enum ScanResult { NotFound, Uniform, Mixed }

        /// <summary>
        /// 扫描 FnDrawMosaic 的全部 {ldc.i4.0/1, ret} 返回点。
        /// mosaicOn 输出：全部返回点一致时为该值；NotFound/Mixed 时无意义。
        /// </summary>
        private static ScanResult Scan(ModuleDefMD module, out bool mosaicOn)
        {
            mosaicOn = false;

            TypeDef type = FindType(module, TypeName);
            if (type == null) return ScanResult.NotFound;

            MethodDef method = null;
            foreach (MethodDef m in type.Methods)
            {
                if (m.HasBody && string.Equals(m.Name, MethodName, StringComparison.OrdinalIgnoreCase))
                {
                    method = m;
                    break;
                }
            }
            if (method == null) return ScanResult.NotFound;

            // 返回类型校验：目标方法必须返回 bool（防止未来版本里出现同名方法导致改错位置）
            if (method.ReturnType == null || method.ReturnType.FullName != "System.Boolean")
                return ScanResult.NotFound;

            var instrs = method.Body.Instructions;
            bool sawCandidate = false;
            int on = 0, off = 0;
            for (int i = 0; i < instrs.Count; i++)
            {
                if (instrs[i].OpCode != OpCodes.Ret || i == 0) continue;
                Instruction prev = instrs[i - 1];
                if (prev.OpCode != OpCodes.Ldc_I4_0 && prev.OpCode != OpCodes.Ldc_I4_1) continue;
                sawCandidate = true;
                if (prev.OpCode == OpCodes.Ldc_I4_1) on++; else off++;
            }

            if (!sawCandidate) return ScanResult.NotFound;
            if (on > 0 && off == 0) { mosaicOn = true; return ScanResult.Uniform; }
            if (off > 0 && on == 0) { mosaicOn = false; return ScanResult.Uniform; }
            return ScanResult.Mixed;
        }

        /// <summary>把所有 {ldc.i4.0/1, ret} 返回点翻转到目标值。</summary>
        private static void Flip(ModuleDefMD module, bool mosaicEnabled)
        {
            TypeDef type = FindType(module, TypeName);
            if (type == null) return;

            foreach (MethodDef m in type.Methods)
            {
                if (!m.HasBody || !string.Equals(m.Name, MethodName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (m.ReturnType == null || m.ReturnType.FullName != "System.Boolean")
                    continue;

                var instrs = m.Body.Instructions;
                OpCode target = mosaicEnabled ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0;
                for (int i = 0; i < instrs.Count; i++)
                {
                    if (instrs[i].OpCode != OpCodes.Ret || i == 0) continue;
                    Instruction prev = instrs[i - 1];
                    if (prev.OpCode == OpCodes.Ldc_I4_0 || prev.OpCode == OpCodes.Ldc_I4_1)
                        prev.OpCode = target;
                }
            }
        }

        /// <summary>按名字找类型（大小写不敏感，含嵌套类型）。</summary>
        private static TypeDef FindType(ModuleDefMD module, string name)
        {
            foreach (TypeDef t in module.GetTypes())
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                    return t;
            return null;
        }
    }
}
