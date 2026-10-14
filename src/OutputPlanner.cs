using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FixVideoTimeLine
{
    /// <summary>
    /// 输出方式规划：按所选模式计算最终输出路径、覆盖模式的临时工作文件，
    /// 并识别「本程序生成的成品 / 临时文件」，供扫描输入时排除，避免套娃。
    /// SubDirectory / SameDirectory 两种模式不改动源文件；
    /// Overwrite 模式先把临时文件写到「运行目录\temp」根下（纯平铺，无子目录），
    /// 修复成功后才用它替换原文件。
    /// 每个文件完成后只删临时文件；整个队列处理完毕由 CleanupTempRoot 一次性清理 temp。
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
        /// 覆盖模式：写到「运行目录\temp」根下，文件名为**完整源路径的 32 位 MD5**（小写十六进制），
        /// 保持原扩展名以保证容器一致。如 X:\clips\sub\a.mp4 -&gt; &lt;运行目录&gt;\temp\2a1f...4390.mp4。
        /// 用完整路径而非仅文件名做输入，避免不同目录下的同名文件算出一致的临时名；
        /// 定长 32 字符也让临时路径彻底摆脱 Windows 260 字符上限的困扰。
        /// 其余模式：工作文件即最终文件（子目录模式会确保 genpts 目录存在）。
        /// </summary>
        public static string WorkingPath(string inputFile, OutputMode mode)
        {
            if (mode != OutputMode.Overwrite)
            {
                return FinalPath(inputFile, mode);
            }

            string root = AppConfig.TempRoot;
            Directory.CreateDirectory(root);

            string name = TempFileName(inputFile);
            string cand = Path.Combine(root, name);
            if (!File.Exists(cand)) return cand;

            // 极端情况（上次崩溃残留同名临时文件）：加序号避让
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            for (int i = 1; i < 100; i++)
            {
                cand = Path.Combine(root, stem + "_" + i.ToString() + ext);
                if (!File.Exists(cand)) return cand;
            }
            return Path.Combine(root, stem + "_" + Guid.NewGuid().ToString("N") + ext);
        }

        /// <summary>
        /// 临时文件名：完整源路径的 32 位 MD5 + 原扩展名。
        /// 编码固定为 UTF-8（不随系统区域设置变化），保证同一源文件在任何机器上得到同一名字。
        /// </summary>
        public static string TempFileName(string inputFile)
        {
            string full;
            try { full = Path.GetFullPath(inputFile); }
            catch { full = inputFile ?? string.Empty; }

            string hash;
            try
            {
                using (var md5 = MD5.Create())
                {
                    byte[] data = md5.ComputeHash(Encoding.UTF8.GetBytes(full));
                    var sb = new StringBuilder(32);
                    for (int i = 0; i < data.Length; i++)
                        sb.Append(data[i].ToString("x2"));
                    hash = sb.ToString();
                }
            }
            catch
            {
                // 理论上不会失败；兜底用进程内唯一名，保证不覆盖别的东西
                hash = Guid.NewGuid().ToString("N");
            }

            string ext = string.Empty;
            try { ext = Path.GetExtension(full); } catch { }
            return hash + ext;
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

        /// <summary>
        /// 是否为覆盖模式的临时工作文件：位于 temp 目录内，
        /// 或主文件名为 32 位十六进制（新命名）、含旧临时标记（历史残留）。
        /// </summary>
        public static bool IsWorkingFile(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            if (InputScanner.IsUnderTempDir(fullPath)) return true;
            try
            {
                string stem = Path.GetFileNameWithoutExtension(fullPath);
                if (string.IsNullOrEmpty(stem)) return false;
                if (stem.IndexOf(AppConfig.WorkingMark, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return IsMd5Name(stem);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>是否为 32 位小写十六进制字符串（临时文件命名格式）。</summary>
        private static bool IsMd5Name(string stem)
        {
            if (stem.Length != 32) return false;
            for (int i = 0; i < stem.Length; i++)
            {
                char c = stem[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        /// <summary>
        /// 覆盖模式提交：用修复好的临时文件替换原文件。优先 File.Replace（同卷原子替换），
        /// 失败（含跨卷）时退回「改名备份 → 移动 → 删备份」，移动失败尽力还原备份，确保源文件不丢。
        /// 只读处理：替换**前**清除双方只读位（只读目标会让 File.Replace 抛
        /// UnauthorizedAccessException），替换**成功后**仅当源文件原本就是只读时还原只读位
        /// ——原本不是只读的文件绝不主动添加只读属性。
        /// 成功后只删除临时文件（File.Replace 已消费），整个队列跑完由 CleanupTempRoot 清理 temp。
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

            // 记录源文件原有属性。File.Replace 会保留创建时间与 Hidden，但**丢掉 ReadOnly**，
            // 所以只读位需要在替换成功后手动还原。
            bool srcWasReadOnly = false;
            try
            {
                FileAttributes attr = File.GetAttributes(inputFile);
                srcWasReadOnly = (attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            }
            catch { }

            ClearReadOnly(workingFile);
            // 只读的源文件会让 File.Replace 直接抛 UnauthorizedAccessException（此前未被捕获，
            // 异常穿透导致替换失败），因此必须提前清位。
            ClearReadOnly(inputFile);

            try
            {
                // File.Replace：destination(原文件) 被 source(临时文件) 内容替换，不保留备份（第3参 null）。
                // 同卷跨目录可用；跨卷 / 不支持的文件系统会抛异常，走下面兜底。
                File.Replace(workingFile, inputFile, null);
                RestoreReadOnly(inputFile, srcWasReadOnly);
                return;
            }
            catch (PlatformNotSupportedException) { }
            catch (NotSupportedException) { }
            catch (IOException)
            {
                // 某些卷/文件系统不支持 Replace（跨卷、FAT、网络盘等），走下面的非原子兜底
            }

            // ---- 非原子兜底：备份原文件 → 移入临时文件 → 删除备份 ----
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
            RestoreReadOnly(inputFile, srcWasReadOnly);
        }

        /// <summary>
        /// 丢弃本次临时工作文件（修复失败 / 用户停止时调用），源文件保持不动。
        /// 只删临时文件；整个队列跑完由 CleanupTempRoot 一次性清理 temp。
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
        /// 队列全部处理完毕后一次性清理：删除 temp 内残留的临时文件（新旧命名都认），
        /// 并自底向上删除因此变空的目录（兼容历史版本留下的镜像子目录），直到 temp 根。
        /// 只动本程序运行目录下的 temp，不影响其它文件。
        /// </summary>
        public static void CleanupTempRoot()
        {
            string root = AppConfig.TempRoot;
            if (!Directory.Exists(root)) return;
            PurgeTempFiles(root);
            RemoveEmptyDirsDeep(root);
            TryRemoveDir(root);
        }

        /// <summary>
        /// 清理上次异常退出可能残留的临时文件（仅限 temp 目录内的临时文件），
        /// 再删除因此变空的目录。每次开始修复前调用，幂等、只动本程序自己的 temp。
        /// </summary>
        public static void SweepStaleTemp()
        {
            string root = AppConfig.TempRoot;
            if (!Directory.Exists(root)) return;
            PurgeTempFiles(root);
            RemoveEmptyDirsDeep(root);
            TryRemoveDir(root);
        }

        /// <summary>
        /// 删除 temp 下的临时文件：主名为 32 位十六进制（当前命名）或含旧临时标记（历史残留）。
        /// 其它文件一律保留，避免误删用户放进来的东西。
        /// </summary>
        private static void PurgeTempFiles(string root)
        {
            string[] files;
            try { files = Directory.GetFiles(root, "*", SearchOption.AllDirectories); }
            catch { return; }

            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    string stem = Path.GetFileNameWithoutExtension(files[i]);
                    if (string.IsNullOrEmpty(stem)) continue;

                    // 当前命名：32 位 hex 主名 + 视频扩展名；
                    // 历史命名：主名含 .__genpts_tmp_ 标记。其它文件一律保留。
                    bool isTemp = (IsMd5Name(stem) && IsVideoExtName(files[i]))
                        || stem.IndexOf(AppConfig.WorkingMark, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isTemp) continue;

                    ClearReadOnly(files[i]);
                    File.Delete(files[i]);
                }
                catch { }
            }
        }

        private static bool IsVideoExtName(string path)
        {
            try { return AppConfig.IsVideoExt(path); }
            catch { return false; }
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

        /// <summary>仅当 originallyReadOnly 为 true 时还原只读位；否则保持不动。</summary>
        private static void RestoreReadOnly(string path, bool originallyReadOnly)
        {
            if (!originallyReadOnly) return;
            try
            {
                var attr = File.GetAttributes(path);
                if ((attr & FileAttributes.ReadOnly) != FileAttributes.ReadOnly)
                {
                    File.SetAttributes(path, attr | FileAttributes.ReadOnly);
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
