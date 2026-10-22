using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace AicNoMosaic.BepInEx
{
    /// <summary>
    /// Alice In Cradle 去马赛克 BepInEx 插件。
    ///
    /// 与"改 Assembly-CSharp.dll"的静态补丁不同，本插件用 Harmony 在运行时
    /// 拦截 MosaicShower.FnDrawMosaic 的返回值 —— 游戏更新后无需重新打补丁，
    /// 删掉插件文件即完全还原。也可与静态补丁共存（Harmony 层优先生效）。
    ///
    /// 定位方式：TargetMethod() 反射解析类型/方法名，不依赖游戏程序集的
    /// 编译期引用，对未来版本的方法迁移更宽容；找不到目标时只记日志、不打补丁。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class NoMosaicPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.lhycloud.aic.nomosaic";
        public const string PluginName = "AIC NoMosaic";
        public const string PluginVersion = "1.0.0";

        internal ConfigEntry<bool> Enabled;
        private ManualLogSource _log;

        /// <summary>给 NoMosaicPatch 用的单例访问（Awake 里赋值，PatchAll 之前就绪）。</summary>
        internal static NoMosaicPlugin Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            _log = Logger;

            Enabled = Config.Bind(
                "General",
                "Enabled",
                true,
                "去马赛克总开关。可直接编辑 BepInEx/config/" + PluginGuid + ".cfg，" +
                "或用 BepInEx ConfigurationManager（F1）热切换。");

            try
            {
                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(NoMosaicPatch));
                _log.LogInfo(PluginName + " " + PluginVersion + " 已加载，Enabled=" + Enabled.Value);
            }
            catch (Exception ex)
            {
                _log.LogError("Harmony 补丁注册失败：" + ex);
            }

            Enabled.SettingChanged += (s, e) =>
                _log.LogInfo(PluginName + " 开关已切换为 " + Enabled.Value);
        }
    }

    [HarmonyPatch]
    internal static class NoMosaicPatch
    {
        /// <summary>反射定位 MosaicShower.FnDrawMosaic（大小写不敏感）。</summary>
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("MosaicShower");
            if (type == null) return null;
            return AccessTools.Method(type, "FnDrawMosaic");
        }

        /// <summary>探测失败不打补丁也不崩溃，只发日志（Harmony 约定：Prepare 返回 false 跳过）。</summary>
        private static bool Prepare(MethodBase original)
        {
            return original != null;
        }

        /// <summary>Postfix 直接改写返回值：false = 不绘制马赛克。</summary>
        private static void Postfix(ref bool __result)
        {
            NoMosaicPlugin plugin = NoMosaicPlugin.Instance;
            if (plugin == null || plugin.Enabled == null || !plugin.Enabled.Value) return;
            __result = false;
        }
    }
}
