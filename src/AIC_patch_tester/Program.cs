using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AicNoMosaic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace AicPatchTester
{
    /// <summary>
    /// 补丁逻辑验证器：对传入的 Assembly-CSharp.dll 依次执行
    ///   1) Inspect 读取当前状态
    ///   2) Apply 翻转到"恢复马赛克"(true)   —— 应 Success（仓库内 DLL 均为已安装状态）
    ///   3) Inspect 确认已翻转
    ///   4) Apply 再次翻转 true             —— 应 AlreadyApplied（幂等）
    /// 返回非 0 表示有失败，用于 CI 断言。
    /// </summary>
    internal static class Program
    {
        private static int _synthFailures;

        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("用法: AIC_patch_tester <Assembly-CSharp.dll> [more.dll ...]");
                Console.WriteLine("  对每个 DLL 验证 MosaicPatcher 的定位/翻转/幂等逻辑。");
                return 2;
            }

            int failures = 0;

            // —— 合成用例：以第一个真实 DLL 为宿主改造出多返回点结构（不碰原文件）——
            if (args.Length > 0 && File.Exists(args[0]))
                failures += RunSyntheticTests(args[0]);

            foreach (string dll in args)
            {
                if (!File.Exists(dll))
                {
                    Console.WriteLine("[FAIL] 文件不存在: " + dll);
                    failures++;
                    continue;
                }

                Console.WriteLine("== " + Path.GetFileName(Path.GetDirectoryName(dll))
                    + Path.DirectorySeparatorChar + Path.GetFileName(dll) + " (" + new FileInfo(dll).Length.ToString("N0") + " B)");

                MosaicPatcher.InspectState state0 = MosaicPatcher.Inspect(dll);
                Console.WriteLine("   初始状态: " + state0);
                if (state0 != MosaicPatcher.InspectState.MosaicOff && state0 != MosaicPatcher.InspectState.MosaicOn)
                {
                    Console.WriteLine("   [FAIL] 初始状态异常，无法验证翻转逻辑。");
                    failures++;
                    continue;
                }

                string temp1 = dll + ".tester_flip";
                string temp2 = dll + ".tester_flip2";
                try
                {
                    // 1) 翻转到"恢复马赛克" = true（相当于卸载语义）
                    var r1 = MosaicPatcher.Apply(dll, true, temp1);
                    Console.WriteLine("   翻转 true -> " + r1);
                    if (r1 != MosaicPatcher.PatchStatus.Success) { failures++; continue; }

                    MosaicPatcher.InspectState state1 = MosaicPatcher.Inspect(temp1);
                    bool expectedOn = state0 == MosaicPatcher.InspectState.MosaicOff;   // off -> on 才算对
                    Console.WriteLine("   翻转后状态: " + state1);
                    if ((state1 == MosaicPatcher.InspectState.MosaicOn) != expectedOn)
                    {
                        Console.WriteLine("   [FAIL] 翻转后状态不符。");
                        failures++;
                        continue;
                    }

                    // 2) 再翻转一次（幂等性）
                    // 注意：AlreadyApplied 时不产生输出文件，状态检查回退到输入文件
                    var r2 = MosaicPatcher.Apply(temp1, true, temp2);
                    Console.WriteLine("   重复应用 -> " + r2);
                    string secondFile;
                    if (r2 == MosaicPatcher.PatchStatus.AlreadyApplied)
                        secondFile = temp1;
                    else if (r2 == MosaicPatcher.PatchStatus.Success)
                        secondFile = temp2;
                    else
                    {
                        Console.WriteLine("   [FAIL] 幂等性校验失败。");
                        failures++;
                        continue;
                    }

                    // 校验最终产物状态与上次一致（round-trip 完整性）
                    MosaicPatcher.InspectState state2 = MosaicPatcher.Inspect(secondFile);
                    if (state2 != state1)
                    {
                        Console.WriteLine("   [FAIL] 二次写出的状态与上次不符: " + state2);
                        failures++;
                        continue;
                    }

                    Console.WriteLine("   [OK]");
                }
                finally
                {
                    TryDelete(temp1);
                    TryDelete(temp2);
                }
            }

            Console.WriteLine(failures == 0 ? "全部通过" : ("有 " + failures + " 个失败"));
            return failures == 0 ? 0 : 1;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ==================================================================
        //  合成用例：验证多返回点（主返回点策略）与退化策略
        // ==================================================================

        private static int RunSyntheticTests(string hostGameDll)
        {
            Console.WriteLine("== 合成用例（宿主: " + Path.GetFileName(hostGameDll) + ")");
            _synthFailures = 0;
            string work = Path.Combine(Path.GetTempPath(), "aic_tester_synth");
            Directory.CreateDirectory(work);

            try
            {
                // Case A：多返回点 —— 早期 return false + 最终 return true（0.30i 实测形态）
                // 主返回点 = 最后一个 ret → 补丁只翻主点，早期分支保持原样
                //   IL: Ldc_I4_0, Ret, Ldc_I4_1, Ret
                string a = Path.Combine(work, "caseA.dll");
                string aOut = Path.Combine(work, "caseA_out.dll");
                string aOut2 = Path.Combine(work, "caseA_out2.dll");
                if (BuildSynthetic(hostGameDll, a, b =>
                {
                    b.Instructions.Add(new Instruction(OpCodes.Ldc_I4_0));
                    b.Instructions.Add(new Instruction(OpCodes.Ret));
                    b.Instructions.Add(new Instruction(OpCodes.Ldc_I4_1));
                    b.Instructions.Add(new Instruction(OpCodes.Ret));
                }))
                {
                    Expect("A1 剩余 MosaicOn", MosaicPatcher.Inspect(a) == MosaicPatcher.InspectState.MosaicOn);
                    Expect("A2 应用 false", MosaicPatcher.Apply(a, false, aOut) == MosaicPatcher.PatchStatus.Success);
                    OpCode[] ops = ReadOpcodes(aOut);
                    Expect("A3 只翻主点（早期 return 保留）", ops.Length == 4
                        && ops[0] == OpCodes.Ldc_I4_0 && ops[1] == OpCodes.Ret
                        && ops[2] == OpCodes.Ldc_I4_0 && ops[3] == OpCodes.Ret);
                    Expect("A4 应用后 MosaicOff", MosaicPatcher.Inspect(aOut) == MosaicPatcher.InspectState.MosaicOff);
                    Expect("A5 恢复 true", MosaicPatcher.Apply(aOut, true, aOut2) == MosaicPatcher.PatchStatus.Success);
                    OpCode[] ops2 = ReadOpcodes(aOut2);
                    Expect("A6 恢复后主点=1、早期点=0", ops2.Length == 4
                        && ops2[0] == OpCodes.Ldc_I4_0 && ops2[1] == OpCodes.Ret
                        && ops2[2] == OpCodes.Ldc_I4_1 && ops2[3] == OpCodes.Ret);
                }
                else { Console.WriteLine("   [FAIL] 未能在宿主上定位 MosaicShower.FnDrawMosaic"); _synthFailures++; }

                // Case B：最后一个 ret 前导非字面量 → 退化策略取唯一 {ldc, ret} 候选
                //   IL: Ldc_I4_0, Ret, Ldc_I4_M1, Ret   (M1 不是补丁候选)
                string b2 = Path.Combine(work, "caseB.dll");
                string bOut = Path.Combine(work, "caseB_out.dll");
                if (BuildSynthetic(hostGameDll, b2, bd =>
                {
                    bd.Instructions.Add(new Instruction(OpCodes.Ldc_I4_0));
                    bd.Instructions.Add(new Instruction(OpCodes.Ret));
                    bd.Instructions.Add(new Instruction(OpCodes.Ldc_I4_M1));
                    bd.Instructions.Add(new Instruction(OpCodes.Ret));
                }))
                {
                    Expect("B1 退化策略得 MosaicOff", MosaicPatcher.Inspect(b2) == MosaicPatcher.InspectState.MosaicOff);
                    Expect("B2 已是 false → AlreadyApplied", MosaicPatcher.Apply(b2, false, bOut) == MosaicPatcher.PatchStatus.AlreadyApplied);
                    Expect("B3 翻转 true", MosaicPatcher.Apply(b2, true, bOut) == MosaicPatcher.PatchStatus.Success);
                    OpCode[] ops = ReadOpcodes(bOut);
                    Expect("B4 唯一候选被翻、M1 保留", ops.Length == 4
                        && ops[0] == OpCodes.Ldc_I4_1 && ops[1] == OpCodes.Ret
                        && ops[2] == OpCodes.Ldc_I4_M1 && ops[3] == OpCodes.Ret);
                }
                else { Console.WriteLine("   [FAIL] CaseB 构造失败"); _synthFailures++; }

                // Case C：无字面量候选（结构完全改动）→ NotFound
                string c = Path.Combine(work, "caseC.dll");
                if (BuildSynthetic(hostGameDll, c, cd =>
                {
                    cd.Instructions.Add(new Instruction(OpCodes.Ldc_I4_2));
                    cd.Instructions.Add(new Instruction(OpCodes.Ret));
                    cd.Instructions.Add(new Instruction(OpCodes.Ldc_I4_M1));
                    cd.Instructions.Add(new Instruction(OpCodes.Ret));
                }))
                {
                    Expect("C1 无候选 → NotFound", MosaicPatcher.Inspect(c) == MosaicPatcher.InspectState.NotFound);
                }
                else { Console.WriteLine("   [FAIL] CaseC 构造失败"); _synthFailures++; }
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }

            return _synthFailures;
        }

        private static void Expect(string name, bool ok)
        {
            Console.WriteLine("   [" + (ok ? "OK" : "FAIL") + "] " + name);
            if (!ok) _synthFailures++;
        }

        private static bool BuildSynthetic(string hostDll, string outPath, Action<CilBody> filler)
        {
            try
            {
                using (ModuleDefMD module = ModuleDefMD.Load(hostDll))
                {
                    MethodDef method = FindMosaicMethod(module);
                    if (method == null) return false;
                    method.Body = new CilBody { InitLocals = false };
                    filler(method.Body);
                    module.Write(outPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("   [FAIL] 合成构造异常: " + ex.Message);
                return false;
            }
        }

        private static MethodDef FindMosaicMethod(ModuleDef module)
        {
            foreach (TypeDef t in module.GetTypes())
            {
                if (!string.Equals(t.Name, "MosaicShower", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (MethodDef m in t.Methods)
                {
                    if (m.HasBody && string.Equals(m.Name, "FnDrawMosaic", StringComparison.OrdinalIgnoreCase))
                        return m;
                }
            }
            return null;
        }

        private static OpCode[] ReadOpcodes(string path)
        {
            using (ModuleDefMD module = ModuleDefMD.Load(path))
            {
                MethodDef method = FindMosaicMethod(module);
                return method.Body.Instructions.Select(i => i.OpCode).ToArray();
            }
        }
    }
}
