using System;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace AicNoMosaic
{
    /// <summary>
    /// Assembly-CSharp.dll 的去马赛克补丁核心（dnlib 进程内改 IL）。
    /// 目标方法：MosaicShower.FnDrawMosaic —— 把"是否绘制马赛克"的返回点翻转到目标值。
    ///
    /// 返回点定位策略（修 0.30i 实测问题）：
    ///   0.30i 起方法体内出现多个 ret（提前 return + 最终 return），且值不一致。
    ///   与上游工具一致，"主返回点"= 最后一个 ret（其前导须为 ldc.i4.0/1）；
    ///   最后一个 ret 前导不是字面量时，退化为唯一一个 {ldc.i4.0/1, ret} 候选点。
    ///   —— 不再要求"全部返回点值一致"，早期 return 分支保持游戏原样。
    /// 其它加固：校验方法返回类型必须是 System.Boolean；大小写不敏感；
    /// GetTypes() 覆盖嵌套类型；写出后重新读出自校验。
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
            NotFound,   // 未找到目标方法/返回点
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
                    MethodDef method = FindTargetMethod(module);
                    if (method == null) return PatchStatus.NotFound;

                    int retIndex;
                    bool currentValue;
                    if (!FindPatchPoint(method, out retIndex, out currentValue))
                        return PatchStatus.NotFound;

                    if (currentValue == mosaicEnabled) return PatchStatus.AlreadyApplied;

                    method.Body.Instructions[retIndex - 1].OpCode =
                        mosaicEnabled ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0;

                    try { module.Write(outputPath); }
                    catch { return PatchStatus.IoError; }
                }

                // 写出后重新读出，确认主返回点已是目标值
                try
                {
                    using (ModuleDefMD check = ModuleDefMD.Load(outputPath))
                    {
                        MethodDef checkMethod = FindTargetMethod(check);
                        int checkIdx;
                        bool checkValue;
                        if (checkMethod == null ||
                            !FindPatchPoint(checkMethod, out checkIdx, out checkValue) ||
                            checkValue != mosaicEnabled)
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
                    MethodDef method = FindTargetMethod(module);
                    if (method == null) return InspectState.NotFound;

                    int retIndex;
                    bool currentValue;
                    if (!FindPatchPoint(method, out retIndex, out currentValue))
                        return InspectState.NotFound;

                    return currentValue ? InspectState.MosaicOn : InspectState.MosaicOff;
                }
            }
            catch
            {
                return InspectState.Error;
            }
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// 按名字找目标方法（大小写不敏感，含嵌套类型）。
        /// 返回类型必须是 System.Boolean —— 防止未来版本里出现同名方法导致改错位置。
        /// </summary>
        private static MethodDef FindTargetMethod(ModuleDefMD module)
        {
            TypeDef type = null;
            foreach (TypeDef t in module.GetTypes())
            {
                if (string.Equals(t.Name, TypeName, StringComparison.OrdinalIgnoreCase))
                {
                    type = t;
                    break;
                }
            }
            if (type == null) return null;

            foreach (MethodDef m in type.Methods)
            {
                if (!m.HasBody) continue;
                if (!string.Equals(m.Name, MethodName, StringComparison.OrdinalIgnoreCase)) continue;
                if (m.ReturnType == null || m.ReturnType.FullName != "System.Boolean") return null;
                return m;
            }
            return null;
        }

        /// <summary>
        /// 定位补丁返回点。
        /// 主策略（与上游一致）：方法体内最后一个 ret，且其前导是 ldc.i4.0/1；
        /// 退化策略：最后一个 ret 前导不是字面量时，方法体内必须恰有唯一一个
        /// {ldc.i4.0/1, ret} 候选，取之；否则视为找不到（NotFound）。
        /// 输出 retIndex = ret 指令位置（补丁点即 retIndex-1），currentValue = 当前值。
        /// </summary>
        private static bool FindPatchPoint(MethodDef method, out int retIndex, out bool currentValue)
        {
            retIndex = -1;
            currentValue = false;

            var instrs = method.Body.Instructions;
            int n = instrs.Count;

            // 最后一个 ret
            int lastRet = -1;
            for (int i = 0; i < n; i++)
            {
                if (instrs[i].OpCode == OpCodes.Ret) lastRet = i;
            }
            if (lastRet <= 0) return false;

            // 主策略：最后一个 ret 的前导是字面量
            OpCode lastPrev = instrs[lastRet - 1].OpCode;
            if (lastPrev == OpCodes.Ldc_I4_0 || lastPrev == OpCodes.Ldc_I4_1)
            {
                retIndex = lastRet;
                currentValue = lastPrev == OpCodes.Ldc_I4_1;
                return true;
            }

            // 退化策略：恰有一个 {ldc.i4.0/1, ret} 候选
            int candidate = -1;
            int candidateCount = 0;
            for (int i = 1; i < n; i++)
            {
                if (instrs[i].OpCode != OpCodes.Ret) continue;
                OpCode prev = instrs[i - 1].OpCode;
                if (prev != OpCodes.Ldc_I4_0 && prev != OpCodes.Ldc_I4_1) continue;
                candidate = i;
                candidateCount++;
            }
            if (candidateCount != 1) return false;

            retIndex = candidate;
            currentValue = instrs[candidate - 1].OpCode == OpCodes.Ldc_I4_1;
            return true;
        }
    }
}
