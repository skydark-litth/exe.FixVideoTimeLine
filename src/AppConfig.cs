using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace FixVideoTimeLine
{
    /// <summary>输出方式。</summary>
    internal enum OutputMode
    {
        /// <summary>输出到源目录下的 genpts 子目录，源文件不动（默认、推荐）。</summary>
        SubDirectory,

        /// <summary>输出到源文件所在的同一目录，主文件名追加 _genpts 后缀，源文件不动。</summary>
        SameDirectory,

        /// <summary>先写同目录临时文件，成功后原子替换原文件（破坏性，不可恢复）。</summary>
        Overwrite
    }

    /// <summary>全局配置：路径、扩展名、下载来源等常量。</summary>
    internal static class AppConfig
    {
        public const string AppName = "修复视频时间轴";
        public const string AppVer = "2.7";

        /// <summary>输出子目录名：&lt;源目录&gt;\genpts\&lt;原文件名&gt;</summary>
        public const string OutSubDir = "genpts";

        /// <summary>同目录输出时追加到主文件名的后缀：a.mp4 -&gt; a_genpts.mp4。</summary>
        public const string OutFileSuffix = "_genpts";

        /// <summary>覆盖模式临时文件标记：&lt;主名&gt;.__genpts_tmp_&lt;8位&gt;.&lt;扩展名&gt;。</summary>
        public const string WorkingMark = ".__genpts_tmp_";

        /// <summary>临时文件统一根目录名（位于运行目录下）：&lt;运行目录&gt;\temp\。</summary>
        public const string TempDirName = "temp";

        /// <summary>认定为视频的扩展名（小写，含点）。</summary>
        public static readonly string[] VideoExt = new string[]
        {
            ".mp4", ".mkv", ".mov", ".avi", ".flv", ".wmv", ".m4v", ".ts", ".mts",
            ".m2ts", ".mpg", ".mpeg", ".rmvb", ".rm", ".webm", ".3gp", ".vob", ".asf"
        };

        /// <summary>修复策略固定：先增强，失败自动降级基础。</summary>
        public static readonly string[] FixModes = new string[] { "Strong", "Basic" };

        // ---- 下载来源 ----
        public const string GithubRepo = "BtbN/FFmpeg-Builds";
        public const string GyanDevZip = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        public const string FfmpegOrgPage = "https://ffmpeg.org/download.html";

        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

        /// <summary>exe 所在目录（部署根目录）。</summary>
        public static string Root
        {
            get
            {
                string p = null;
                try
                {
                    Assembly asm = Assembly.GetEntryAssembly();
                    if (asm != null && !string.IsNullOrEmpty(asm.Location))
                    {
                        p = Path.GetDirectoryName(asm.Location);
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(p))
                {
                    p = AppDomain.CurrentDomain.BaseDirectory;
                }
                return p;
            }
        }

        public static string ToolsDir { get { return Path.Combine(Root, "tools"); } }
        public static string FfmpegDir { get { return Path.Combine(ToolsDir, "ffmpeg"); } }
        public static string LogDir { get { return Path.Combine(Root, "log"); } }

        /// <summary>临时文件统一根目录：&lt;运行目录&gt;\temp。</summary>
        public static string TempRoot { get { return Path.Combine(Root, TempDirName); } }

        public static bool IsVideoExt(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.ToLowerInvariant();
            for (int i = 0; i < VideoExt.Length; i++)
            {
                if (VideoExt[i] == ext) return true;
            }
            return false;
        }
    }
}
