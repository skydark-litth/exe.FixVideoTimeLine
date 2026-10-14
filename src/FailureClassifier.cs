using System;
using System.Collections.Generic;

namespace FixVideoTimeLine
{
    /// <summary>
    /// 失败原因分类。数值本身无意义，仅用于分组统计。
    /// 四类基础原因（磁盘空间不足 / 源文件损坏 / 权限不足 / 文件不存在）保留，
    /// 并按 libavutil/error.h 的定义补齐其余错误类别。
    /// </summary>
    internal enum FailureKind
    {
        None = 0,
        DiskFull,            // 磁盘空间不足
        CorruptSource,       // 源文件损坏 / 数据无效 / 截断
        AccessDenied,        // 权限不足（含文件被占用）
        FileNotFound,        // 源文件不存在 / 路径无效
        PathTooLong,         // 路径过长
        CodecUnsupported,    // 编码 / 容器不兼容
        MissingComponent,    // 缺少解复用器 / 解码器 / 流 / 滤镜 / 协议
        FileInUse,           // 文件被占用 / 磁盘 IO 错误
        InvalidArgument,     // 参数无效 / 不支持的选项
        InternalError,       // ffmpeg 内部错误 / 未知错误
        Interrupted,         // 被中断（信号）
        Other                // 未归类
    }

    /// <summary>
    /// 依据 ffmpeg 的 stderr 文本 + 退出码判定失败原因。
    /// 分类策略：**文本优先、退出码兜底**——实测退出码存在场景依赖
    /// （同一「文件不存在」，绝对路径报 Illegal byte sequence 且码 -42，相对路径才是 -2），
    /// 故不能只靠退出码。
    /// 错误码常量用 MKTAG 公式在运行时计算，与 libavutil/error.h 定义逐位一致。
    /// </summary>
    internal static class FailureClassifier
    {
        // ---------------- ffmpeg 错误码（与 libavutil/error.h 一致） ----------------

        /// <summary>FFERRTAG(a,b,c,d) = -(int)MKTAG(a,b,c,d)；MKTAG 按小端拼四字符。</summary>
        private static int Tag(char a, char b, char c, char d)
        {
            uint v = (uint)a
                   | ((uint)b << 8)
                   | ((uint)c << 16)
                   | ((uint)d << 24);
            return unchecked(-(int)v);
        }

        /// <summary>AVERROR(e) = -(e)：POSIX errno 取负。</summary>
        private static int Err(int e) { return -e; }

        // FFERRTAG 系列（0xF8 前缀表示「未找到」类）
        private static readonly int AVERROR_BSF_NOT_FOUND = Tag((char)0xF8, 'B', 'S', 'F');
        private static readonly int AVERROR_BUG = Tag('B', 'U', 'G', '!');
        private static readonly int AVERROR_BUG2 = Tag('B', 'U', 'G', ' ');
        private static readonly int AVERROR_BUFFER_TOO_SMALL = Tag('B', 'U', 'F', 'S');
        private static readonly int AVERROR_DECODER_NOT_FOUND = Tag((char)0xF8, 'D', 'E', 'C');
        private static readonly int AVERROR_DEMUXER_NOT_FOUND = Tag((char)0xF8, 'D', 'E', 'M');
        private static readonly int AVERROR_ENCODER_NOT_FOUND = Tag((char)0xF8, 'E', 'N', 'C');
        private static readonly int AVERROR_EOF = Tag('E', 'O', 'F', ' ');
        private static readonly int AVERROR_EXIT = Tag('E', 'X', 'I', 'T');
        private static readonly int AVERROR_EXTERNAL = Tag('E', 'X', 'T', ' ');
        private static readonly int AVERROR_FILTER_NOT_FOUND = Tag((char)0xF8, 'F', 'I', 'L');
        private static readonly int AVERROR_INVALIDDATA = Tag('I', 'N', 'D', 'A');
        private static readonly int AVERROR_MUXER_NOT_FOUND = Tag((char)0xF8, 'M', 'U', 'X');
        private static readonly int AVERROR_OPTION_NOT_FOUND = Tag((char)0xF8, 'O', 'P', 'T');
        private static readonly int AVERROR_PATCHWELCOME = Tag('P', 'A', 'W', 'E');
        private static readonly int AVERROR_PROTOCOL_NOT_FOUND = Tag((char)0xF8, 'P', 'R', 'O');
        private static readonly int AVERROR_STREAM_NOT_FOUND = Tag((char)0xF8, 'S', 'T', 'R');
        private static readonly int AVERROR_UNKNOWN = Tag('U', 'N', 'K', 'N');
        // 非 FFERRTAG 的固定值（源码直接给出字面量）
        private const int AVERROR_EXPERIMENTAL = -0x2bb2afa8;
        private const int AVERROR_INPUT_CHANGED = -0x636e6701;
        private const int AVERROR_OUTPUT_CHANGED = -0x636e6702;
        // HTTP / RTSP
        private static readonly int AVERROR_HTTP_BAD_REQUEST = Tag((char)0xF8, '4', '0', '0');
        private static readonly int AVERROR_HTTP_UNAUTHORIZED = Tag((char)0xF8, '4', '0', '1');
        private static readonly int AVERROR_HTTP_FORBIDDEN = Tag((char)0xF8, '4', '0', '3');
        private static readonly int AVERROR_HTTP_NOT_FOUND = Tag((char)0xF8, '4', '0', '4');
        private static readonly int AVERROR_HTTP_TOO_MANY_REQUESTS = Tag((char)0xF8, '4', '2', '9');
        private static readonly int AVERROR_HTTP_OTHER_4XX = Tag((char)0xF8, '4', 'X', 'X');
        private static readonly int AVERROR_HTTP_SERVER_ERROR = Tag((char)0xF8, '5', 'X', 'X');

        // POSIX errno 系列（MSVC CRT 取值）
        private const int EPERM = 1, ENOENT = 2, EINTR = 4, EIO = 5, EAGAIN = 11, ENOMEM = 12;
        private const int EACCES = 13, EFAULT = 14, EBUSY = 16, EEXIST = 17, EXDEV = 18;
        private const int ENODEV = 19, ENOTDIR = 20, EISDIR = 21, EINVAL = 22, EFBIG = 27;
        private const int ENOSPC = 28, EROFS = 30, EPIPE = 32, EDOM = 33, ERANGE = 34;
        private const int EDEADLK = 36, ENAMETOOLONG = 38, ENOSYS = 40, ENOTEMPTY = 41;

        // ---------------- 文本规则（优先匹配，顺序即优先级） ----------------

        private class Rule
        {
            public string Pattern;
            public FailureKind Kind;
            public Rule(string pattern, FailureKind kind) { Pattern = pattern; Kind = kind; }
        }

        /// <summary>
        /// 文本模式表。顺序敏感：越具体的放前面，避免被通用词抢先命中。
        /// 全部为 stderr 中真实出现过的片段（含二进制内嵌可搜串）。
        /// </summary>
        private static readonly Rule[] TextRules = new Rule[]
        {
            // —— 磁盘空间不足 ——
            new Rule("No space left on device", FailureKind.DiskFull),
            new Rule("not enough space", FailureKind.DiskFull),
            new Rule("disk full", FailureKind.DiskFull),

            // —— 权限 / 占用 ——
            new Rule("Permission denied", FailureKind.AccessDenied),
            new Rule("Access is denied", FailureKind.AccessDenied),
            new Rule("operation not permitted", FailureKind.AccessDenied),
            new Rule("being used by another process", FailureKind.FileInUse),
            new Rule("Error writing trailer", FailureKind.FileInUse),
            new Rule("Input/output error", FailureKind.FileInUse),
            new Rule("Resource temporarily unavailable", FailureKind.FileInUse),

            // —— 路径过长 ——
            new Rule("Filename too long", FailureKind.PathTooLong),
            new Rule("path too long", FailureKind.PathTooLong),

            // —— 文件不存在 / 路径无效 ——
            new Rule("No such file or directory", FailureKind.FileNotFound),
            new Rule("Illegal byte sequence", FailureKind.FileNotFound),
            new Rule("Could not open file", FailureKind.FileNotFound),
            new Rule("cannot find the path", FailureKind.FileNotFound),
            new Rule("Invalid argument", FailureKind.InvalidArgument),

            // —— 源文件损坏 / 数据无效 ——
            new Rule("Invalid data found when processing input", FailureKind.CorruptSource),
            new Rule("moov atom not found", FailureKind.CorruptSource),
            new Rule("Truncated", FailureKind.CorruptSource),
            new Rule("truncated", FailureKind.CorruptSource),
            new Rule("corrupt", FailureKind.CorruptSource),
            new Rule("Error while decoding", FailureKind.CorruptSource),
            new Rule("End of file", FailureKind.CorruptSource),
            new Rule("header missing", FailureKind.CorruptSource),

            // —— 缺少组件（流 / 解码器 / 解复用器 / 滤镜 / 协议） ——
            new Rule("does not contain any stream", FailureKind.MissingComponent),
            new Rule("matches no streams", FailureKind.MissingComponent),
            new Rule("Stream map", FailureKind.MissingComponent),
            new Rule("Decoder not found", FailureKind.MissingComponent),
            new Rule("Decoder (", FailureKind.MissingComponent),
            new Rule("Demuxer not found", FailureKind.MissingComponent),
            new Rule("Filter not found", FailureKind.MissingComponent),
            new Rule("Protocol not found", FailureKind.MissingComponent),
            new Rule("Bitstream filter not found", FailureKind.MissingComponent),
            new Rule("Option not found", FailureKind.MissingComponent),
            new Rule("Unknown decoder", FailureKind.MissingComponent),
            new Rule("Unknown encoder", FailureKind.MissingComponent),
            new Rule("Unknown input format", FailureKind.MissingComponent),
            new Rule("Unknown output format", FailureKind.MissingComponent),

            // —— 编码 / 容器不兼容 ——
            new Rule("Could not find tag for codec", FailureKind.CodecUnsupported),
            new Rule("Encoder not found", FailureKind.CodecUnsupported),
            new Rule("Muxer not found", FailureKind.CodecUnsupported),
            new Rule("not supported", FailureKind.CodecUnsupported),
            new Rule("unsupported", FailureKind.CodecUnsupported),
            new Rule("not yet implemented", FailureKind.CodecUnsupported),
            new Rule("Tag ", FailureKind.CodecUnsupported),

            // —— 中断 ——
            new Rule("Immediate exit", FailureKind.Interrupted),
            new Rule("Received signal", FailureKind.Interrupted),

            // —— 内部 / 未知 ——
            new Rule("Internal bug", FailureKind.InternalError),
            new Rule("Buffer too small", FailureKind.InternalError),
            new Rule("Unknown error", FailureKind.InternalError),
        };

        /// <summary>
        /// 判定失败原因：先按 stderr 文本匹配（取最先命中的具体规则），
        /// 未命中再按退出码兜底，最后归为 Other。
        /// </summary>
        /// <param name="lines">ffmpeg 的完整输出行（含 stderr 与 stdout 合并后的收集结果）</param>
        /// <param name="exitCode">ffmpeg 退出码（已是负的 AVERROR 或 POSIX 负值）</param>
        public static FailureKind Classify(List<string> lines, int exitCode)
        {
            // 1) 文本优先：整段输出里扫描，命中即返回最具体的规则
            if (lines != null && lines.Count > 0)
            {
                for (int ri = 0; ri < TextRules.Length; ri++)
                {
                    Rule rule = TextRules[ri];
                    for (int li = 0; li < lines.Count; li++)
                    {
                        string s = lines[li];
                        if (!string.IsNullOrEmpty(s)
                            && s.IndexOf(rule.Pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return rule.Kind;
                        }
                    }
                }
            }

            // 2) 退出码兜底
            return FromExitCode(exitCode);
        }

        /// <summary>退出码 → 原因（兜底路径，精度低于文本匹配）。</summary>
        public static FailureKind FromExitCode(int exitCode)
        {
            if (exitCode == 0) return FailureKind.None;

            if (exitCode == AVERROR_INVALIDDATA) return FailureKind.CorruptSource;
            if (exitCode == AVERROR_EOF) return FailureKind.CorruptSource;
            if (exitCode == AVERROR_EXIT) return FailureKind.Interrupted;

            if (exitCode == AVERROR_DECODER_NOT_FOUND) return FailureKind.MissingComponent;
            if (exitCode == AVERROR_DEMUXER_NOT_FOUND) return FailureKind.MissingComponent;
            if (exitCode == AVERROR_STREAM_NOT_FOUND) return FailureKind.MissingComponent;
            if (exitCode == AVERROR_FILTER_NOT_FOUND) return FailureKind.MissingComponent;
            if (exitCode == AVERROR_PROTOCOL_NOT_FOUND) return FailureKind.MissingComponent;
            if (exitCode == AVERROR_BSF_NOT_FOUND) return FailureKind.MissingComponent;
            if (exitCode == AVERROR_OPTION_NOT_FOUND) return FailureKind.InvalidArgument;

            if (exitCode == AVERROR_ENCODER_NOT_FOUND) return FailureKind.CodecUnsupported;
            if (exitCode == AVERROR_MUXER_NOT_FOUND) return FailureKind.CodecUnsupported;
            if (exitCode == AVERROR_PATCHWELCOME) return FailureKind.CodecUnsupported;
            if (exitCode == AVERROR_EXPERIMENTAL) return FailureKind.CodecUnsupported;

            if (exitCode == AVERROR_BUG || exitCode == AVERROR_BUG2) return FailureKind.InternalError;
            if (exitCode == AVERROR_BUFFER_TOO_SMALL) return FailureKind.InternalError;
            if (exitCode == AVERROR_UNKNOWN) return FailureKind.InternalError;
            if (exitCode == AVERROR_EXTERNAL) return FailureKind.InternalError;
            if (exitCode == AVERROR_INPUT_CHANGED) return FailureKind.InternalError;
            if (exitCode == AVERROR_OUTPUT_CHANGED) return FailureKind.InternalError;

            if (exitCode == AVERROR_HTTP_BAD_REQUEST) return FailureKind.Other;
            if (exitCode == AVERROR_HTTP_UNAUTHORIZED) return FailureKind.AccessDenied;
            if (exitCode == AVERROR_HTTP_FORBIDDEN) return FailureKind.AccessDenied;
            if (exitCode == AVERROR_HTTP_NOT_FOUND) return FailureKind.FileNotFound;
            if (exitCode == AVERROR_HTTP_TOO_MANY_REQUESTS) return FailureKind.Other;
            if (exitCode == AVERROR_HTTP_OTHER_4XX) return FailureKind.Other;
            if (exitCode == AVERROR_HTTP_SERVER_ERROR) return FailureKind.Other;

            if (exitCode == Err(ENOSPC) || exitCode == Err(EFBIG)) return FailureKind.DiskFull;
            if (exitCode == Err(EACCES) || exitCode == Err(EPERM)) return FailureKind.AccessDenied;
            if (exitCode == Err(EROFS)) return FailureKind.AccessDenied;
            if (exitCode == Err(ENOENT)) return FailureKind.FileNotFound;
            if (exitCode == Err(ENOTDIR) || exitCode == Err(EISDIR)) return FailureKind.FileNotFound;
            if (exitCode == Err(ENAMETOOLONG)) return FailureKind.PathTooLong;
            if (exitCode == Err(EBUSY) || exitCode == Err(EIO)) return FailureKind.FileInUse;
            if (exitCode == Err(EPIPE)) return FailureKind.FileInUse;
            if (exitCode == Err(EINVAL) || exitCode == Err(ERANGE) || exitCode == Err(EDOM)) return FailureKind.InvalidArgument;
            if (exitCode == Err(EEXIST) || exitCode == Err(EXDEV)) return FailureKind.FileInUse;
            if (exitCode == Err(ENODEV) || exitCode == Err(ENOSYS)) return FailureKind.MissingComponent;
            if (exitCode == Err(ENOTEMPTY)) return FailureKind.FileInUse;
            if (exitCode == Err(EDEADLK)) return FailureKind.FileInUse;
            if (exitCode == Err(ENOMEM)) return FailureKind.InternalError;
            if (exitCode == Err(EFAULT)) return FailureKind.InternalError;
            if (exitCode == Err(EAGAIN)) return FailureKind.FileInUse;
            if (exitCode == Err(EINTR)) return FailureKind.Interrupted;

            // ffmpeg.c 特例：收信号 → 255
            if (exitCode == 255) return FailureKind.Interrupted;

            return FailureKind.Other;
        }

        /// <summary>原因的中文短名（用于分组汇总）。</summary>
        public static string Describe(FailureKind kind)
        {
            switch (kind)
            {
                case FailureKind.DiskFull: return "磁盘空间不足";
                case FailureKind.CorruptSource: return "源文件损坏或数据无效";
                case FailureKind.AccessDenied: return "权限不足";
                case FailureKind.FileNotFound: return "源文件不存在或路径无效";
                case FailureKind.PathTooLong: return "路径过长";
                case FailureKind.CodecUnsupported: return "编码或容器不兼容";
                case FailureKind.MissingComponent: return "缺少所需组件（流/解码器/滤镜）";
                case FailureKind.FileInUse: return "文件被占用或读写错误";
                case FailureKind.InvalidArgument: return "参数无效";
                case FailureKind.InternalError: return "FFmpeg 内部或未知错误";
                case FailureKind.Interrupted: return "被中断";
                default: return "其它原因";
            }
        }

        /// <summary>按汇总展示的固定顺序返回全部原因类别（不含 None）。</summary>
        public static FailureKind[] AllKinds()
        {
            // 不用数组字面量初始化：实测在 LangVersion 5 + Release 优化下，
            // 字面量首元素会被写成错误值（枚举本身取值正常，逐元素赋值可规避）。
            FailureKind[] a = new FailureKind[12];
            a[0] = FailureKind.DiskFull;
            a[1] = FailureKind.CorruptSource;
            a[2] = FailureKind.AccessDenied;
            a[3] = FailureKind.FileNotFound;
            a[4] = FailureKind.PathTooLong;
            a[5] = FailureKind.CodecUnsupported;
            a[6] = FailureKind.MissingComponent;
            a[7] = FailureKind.FileInUse;
            a[8] = FailureKind.InvalidArgument;
            a[9] = FailureKind.InternalError;
            a[10] = FailureKind.Interrupted;
            a[11] = FailureKind.Other;
            return a;
        }
    }
}
