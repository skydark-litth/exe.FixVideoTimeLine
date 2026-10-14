using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FixVideoTimeLine
{
    /// <summary>弹出窗口文案构造（纯函数，便于回归测试与复用）。
    ///
    /// 统一规则：所有弹窗一律只给「条数 + 输出方式 + 输出位置」的汇总，
    /// **绝不罗列文件清单** —— 列表过长(几十上百行)会把下方的警示/结论文字挤出视野，
    /// 用户反而看不到真正要读的内容。完整清单只写日志文件。
    ///
    /// 受众分离：GUI 弹窗 = 最终用户（简明扼要）；log 文件 = 排查者（全量明细）。</summary>
    internal static class DialogText
    {
        /// <summary>输出方式的中文说明。</summary>
        public static string ModeLabel(OutputMode mode)
        {
            if (mode == OutputMode.Overwrite) return "覆盖原文件（不可恢复）";
            if (mode == OutputMode.SameDirectory) return "与源文件同目录（文件名加 _genpts）";
            return "输出到 genpts 子目录";
        }

        /// <summary>输出位置的简短描述：只说明规则，必要时给一个示例目录，不罗列文件。</summary>
        public static string PlaceHint(List<string> files, OutputMode mode)
        {
            if (mode == OutputMode.Overwrite)
                return "输出位置：原文件所在位置，文件名不变（内容被修复结果替换）。";

            string tail = mode == OutputMode.SameDirectory
                ? "与各源文件同目录，主文件名后加 _genpts。"
                : "在源文件所在目录下新建 genpts 子目录，文件名与源文件相同。";

            string common = CommonDirectory(files);
            if (common == null) return "输出位置：" + tail;

            string dir = EllipsizePath(common, 46);
            if (mode == OutputMode.SubDirectory) dir = dir.TrimEnd('\\', '/') + "\\" + AppConfig.OutSubDir;
            return "输出位置：" + tail + "\r\n示例目录：" + dir;
        }

        /// <summary>所有文件是否同处一个目录；是则返回该目录，否则返回 null。</summary>
        public static string CommonDirectory(List<string> files)
        {
            if (files == null || files.Count == 0) return null;
            string common = null;
            for (int i = 0; i < files.Count; i++)
            {
                string d;
                try { d = Path.GetDirectoryName(files[i]); }
                catch { d = null; }
                if (string.IsNullOrEmpty(d)) return null;
                if (i == 0) common = d;
                else if (!string.Equals(common, d, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return common;
        }

        /// <summary>按字符数中间省略，保留首尾便于识别（弹窗里不出现过长路径）。</summary>
        public static string EllipsizePath(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || maxLen <= 0 || s.Length <= maxLen) return s;
            int keep = maxLen - 3;
            if (keep <= 0) return s.Substring(0, maxLen);
            int head = keep / 2;
            int tail = keep - head;
            return s.Substring(0, head) + "..." + s.Substring(s.Length - tail);
        }

        /// <summary>开始前的确认弹窗正文（覆盖模式）。</summary>
        public static string ConfirmOverwrite(List<string> files, OutputMode mode)
        {
            int n = files == null ? 0 : files.Count;
            return string.Format(CultureInfo.InvariantCulture,
                "即将处理 {0} 个视频文件。\r\n输出方式：{1}\r\n{2}\r\n\r\n" +
                "【警告】修复成功后会用结果直接替换原文件，文件名与位置不变，此操作不可恢复！\r\n" +
                "程序会先写临时文件、确认修复成功后才替换；失败或停止时原文件不动。\r\n" +
                "完整文件清单将记录在日志文件中。\r\n\r\n确定要覆盖原文件吗？",
                n, ModeLabel(mode), PlaceHint(files, mode));
        }

        /// <summary>开始前的确认弹窗正文（不改动源文件的两种模式）。</summary>
        public static string ConfirmRun(List<string> files, OutputMode mode)
        {
            int n = files == null ? 0 : files.Count;
            return string.Format(CultureInfo.InvariantCulture,
                "即将处理 {0} 个视频文件。\r\n输出方式：{1}\r\n{2}\r\n\r\n" +
                "完整文件清单将记录在日志文件中。\r\n\r\n源文件不会被修改。是否开始？",
                n, ModeLabel(mode), PlaceHint(files, mode));
        }

        /// <summary>覆盖模式的第二次确认。</summary>
        public static string ConfirmSecond(int count)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "请再次确认：将直接覆盖 {0} 个原视频文件，覆盖后无法找回原文件。\r\n" +
                "建议重要文件先自行备份。\r\n\r\n真的要继续吗？", count);
        }

        /// <summary>全部成功的结束汇总（只给条数与输出目录，不罗列文件）。</summary>
        public static string SummaryOk(int okCount, int degradedCount, int skipCount,
            string outputDir, string logPath)
        {
            string msg = string.Format(CultureInfo.InvariantCulture, "处理完成：成功 {0} 个。", okCount);
            if (degradedCount > 0)
                msg += string.Format(CultureInfo.InvariantCulture, "（其中 {0} 个为增强失败后降级完成）", degradedCount);
            if (skipCount > 0)
                msg += string.Format(CultureInfo.InvariantCulture, "跳过 {0} 个。", skipCount);
            if (!string.IsNullOrEmpty(outputDir))
                msg += "\r\n\r\n输出位置：\r\n" + EllipsizePath(outputDir, 68);
            msg += "\r\n完整清单见日志文件：\r\n" + logPath;
            return msg;
        }

        /// <summary>有失败时的结束汇总（按原因给条数，不罗列文件）。</summary>
        public static string SummaryFail(string head, Dictionary<FailureKind, int> byKind, string logPath)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(head)) sb.Append(head);
            if (byKind != null && byKind.Count > 0)
            {
                FailureKind[] order = FailureClassifier.AllKinds();
                for (int i = 0; i < order.Length; i++)
                {
                    int c;
                    if (!byKind.TryGetValue(order[i], out c) || c <= 0) continue;
                    sb.Append("\r\n　因");
                    sb.Append(FailureClassifier.Describe(order[i]));
                    sb.Append("失败 ");
                    sb.Append(c.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" 个");
                }
            }
            sb.Append("\r\n\r\n完整清单见日志文件：\r\n");
            sb.Append(logPath);
            return sb.ToString();
        }

        /// <summary>队列结束时写进「执行过程展示框」的汇总行（第 0 项为标题，其余为明细）。
        /// 最终用户只看这个框，结果必须在这里出现，不能只写状态栏。</summary>
        public static string[] SummaryLines(int okCount, int failCount, int skippedTotal,
            int degradedCount, int spaceSkipCount, bool stopped, int pendingCount)
        {
            var lines = new List<string>();
            if (stopped)
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "已停止：成功 {0} · 失败 {1} · 跳过 {2}", okCount, failCount, skippedTotal));
                if (pendingCount > 0)
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "    未处理 {0} 个", pendingCount));
            }
            else
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "处理结束：成功 {0} · 失败 {1} · 跳过 {2}", okCount, failCount, skippedTotal));
                if (degradedCount > 0)
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "    其中 {0} 个为增强失败后降级完成", degradedCount));
                if (spaceSkipCount > 0)
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "    跳过中有 {0} 个因磁盘空间不足", spaceSkipCount));
            }
            return lines.ToArray();
        }
    }
}
