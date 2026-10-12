using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FixVideoTimeLine
{
    /// <summary>单次修复的结果。</summary>
    internal class FixResult
    {
        public int ExitCode;
        public string Mode;          // 最终成功的模式：Strong / Basic
        public bool Degraded;        // 是否发生了降级
        public string FailedMode;    // 降级前失败的模式
        public List<string> Lines = new List<string>();
        public List<Attempt> Attempts = new List<Attempt>();
    }

    internal class Attempt
    {
        public string Mode;
        public int ExitCode;
        public double Seconds;
    }

    /// <summary>FFmpeg 调用与修复流程。</summary>
    internal static class FixEngine
    {
        /// <summary>用户请求停止时使用的退出码（区别于 ffmpeg 真实退出码与 -1 异常）。</summary>
        public const int StoppedExitCode = -999;

        private static readonly Regex ReDuration =
            new Regex(@"Duration:\s*(\d+):(\d+):([\d.]+)", RegexOptions.Compiled);
        private static readonly Regex ReTime =
            new Regex(@"time=\s*(\d+):(\d+):([\d.]+)", RegexOptions.Compiled);
        private static readonly Regex ReProgress =
            new Regex(@"^\s*(frame|size|video|audio|subtitle)", RegexOptions.Compiled);

        /// <summary>
        /// 仅用于自动化测试：若非空，则把这些参数追加到「增强模式」参数末尾，
        /// 用于强制增强失败以验证自动降级逻辑。
        /// 生产运行时恒为 null，不影响任何行为。
        /// </summary>
        internal static List<string> StrongArgsInjectForTest = null;

        /// <summary>构造 FFmpeg 参数。</summary>
        public static List<string> BuildArgs(string input, string output, bool strong)
        {
            var a = new List<string>();
            a.Add("-hide_banner");
            a.Add("-nostdin");
            a.Add("-y");
            a.Add("-i"); a.Add(input);
            a.Add("-c"); a.Add("copy");
            a.Add("-fflags"); a.Add("+genpts");

            if (strong)
            {
                a.Add("-avoid_negative_ts"); a.Add("make_zero");
                a.Add("-reset_timestamps"); a.Add("1");
                a.Add("-map"); a.Add("0");
                a.Add("-ignore_unknown");

                if (StrongArgsInjectForTest != null)
                    a.AddRange(StrongArgsInjectForTest);
            }

            a.Add(output);
            return a;
        }

        /// <summary>是否为 FFmpeg 的进度行（不显示到界面）。</summary>
        public static bool IsProgressLine(string line)
        {
            return line != null && line.IndexOf("time=", StringComparison.Ordinal) >= 0
                && ReProgress.IsMatch(line);
        }

        public static string ModeName(string mode)
        {
            return mode == "Strong" ? "增强" : "基础";
        }

        /// <summary>
        /// 修复一个文件：先增强，失败自动降级基础重做同一个文件。
        /// </summary>
        /// <param name="ffmpeg">ffmpeg.exe 路径</param>
        /// <param name="input">源文件</param>
        /// <param name="output">输出文件</param>
        /// <param name="onLine">非进度行回调（用于界面日志与文件日志）</param>
        /// <param name="onPercent">进度百分比回调</param>
        /// <param name="onMode">模式切换回调（如「修复模式：增强」）</param>
        /// <param name="shouldStop">返回 true 时中止当前进程</param>
        /// <param name="registerProcess">把当前进程登记出去，供「停止」按钮杀进程</param>
        public static FixResult FixOne(
            string ffmpeg, string input, string output,
            Action<string> onLine, Action<int> onPercent, Action<string> onMode,
            Func<bool> shouldStop, Action<Process> registerProcess)
        {
            var result = new FixResult();
            FixResult last = null;

            string[] modes = AppConfig.FixModes;
            for (int mi = 0; mi < modes.Length; mi++)
            {
                // 用户已请求停止：不再尝试任何模式，立即返回停止码
                if (shouldStop != null && shouldStop())
                {
                    var stopped = new FixResult();
                    stopped.ExitCode = StoppedExitCode;
                    stopped.Mode = modes[mi];
                    stopped.Attempts = result.Attempts;
                    return stopped;
                }

                string mode = modes[mi];
                bool isFallback = mi > 0;

                if (isFallback)
                {
                    Log.Warn("增强模式失败，自动降级为基础模式重试同一个文件...");
                    if (onMode != null) onMode("增强模式失败，自动降级为基础模式重试");
                }
                else
                {
                    Log.Step(string.Format(CultureInfo.InvariantCulture,
                        "开始修复（{0} 模式）：{1}", ModeName(mode), input));
                    if (onMode != null) onMode(string.Format(CultureInfo.InvariantCulture,
                        "修复模式：{0}", ModeName(mode)));
                }

                var attempt = new Attempt();
                attempt.Mode = mode;

                var sw = Stopwatch.StartNew();
                List<string> lines;
                int exit = RunFfmpeg(ffmpeg, BuildArgs(input, output, mode == "Strong"),
                    onLine, onPercent, shouldStop, registerProcess, out lines);
                sw.Stop();

                attempt.ExitCode = exit;
                attempt.Seconds = sw.Elapsed.TotalSeconds;
                result.Attempts.Add(attempt);

                // 用户停止：立即返回，不再降级
                if (exit == StoppedExitCode)
                {
                    var stopped = new FixResult();
                    stopped.ExitCode = StoppedExitCode;
                    stopped.Mode = mode;
                    stopped.Lines = lines;
                    stopped.Attempts = result.Attempts;
                    return stopped;
                }

                bool hasOut = File.Exists(output);
                Log.Info(string.Format(CultureInfo.InvariantCulture,
                    "{0} 模式结束：退出码 {1}，用时 {2:N1} 秒，输出文件{3}",
                    ModeName(mode), exit, sw.Elapsed.TotalSeconds, hasOut ? "已生成" : "不存在"));

                var r = new FixResult();
                r.ExitCode = exit;
                r.Mode = mode;
                r.Lines = lines;
                r.Attempts = result.Attempts;

                if (exit == 0 && hasOut)
                {
                    if (isFallback)
                    {
                        Log.Ok("降级为基础模式后修复成功。");
                        r.Degraded = true;
                        r.FailedMode = modes[0];
                    }
                    return r;
                }

                if (!isFallback)
                {
                    Log.Warn(string.Format(CultureInfo.InvariantCulture,
                        "增强模式失败（退出码 {0}），准备降级重试。", exit));
                }
                last = r;
            }

            if (last != null) return last;
            var empty = new FixResult();
            empty.ExitCode = -1;
            empty.Mode = modes[modes.Length - 1];
            return empty;
        }

        /// <summary>运行一次 ffmpeg，返回退出码并收集输出行。</summary>
        private static int RunFfmpeg(
            string exe, List<string> args,
            Action<string> onLine, Action<int> onPercent,
            Func<bool> shouldStop, Action<Process> registerProcess,
            out List<string> lines)
        {
            lines = new List<string>();
            double totalSeconds = 0;
            DateTime lastPct = DateTime.MinValue;
            var collected = lines;   // 供 lambda 捕获
            int exitCode = -1;
            bool killed = false;

            var psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = ArgString(args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.StandardOutputEncoding = Encoding.UTF8;

            Process p = new Process();
            p.StartInfo = psi;

            var errGate = new object();

            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                string line = e.Data;
                lock (errGate) { collected.Add(line); }

                if (totalSeconds <= 0)
                {
                    Match md = ReDuration.Match(line);
                    if (md.Success)
                    {
                        totalSeconds = ToSeconds(md.Groups[1].Value, md.Groups[2].Value, md.Groups[3].Value);
                    }
                }

                if (onPercent != null && totalSeconds > 0)
                {
                    Match mt = ReTime.Match(line);
                    if (mt.Success)
                    {
                        DateTime now = DateTime.Now;
                        if ((now - lastPct).TotalMilliseconds >= 200)
                        {
                            lastPct = now;
                            double cur = ToSeconds(mt.Groups[1].Value, mt.Groups[2].Value, mt.Groups[3].Value);
                            int pct = (int)Math.Min(100, Math.Max(0, cur / totalSeconds * 100));
                            onPercent(pct);
                        }
                    }
                }

                if (onLine != null) onLine(line);
            };

            try
            {
                p.Start();
                if (registerProcess != null) registerProcess(p);
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();

                // 轮询等待；期间响应「停止」请求
                while (!p.HasExited)
                {
                    if (shouldStop != null && shouldStop())
                    {
                        try { p.Kill(); } catch { }
                        killed = true;
                        break;
                    }
                    System.Threading.Thread.Sleep(50);
                }
                try { p.WaitForExit(); } catch { }
                exitCode = killed ? StoppedExitCode : SafeExitCode(p);
            }
            catch (Exception ex)
            {
                string t = "[执行异常] " + ex.Message;
                lock (errGate) { collected.Add(t); }
                if (onLine != null) onLine(t);
                exitCode = -1;
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }

            return exitCode;
        }

        private static int SafeExitCode(Process p)
        {
            try { return p.ExitCode; }
            catch { return -1; }
        }

        private static double ToSeconds(string h, string m, string s)
        {
            double hh = double.Parse(h, CultureInfo.InvariantCulture);
            double mm = double.Parse(m, CultureInfo.InvariantCulture);
            double ss = double.Parse(s, CultureInfo.InvariantCulture);
            return hh * 3600 + mm * 60 + ss;
        }

        /// <summary>取数组末尾 n 个元素。</summary>
        public static string[] Tail(List<string> lines, int n)
        {
            if (lines == null || lines.Count == 0) return new string[0];
            if (lines.Count <= n) return lines.ToArray();
            var r = new string[n];
            for (int i = 0; i < n; i++) r[i] = lines[lines.Count - n + i];
            return r;
        }

        // ---------------- 参数转字符串（Windows 命令行引号规则） ----------------

        public static string ArgString(List<string> args)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(QuoteArg(args[i]));
            }
            return sb.ToString();
        }

        /// <summary>按 Windows 命令行规则给单个参数加引号。</summary>
        public static string QuoteArg(string s)
        {
            if (s == null) return "\"\"";
            if (s.Length == 0) return "\"\"";
            bool needQuote = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == ' ' || s[i] == '\t' || s[i] == '"') { needQuote = true; break; }
            }
            if (!needQuote) return s;

            var sb = new StringBuilder();
            sb.Append('"');
            int bs = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '\\') { bs++; continue; }
                if (ch == '"')
                {
                    sb.Append('\\', bs * 2 + 1);
                    sb.Append('"');
                    bs = 0;
                    continue;
                }
                if (bs > 0) { sb.Append('\\', bs); bs = 0; }
                sb.Append(ch);
            }
            if (bs > 0) sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
