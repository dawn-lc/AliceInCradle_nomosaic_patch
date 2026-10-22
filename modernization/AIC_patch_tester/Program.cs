using System;
using System.Collections.Generic;
using System.IO;
using AicNoMosaic;

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
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("用法: AIC_patch_tester <Assembly-CSharp.dll> [more.dll ...]");
                Console.WriteLine("  对每个 DLL 验证 MosaicPatcher 的定位/翻转/幂等逻辑。");
                return 2;
            }

            int failures = 0;

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
    }
}
