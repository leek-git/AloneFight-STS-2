using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace AloneFight;

/// <summary>
/// 模组独立的按局文件日志：
///   • 每开始一局多人跑团生成一个 run_yyyyMMdd_HHmmss.log；
///   • 只保留最近两局（可在反馈问题时直接把文件发给开发者）；
///   • 同时镜像到游戏自带日志（godot.log），Debug 级仅写入模组文件；
///   • 挂载全局异常钩子，捕获模组代码相关的一次异常、未观察任务异常与
///     致命未处理异常，避免用户侧问题只有“没反应”而没有线索。
///
/// 文件位置：%APPDATA%\SlayTheSpire2\AloneFight\logs\
/// </summary>
public static class AloneFightLog
{
    public const string ModVersion = "0.2.0";

    private const int KeepRunCount = 2;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string? _currentPath;
    private static bool _hooksRegistered;

    // FirstChance 异常去重：同一“类型+消息”每局只记录一次，避免刷屏
    private static readonly HashSet<string> SeenFirstChance = new();

    private static string LogsDir => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "SlayTheSpire2", "AloneFight", "logs");

    /// <summary>一局跑团开始时打开新的日志文件并写入头部信息。</summary>
    public static void BeginRun(RunState runState)
    {
        lock (Gate)
        {
            try
            {
                EndRunLocked();

                Directory.CreateDirectory(LogsDir);
                PruneOldLogs();

                string path = Path.Combine(LogsDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                _writer = new StreamWriter(new FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read))
                {
                    AutoFlush = true
                };
                _currentPath = path;
                SeenFirstChance.Clear();

                WriteLocked("INFO", "════════════════════════════════════════════════════");
                WriteLocked("INFO", $"AloneFight {ModVersion} 跑团日志开始");
                WriteLocked("INFO", $"时间(本地)：{DateTime.Now:yyyy-MM-dd HH:mm:ss}  时区：{TimeZoneInfo.Local.StandardName}");
                string godotVersion = "?";
                if (Engine.GetVersionInfo().TryGetValue("string", out var gv))
                    godotVersion = gv.AsString();
                WriteLocked("INFO", $"Godot：{godotVersion}；OS：{OS.GetName()} / {System.Environment.OSVersion.VersionString}");
                WriteLocked("INFO", $"飞升等级：{runState.AscensionLevel}");
                var players = runState.Players.ToList();
                WriteLocked("INFO", $"英雄({players.Count})：{string.Join(", ", players.Select(p => $"NetId={p.NetId}/{p.Character.Id.Entry}"))}");
                WriteLocked("INFO", "════════════════════════════════════════════════════");

                // 同步在游戏日志里留下文件路径，方便快速定位
                Log.Info($"[AloneFight] 本局日志文件：{path}");
            }
            catch (Exception e)
            {
                // 日志本身永远不能影响游戏流程
                TryGameLogError("打开模组日志文件失败", e);
                _writer = null;
            }
        }
    }

    /// <summary>跑团结束（回主菜单/重开）时写入尾部并关闭文件。</summary>
    public static void EndRun()
    {
        lock (Gate)
        {
            EndRunLocked();
        }
    }

    private static void EndRunLocked()
    {
        if (_writer == null) return;
        try
        {
            WriteLocked("INFO", $"跑团日志结束：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _writer.Flush();
            _writer.Dispose();
        }
        catch
        {
            // 忽略关闭阶段的 IO 错误
        }
        finally
        {
            _writer = null;
            _currentPath = null;
        }
    }

    /// <summary>删除超出保留数量的旧日志（按修改时间，最新的两局保留）。</summary>
    private static void PruneOldLogs()
    {
        var files = Directory.EnumerateFiles(LogsDir, "run_*.log")
            .Select(f => new FileInfo(f))
            .ToList();

        // 0 字节文件意味着进程在日志头写入前就被结束（强杀/崩溃），没有分析价值，
        // 直接清理且不占用保留名额
        foreach (var empty in files.Where(f => f.Length == 0))
        {
            try { empty.Delete(); }
            catch { /* 忽略占用 */ }
        }

        foreach (var file in files.Where(f => f.Length > 0)
                     .OrderByDescending(f => f.LastWriteTime)
                     .Skip(KeepRunCount))
        {
            try { file.Delete(); }
            catch { /* 文件可能正被其他进程占用，下一局再试 */ }
        }
    }

    public static void Debug(string message)
    {
        Log.Debug(message); // 游戏日志默认不落 Debug，仅在调试级别下可见
        Write("DEBUG", message);
    }

    public static void Info(string message)
    {
        Log.Info(message);
        Write("INFO", message);
    }

    public static void Error(string message)
    {
        Log.Error(message);
        Write("ERROR", message);
    }

    public static void Error(string message, Exception e)
    {
        Log.Error($"{message}: {e}");
        Write("ERROR", $"{message}\n{e}");
    }

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            WriteLocked(level, message);
        }
    }

    private static void WriteLocked(string level, string message)
    {
        if (_writer == null) return;
        try
        {
            _writer.Write($"{DateTime.Now:HH:mm:ss.fff} [{level}] ");
            _writer.WriteLine(message);
            _writer.Flush();
        }
        catch
        {
            // 日志写入失败时静默，绝不影响游戏
        }
    }

    // ── 全局异常钩子（模组初始化时注册一次） ───────────────────────

    /// <summary>注册一次全局异常捕获，把与模组相关的异常写入本局日志。</summary>
    public static void RegisterExceptionHooks()
    {
        if (_hooksRegistered) return;
        _hooksRegistered = true;

        var assembly = Assembly.GetExecutingAssembly();

        // 一次异常：覆盖补丁内被吞掉或被游戏层捕获的异常。仅记录与模组相关的，
        // 按“类型+消息”去重，避免游戏内部被正常处理的异常刷屏。
        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
        {
            try
            {
                var ex = args.Exception;
                bool ours = ex.TargetSite?.Module?.Assembly == assembly
                    || (ex.StackTrace?.Contains("AloneFight.", StringComparison.Ordinal) ?? false);
                if (!ours) return;

                string key = ex.GetType().FullName + "|" + ex.Message;
                lock (Gate)
                {
                    if (!SeenFirstChance.Add(key)) return;
                }
                Write("FIRST-CHANCE", $"模组相关一次异常（去重后记录）：\n{ex}");
            }
            catch
            {
                // 异常处理器内部不能再抛
            }
        };

        // async Task 中未被 await 的异常，延迟到 GC 时触发
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try
            {
                bool ours = args.Exception.InnerExceptions.Any(e =>
                    e.TargetSite?.Module?.Assembly == assembly
                    || (e.StackTrace?.Contains("AloneFight.", StringComparison.Ordinal) ?? false));
                if (ours)
                    Write("UNOBSERVED-TASK", $"未观察的任务异常：\n{args.Exception}");
            }
            catch
            {
                // 忽略
            }
        };

        // 致命未处理异常：立即落盘（进程可能马上退出）
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                if (args.ExceptionObject is Exception ex)
                    Write("FATAL", $"未处理异常（IsTerminating={args.IsTerminating}）：\n{ex}");
            }
            catch
            {
                // 忽略
            }
        };
    }

    private static void TryGameLogError(string message, Exception e)
    {
        try { Log.Error($"[AloneFight] {message}: {e.Message}"); }
        catch { /* 忽略 */ }
    }
}
