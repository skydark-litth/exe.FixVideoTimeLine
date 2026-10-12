using System;
using System.IO;

namespace FixVideoTimeLine
{
    /// <summary>
    /// 输出方式规划：按所选模式计算最终输出路径、覆盖模式的临时工作文件，
    /// 并识别「本程序生成的成品 / 临时文件」，供扫描输入时排除，避免套娃。
    /// SubDirectory / SameDirectory 两种模式不改动源文件；
    /// Overwrite 模式先把临时文件写到「运行目录\temp」下、按源文件目录镜像建子目录，
    /// 修复成功后才用它替换原文件。
    /// 每个文件完成后只删临时文件、**保留 temp 目录结构**（同一目录多文件时避免反复重建），
    /// 整个队列处理完毕再由 CleanupTempRoot 一次性清理 temp。
    /// </summary>
    internal static class OutputPlanner
    {
        /// <summary>最终成品路径。覆盖模式下即源文件本身（真正写入走 WorkingPath）。</summary>
        public static string FinalPath(string inputFile, OutputMode mode)
        {
            if (mode == OutputMode.SameDirectory)
            {
                return SameDirPath(inputFile, AppConfig.OutFileSuffix);
            }

            if (mode == OutputMode.Overwrite)
            {
                // 最终目标就是源文件本身；ffmpeg 绝不原地写，实际输出见 WorkingPath。
                return inputFile;
            }

            // 子目录模式：<源目录>\genpts\<原文件名>，并确保目录存在
            return InputScanner.OutputPathFor(inputFile, true);
        }

        /// <summary>
        /// ffmpeg 实际写入的工作文件。
        /// 覆盖模式：写到「运行目录\temp」下、按源文件目录镜像的唯一临时文件，保持原扩展名
        /// （容器一致）。如源 X:\clips\sub\a.mp4 -&gt; &lt;运行目录&gt;\temp\X\clips\sub\a.__genpts_tmp_3f9a1c2e.mp4。
        /// 其余模式：工作文件即最终文件（子目录模式会确保 genpts 目录存在）。
        /// </summary>
        public static string WorkingPath(string inputFile, OutputMode mode)
        {
            if (mode != OutputMode.Overwrite)
            {
                return FinalPath(inputFile, mode);
            }

            string mirrorDir = TempMirrorDir(inputFile);
            Directory.CreateDirectory(mirrorDir);

            string stem = Path.GetFileNameWithoutExtension(inputFile);
            string ext = Path.GetExtension(inputFile);

            // 生成镜像目录下不存在的唯一临时文件名，避免并发 / 残留冲突
            for (int i = 0; i < 16; i++)
            {
                string cand = Path.Combine(mirrorDir,
                    stem + AppConfig.WorkingMark + Guid.NewGuid().ToString("N").Substring(0, 8) + ext);
                if (!File.Exists(cand)) return cand;
            }
            // 理论上撞不到；兜底用完整 GUID
            return Path.Combine(mirrorDir,
                stem + AppConfig.WorkingMark + Guid.NewGuid().ToString("N") + ext);
        }

        /// <summary>
        /// 是否为本程序生成的成品（扫描文件夹时应排除）：
        /// 位于任意 genpts 目录内，或主文件名以 _genpts 结尾（同目录输出的成品）。
        /// 注意：用户显式点名的文件不做此排除（见 InputScanner）。
        /// </summary>
        public static bool IsGeneratedFile(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            if (InputScanner.IsUnderOutputDir(fullPath)) return true;
            try
            {
                string stem = Path.GetFileNameWithoutExtension(fullPath);
                return !string.IsNullOrEmpty(stem)
                    && stem.EndsWith(AppConfig.OutFileSuffix, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>是否为覆盖模式的临时工作文件（位于 temp 镜像目录，或文件名含临时标记）。</summary>
        public static bool IsWorkingFile(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            if (InputScanner.IsUnderTempDir(fullPath)) return true;
            try
            {
                string stem = Path.GetFileNameWithoutExtension(fullPath);
                return !string.IsNullOrEmpty(stem)
                    && stem.IndexOf(AppConfig.WorkingMark, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 覆盖模式提交：用修复好的临时文件替换原文件。优先 File.Replace（同卷原子替换），
        /// 失败（含跨卷）时退回「改名备份 → 移动 → 删备份」，移动失败尽力还原备份，确保源文件不丢。
        /// 成功后只删除临时文件，**保留 temp 镜像目录结构**（同一目录下多文件时不反复建目录）；
        /// 整个队列跑完由 CleanupTempRoot 一次性清理。
        /// </summary>
        public static void Commit(string inputFile, string workingFile)
        {
            if (!File.Exists(workingFile))
            {
                throw new FileNotFoundException("修复结果临时文件不存在，无法替换原文件：" + workingFile);
            }
            if (!File.Exists(inputFile))
            {
                throw new FileNotFoundException("原文件不存在，无法替换：" + inputFile);
            }

            ClearReadOnly(workingFile);

            try
            {
                // File.Replace：destination(原文件) 被 source(临时文件) 内容替换，不保留备份（第3参 null）。
                // 同卷跨目录可用；跨卷 / 不支持的文件系统会抛异常，走下面兜底。
                File.Replace(workingFile, inputFile, null);
                return;
            }
            catch (PlatformNotSupportedException) { }
            catch (NotSupportedException) { }
            catch (IOException)
            {
                // 某些卷/文件系统不支持 Replace（跨卷、FAT、网络盘等），走下面的非原子兜底
            }

            // ---- 非原子兜底：备份原文件 → 移入临时文件 → 删除备份 ----
            ClearReadOnly(inputFile);
            string backup = inputFile + AppConfig.OutFileSuffix + ".bak";
            int n = 1;
            while (File.Exists(backup))
            {
                backup = inputFile + AppConfig.OutFileSuffix + n + ".bak";
                n++;
            }

            File.Move(inputFile, backup);
            try
            {
                File.Move(workingFile, inputFile);
            }
            catch
            {
                // 新文件没能就位：尽力把备份还原回原路径，保证源文件不丢
                try { if (!File.Exists(inputFile)) File.Move(backup, inputFile); }
                catch { }
                throw;
            }

            TryDelete(backup);
        }

        /// <summary>
        /// 丢弃本次临时工作文件（修复失败 / 用户停止时调用），源文件保持不动。
        /// 只删临时文件，**保留 temp 镜像目录结构**（供后续文件复用，避免反复重建）；
        /// 整个队列跑完由 CleanupTempRoot 一次性清理。
        /// 仅覆盖模式有独立临时文件；其余模式工作文件即成品，不在此删除。
        /// </summary>
        public static void DiscardWorking(string workingFile, string finalPath, OutputMode mode)
        {
            if (mode != OutputMode.Overwrite) return;
            if (string.IsNullOrEmpty(workingFile)) return;
            // 绝不允许误删到源文件 / 最终成品
            if (string.Equals(workingFile, finalPath, StringComparison.OrdinalIgnoreCase)) return;
            if (!IsWorkingFile(workingFile)) return;
            TryDelete(workingFile);
        }

        /// <summary>
        /// 队列全部处理完毕后一次性清理：删除 temp 内所有残留的临时文件，并自底向上删除因此变空的
        /// 镜像目录，直到 temp 根也被删除。只动本程序运行目录下的 temp，不影响其它文件。
        /// </summary>
        public static void CleanupTempRoot()
        {
            string root = AppConfig.TempRoot;
            if (!Directory.Exists(root)) return;

            try
            {
                string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        string stem = Path.GetFileNameWithoutExtension(files[i]);
                        if (!string.IsNullOrEmpty(stem)
                            && stem.IndexOf(AppConfig.WorkingMark, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            ClearReadOnly(files[i]);
                            File.Delete(files[i]);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            RemoveEmptyDirsDeep(root);
            TryRemoveDir(root);
        }

        /// <summary>
        /// 清理上次异常退出可能残留的临时文件（仅限 temp 目录内、文件名带临时标记者），
        /// 再删除因此变空的镜像目录。每次开始修复前调用，幂等、只动本程序自己的 temp。
        /// </summary>
        public static void SweepStaleTemp()
        {
            string root = AppConfig.TempRoot;
            if (!Directory.Exists(root)) return;
            try
            {
                string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        string stem = Path.GetFileNameWithoutExtension(files[i]);
                        if (!string.IsNullOrEmpty(stem)
                            && stem.IndexOf(AppConfig.WorkingMark, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            ClearReadOnly(files[i]);
                            File.Delete(files[i]);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // 反复自底向上删除空目录，直到 temp 根本身
            RemoveEmptyDirsDeep(root);
            TryRemoveDir(root);
        }

        // ---------------- 临时目录布局 ----------------

        /// <summary>
        /// 计算源文件在 temp 下的镜像目录：&lt;运行目录&gt;\temp\&lt;盘符或共享名&gt;\&lt;源相对目录&gt;。
        /// 例：X:\clips\sub\a.mp4 -&gt; &lt;root&gt;\temp\X\clips\sub；
        /// \\server\share\dir\a.mp4 -&gt; &lt;root&gt;\temp\server_share\dir。
        /// </summary>
        private static string TempMirrorDir(string inputFile)
        {
            string full = Path.GetFullPath(inputFile);
            string root = AppConfig.TempRoot;
            string pathRoot = Path.GetPathRoot(full) ?? string.Empty;   // 如 "X:\" 或 "\\server\share"
            string top = SanitizeTop(pathRoot);
            string dir = Path.GetDirectoryName(full) ?? string.Empty;

            string rel;
            if (!string.IsNullOrEmpty(pathRoot) && dir.Length >= pathRoot.Length)
            {
                rel = dir.Substring(pathRoot.Length).TrimStart('\\', '/');
            }
            else
            {
                rel = dir.TrimStart('\\', '/');
            }

            return string.IsNullOrEmpty(rel)
                ? Path.Combine(root, top)
                : Path.Combine(root, top, rel);
        }

        /// <summary>把盘符根 / UNC 根转成可做一级目录名的安全短名。</summary>
        private static string SanitizeTop(string pathRoot)
        {
            string r = (pathRoot ?? string.Empty).Trim('\\', '/');
            if (r.Length == 0) return "_";
            // 盘符：X:\ -> X
            if (r.Length == 2 && r[1] == ':')
            {
                char c = char.ToUpperInvariant(r[0]);
                return (c >= 'A' && c <= 'Z') ? c.ToString() : "_";
            }
            // UNC：server\share -> server_share，并去掉非法文件名字符
            foreach (char bad in Path.GetInvalidFileNameChars())
            {
                r = r.Replace(bad, '_');
            }
            r = r.Replace('\\', '_').Replace('/', '_').Replace(':', '_');
            return r.Length == 0 ? "_" : r;
        }

        // ---------------- 内部工具 ----------------

        /// <summary>递归删除空目录（保留含文件或非空子目录者）。</summary>
        private static void RemoveEmptyDirsDeep(string dir)
        {
            string[] sub;
            try { sub = Directory.GetDirectories(dir); }
            catch { return; }
            for (int i = 0; i < sub.Length; i++)
            {
                RemoveEmptyDirsDeep(sub[i]);
                TryRemoveDir(sub[i]);
            }
        }

        private static bool TryRemoveDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, false);   // 仅当目录为空才成功
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static string SameDirPath(string inputFile, string suffix)
        {
            string dir = Path.GetDirectoryName(inputFile);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            string stem = Path.GetFileNameWithoutExtension(inputFile);
            string ext = Path.GetExtension(inputFile);
            return Path.Combine(dir, stem + suffix + ext);
        }

        private static void ClearReadOnly(string path)
        {
            try
            {
                var attr = File.GetAttributes(path);
                if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
                }
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
