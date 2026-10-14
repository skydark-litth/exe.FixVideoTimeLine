using System;
using System.Collections.Generic;
using System.IO;

namespace FixVideoTimeLine
{
    internal class ScanResult
    {
        public List<string> Files = new List<string>();
        public List<string> Ignored = new List<string>();
        /// <summary>递归扫描时因无权限 / IO 错误而跳过的目录数（供界面提示，不影响其它目录）。</summary>
        public int SkippedDirs = 0;
    }

    /// <summary>输入收集与输出路径计算。</summary>
    internal static class InputScanner
    {
        /// <summary>把文件/文件夹混合清单展开成视频文件清单（去重、排序）。</summary>
        public static ScanResult Expand(IEnumerable<string> paths, bool includeSub)
        {
            return Expand(paths, includeSub, null, null);
        }

        /// <summary>
        /// 把文件/文件夹混合清单展开成视频文件清单（去重、排序）。
        /// 目录遍历为容错的逐目录递归：单个子目录无权限 / IO 异常只跳过该目录，不影响其余结果。
        /// </summary>
        /// <param name="onDir">递归时每进入一个目录回调一次（用于后台显示"正在扫描…"），可为 null。</param>
        /// <param name="shouldStop">返回 true 时提前中止扫描，可为 null。</param>
        public static ScanResult Expand(IEnumerable<string> paths, bool includeSub,
            Action<string> onDir, Func<bool> shouldStop)
        {
            var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var result = new ScanResult();
            var collected = new List<string>();

            foreach (string raw in paths)
            {
                if (shouldStop != null && shouldStop()) break;
                if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0) continue;
                string p = raw.Trim().Trim('"');
                if (p.Length == 0) continue;

                if (File.Exists(p))
                {
                    // 显式指定的文件一律尊重（即便名字恰以 _genpts / 临时标记结尾），仅按扩展名判断
                    if (AppConfig.IsVideoExt(p)) collected.Add(SafeFullPath(p));
                    else result.Ignored.Add(p);
                }
                else if (Directory.Exists(p))
                {
                    if (includeSub)
                    {
                        EnumerateVideosRecursive(p, collected, result, onDir, shouldStop);
                    }
                    else
                    {
                        EnumerateVideosTopOnly(p, collected);
                    }
                }
                else
                {
                    result.Ignored.Add(p);
                }
            }

            // 去重并排序
            collected.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < collected.Count; i++)
            {
                string key = collected[i];
                if (seen.ContainsKey(key)) continue;
                seen[key] = true;
                result.Files.Add(key);
            }
            return result;
        }

        /// <summary>仅枚举目录本层（不递归），应用排除规则。</summary>
        private static void EnumerateVideosTopOnly(string dir, List<string> collected)
        {
            string[] items;
            try { items = Directory.GetFiles(dir); }
            catch { return; }

            for (int i = 0; i < items.Length; i++)
            {
                string full = items[i];
                if (IsExcluded(full)) continue;
                if (AppConfig.IsVideoExt(full)) collected.Add(full);
            }
        }

        /// <summary>
        /// 容错递归：用显式栈逐目录遍历，进入每个目录前回调进度。
        /// 任一目录列文件 / 列子目录失败（常见于无权限、重定向、IO 错误）只记 SkippedDirs 并继续，
        /// 不会像 Directory.GetFiles(AllDirectories) 那样因单个坏目录丢失整棵树的结果。
        /// </summary>
        private static void EnumerateVideosRecursive(string root, List<string> collected,
            ScanResult result, Action<string> onDir, Func<bool> shouldStop)
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                if (shouldStop != null && shouldStop()) return;

                string dir = stack.Pop();
                if (onDir != null)
                {
                    try { onDir(dir); } catch { }
                }

                string[] files = null;
                string[] subDirs = null;
                bool dirOk = true;
                try { files = Directory.GetFiles(dir); }
                catch { dirOk = false; }
                try { subDirs = Directory.GetDirectories(dir); }
                catch { dirOk = false; }

                if (!dirOk)
                {
                    result.SkippedDirs++;
                    continue;
                }

                if (files != null)
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        string full = files[i];
                        if (IsExcluded(full)) continue;
                        if (AppConfig.IsVideoExt(full)) collected.Add(full);
                    }
                }

                if (subDirs != null)
                {
                    for (int i = 0; i < subDirs.Length; i++)
                    {
                        // 跳过联接点 / 符号链接，避免环导致的无限递归
                        if (IsReparsePoint(subDirs[i])) continue;
                        stack.Push(subDirs[i]);
                    }
                }
            }
        }

        /// <summary>扫描文件夹时是否应排除该文件：成品目录 / temp / 成品文件 / 临时文件。</summary>
        private static bool IsExcluded(string full)
        {
            if (IsUnderTempDir(full)) return true;
            if (OutputPlanner.IsGeneratedFile(full)) return true;
            if (OutputPlanner.IsWorkingFile(full)) return true;
            return false;
        }

        private static bool IsReparsePoint(string dir)
        {
            try
            {
                var attr = File.GetAttributes(dir);
                return (attr & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                return true;   // 读不到属性就不进入，稳妥处理
            }
        }

        /// <summary>路径分隔符。刻意不用数组初始化列表（new char[]{...}）：本机工具链生成的 PE 中，
        /// 值类型数组字面量会被放在 .text 节内、紧邻导入 thunk 的数据区，实测存在被加载器 IAT
        /// 修正误写而损坏的情况。改为逐元素赋值构造，语义完全等价。</summary>
        private static readonly char[] PathSeps = MakePathSeps();

        private static char[] MakePathSeps()
        {
            char[] a = new char[2];
            a[0] = '\\';
            a[1] = '/';
            return a;
        }

        /// <summary>路径中是否含有输出子目录名（genpts）。</summary>
        public static bool IsUnderOutputDir(string fullPath)
        {
            string[] parts = fullPath.Split(PathSeps, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], AppConfig.OutSubDir, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>路径是否位于运行目录下的临时根目录（temp）内。按真实路径前缀比较，
        /// 不会误判用户其它盘里同名的 temp 文件夹。</summary>
        public static bool IsUnderTempDir(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            try
            {
                string root = AppConfig.TempRoot.TrimEnd('\\', '/');
                string full = Path.GetFullPath(fullPath).TrimEnd('\\', '/');
                if (full.Length <= root.Length) return false;
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && (full[root.Length] == Path.DirectorySeparatorChar
                        || full[root.Length] == Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>输出规则：&lt;源目录&gt;\genpts\&lt;原文件名&gt;</summary>
        public static string OutputPathFor(string inputFile, bool ensureDir)
        {
            string dir = Path.GetDirectoryName(inputFile);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            string target = Path.Combine(dir, AppConfig.OutSubDir);

            if (ensureDir && !Directory.Exists(target))
            {
                Directory.CreateDirectory(target);
            }
            return Path.Combine(target, Path.GetFileName(inputFile));
        }

        private static string SafeFullPath(string p)
        {
            try { return Path.GetFullPath(p); }
            catch { return p; }
        }
    }
}
