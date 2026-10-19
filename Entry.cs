using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace AloneFight;

/// <summary>模组入口：注册全部 Harmony 补丁与 Godot 脚本桥接。</summary>
[ModInitializer(nameof(Init))]
public static class Entry
{
    /// <summary>游戏加载模组时经 ModInitializer 特性调用。</summary>
    public static void Init()
    {
        var harmony = new Harmony("sts2.alonefight");
        harmony.PatchAll(typeof(Entry).Assembly);

        // 让游戏发现模组程序集中继承 Godot 类型的脚本类
        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);

        // 全局异常捕获写入按局日志（%APPDATA%\SlayTheSpire2\AloneFight\logs）
        AloneFightLog.RegisterExceptionHooks();

        AloneFightLog.Info("[AloneFight] 模组已初始化 —— 一名玩家操控 2~4 名英雄。");
    }
}
