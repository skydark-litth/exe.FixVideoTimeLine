using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FixVideoTimeLine
{
    /// <summary>FFmpeg 定位、下载与安装。</summary>
    internal static class FfmpegManager
    {
        // ---------------- 定位 ----------------

        /// <summary>只查 exe 目录与 exe目录\tools\ffmpeg，不扫 PATH（避免版本搞错）。</summary>
        public static string Find()
        {
            string[] cands = new string[]
            {
                Path.Combine(AppConfig.FfmpegDir, "ffmpeg.exe"),
                Path.Combine(AppConfig.Root, "ffmpeg.exe")
            };
            for (int i = 0; i < cands.Length; i++)
            {
                if (File.Exists(cands[i])) return cands[i];
            }
            return null;
        }

        public static string Version(string exe)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = "-version";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string first = p.StandardOutput.ReadLine();
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    if (first != null) return first.Trim();
                }
            }
            catch { }
            return "未知版本";
        }

        // ---------------- 网页读取 ----------------

        private static string GetWebString(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = AppConfig.UserAgent;
            req.Timeout = 30000;
            req.AllowAutoRedirect = true;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        /// <summary>读取 ffmpeg.org 官方下载页，解析 Windows 构建入口（GitHub 仓库名 / gyan.dev 可用性）。</summary>
        private static void QueryFfmpegOrg(out string githubRepo, out bool gyanOk)
        {
            githubRepo = null;
            gyanOk = false;
            try
            {
                string html = GetWebString(AppConfig.FfmpegOrgPage);
                string seg = html;
                Match m = Regex.Match(html, @"(?is)Windows EXE Files(.{0,2000}?)</div>");
                if (m.Success) seg = m.Groups[1].Value;

                Match gh = Regex.Match(seg, @"https://github\.com/([A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+)/releases");
                if (gh.Success) githubRepo = gh.Groups[1].Value;

                Match gy = Regex.Match(seg, @"https://(?:www\.)?gyan\.dev/ffmpeg/builds/");
                if (gy.Success) gyanOk = true;
            }
            catch { }
        }

        // ---------------- 分段下载 ----------------

        /// <summary>
        /// 分段多线程下载。
        /// 每段写入独立临时文件，校验各段大小后按序合并
        /// （绝不能多线程写同一文件的不同偏移，那会损坏数据）。
        /// </summary>
        public static long Download(string url, string outFile, int parts, Action<string> onProgress, Func<bool> shouldStop)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 64;

            long total = 0;
            bool rangesOk = false;

            var head = (HttpWebRequest)WebRequest.Create(url);
            head.Method = "HEAD";
            head.UserAgent = AppConfig.UserAgent;
            head.Timeout = 30000;
            head.AllowAutoRedirect = true;
            using (var resp = head.GetResponse())
            {
                total = resp.ContentLength;
                string ar = resp.Headers["Accept-Ranges"];
                if (ar != null && ar.IndexOf("bytes", StringComparison.OrdinalIgnoreCase) >= 0) rangesOk = true;
            }

            if (total <= 0) throw new Exception("未能取得文件大小");
            if (!rangesOk || total < 4L * 1024 * 1024) parts = 1;
            if (parts < 1) parts = 1;
            if (parts > 16) parts = 16;

            var dir = Path.GetDirectoryName(outFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string baseName = outFile + ".part";
            long step = total / parts;

            var partPaths = new string[parts];
            var counters = new long[parts];
            var errors = new string[parts];

            var threads = new Thread[parts];
            long[] starts = new long[parts];
            long[] ends = new long[parts];

            for (int i = 0; i < parts; i++)
            {
                starts[i] = i * step;
                ends[i] = (i == parts - 1) ? total - 1 : ((i + 1) * step - 1);
                partPaths[i] = baseName + "." + i.ToString(CultureInfo.InvariantCulture);
                if (File.Exists(partPaths[i])) File.Delete(partPaths[i]);
            }

            DateTime deadline = DateTime.Now.AddMinutes(90);

            for (int i = 0; i < parts; i++)
            {
                int idx = i;
                threads[idx] = new Thread(delegate()
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(url);
                        req.UserAgent = AppConfig.UserAgent;
                        req.Timeout = 60000;
                        req.ReadWriteTimeout = 180000;
                        req.AllowAutoRedirect = true;
                        if (parts > 1) req.AddRange(starts[idx], ends[idx]);

                        using (var resp = req.GetResponse())
                        using (var input = resp.GetResponseStream())
                        using (var fs = new FileStream(partPaths[idx], FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                        {
                            byte[] buf = new byte[262144];
                            while (true)
                            {
                                if (shouldStop != null && shouldStop()) break;
                                int n = input.Read(buf, 0, buf.Length);
                                if (n <= 0) break;
                                fs.Write(buf, 0, n);
                                counters[idx] += n;
                            }
                            fs.Flush();
                        }
                    }
                    catch (Exception ex)
                    {
                        errors[idx] = ex.Message;
                    }
                });
                threads[idx].IsBackground = true;
                threads[idx].Start();
            }

            long lastReported = -1;
            while (true)
            {
                bool allDone = true;
                for (int i = 0; i < parts; i++)
                {
                    if (threads[i] != null && threads[i].IsAlive) { allDone = false; break; }
                }

                long got = 0;
                for (int i = 0; i < parts; i++) got += counters[i];

                if (onProgress != null && got != lastReported)
                {
                    lastReported = got;
                    int pct = (int)Math.Min(100, got * 100 / total);
                    onProgress(string.Format(CultureInfo.InvariantCulture,
                        "正在下载 FFmpeg：{0:N1} / {1:N1} MB（{2}%）",
                        got / 1048576.0, total / 1048576.0, pct));
                }

                if (allDone) break;
                if (shouldStop != null && shouldStop()) break;
                if (DateTime.Now > deadline) break;
                Thread.Sleep(120);
            }

            for (int i = 0; i < parts; i++)
            {
                if (threads[i] != null) { try { threads[i].Join(120000); } catch { } }
            }

            // 校验各段大小后再合并
            var problems = new List<string>();
            long sum = 0;
            for (int i = 0; i < parts; i++)
            {
                long expect = ends[i] - starts[i] + 1;
                long actual = File.Exists(partPaths[i]) ? new FileInfo(partPaths[i]).Length : 0;
                if (parts > 1 && actual != expect)
                {
                    problems.Add(string.Format(CultureInfo.InvariantCulture,
                        "第 {0} 段大小不符（{1}/{2} 字节）", i + 1, actual, expect));
                }
                sum += actual;
                if (errors[i] != null) problems.Add(errors[i]);
            }

            if (problems.Count > 0)
            {
                CleanupParts(partPaths);
                throw new Exception("下载失败：" + string.Join("；", problems.ToArray()));
            }

            using (var outFs = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                for (int i = 0; i < parts; i++)
                {
                    using (var inFs = new FileStream(partPaths[i], FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        byte[] buf = new byte[1048576];
                        while (true)
                        {
                            int n = inFs.Read(buf, 0, buf.Length);
                            if (n <= 0) break;
                            outFs.Write(buf, 0, n);
                        }
                    }
                }
            }
            CleanupParts(partPaths);

            long final = new FileInfo(outFile).Length;
            if (final != total)
            {
                throw new Exception(string.Format(CultureInfo.InvariantCulture,
                    "下载不完整：{0:N1}/{1:N1} MB", final / 1048576.0, total / 1048576.0));
            }
            return final;
        }

        private static void CleanupParts(string[] partPaths)
        {
            for (int i = 0; i < partPaths.Length; i++)
            {
                try { if (File.Exists(partPaths[i])) File.Delete(partPaths[i]); }
                catch { }
            }
        }

        // ---------------- 安装 ----------------

        private class Source
        {
            public string Kind;    // Direct / GitHub
            public string Name;
            public string Url;
            public string Repo;
        }

        /// <summary>
        /// 下载并安装 FFmpeg 到 &lt;exe目录&gt;\tools\ffmpeg。
        /// 来源顺序：先读 ffmpeg.org 确认入口 -> gyan.dev -> GitHub(BtbN/FFmpeg-Builds)。
        /// </summary>
        public static string Install(Action<string> onProgress, Func<bool> shouldStop)
        {
            Action<string> report = delegate(string msg)
            {
                if (onProgress != null) onProgress(msg);
            };

            if (!Directory.Exists(AppConfig.ToolsDir)) Directory.CreateDirectory(AppConfig.ToolsDir);

            string tmpZip = Path.Combine(AppConfig.ToolsDir, "ffmpeg-download.zip");
            string tmpDir = Path.Combine(AppConfig.ToolsDir, "ffmpeg-extract");

            // ---- 规划来源 ----
            string repo = AppConfig.GithubRepo;
            report("正在读取 ffmpeg.org 官方下载页，确认 Windows 构建入口...");
            string orgRepo;
            bool gyanOk;
            QueryFfmpegOrg(out orgRepo, out gyanOk);
            if (!string.IsNullOrEmpty(orgRepo))
            {
                repo = orgRepo;
                report(string.Format(CultureInfo.InvariantCulture, "ffmpeg.org 官方指向的 GitHub 构建：{0}", repo));
            }
            else
            {
                report(string.Format(CultureInfo.InvariantCulture, "未从 ffmpeg.org 取得入口，使用官方推荐仓库 {0}", repo));
            }

            var plan = new List<Source>();
            plan.Add(new Source { Kind = "Direct", Name = "gyan.dev", Url = AppConfig.GyanDevZip });
            plan.Add(new Source { Kind = "GitHub", Name = "GitHub · " + repo, Repo = repo });

            string lastError = null;
            foreach (Source src in plan)
            {
                try
                {
                    if (File.Exists(tmpZip)) File.Delete(tmpZip);

                    if (src.Kind == "GitHub")
                    {
                        report(string.Format(CultureInfo.InvariantCulture, "正在从 {0} 查询可用构建包...", src.Name));
                        string apiUrl = "https://api.github.com/repos/" + src.Repo + "/releases/latest";
                        string json = GetWebString(apiUrl);

                        string pickName = null, pickUrl = null;
                        long pickSize = 0;
                        PickGithubAsset(json, out pickName, out pickUrl, out pickSize);
                        if (pickName == null) throw new Exception("未找到 win64 的 zip 包");

                        report(string.Format(CultureInfo.InvariantCulture, "选中 {0}（{1:N1} MB）", pickName, pickSize / 1048576.0));
                        DownloadGithubAsset(pickUrl, tmpZip, 8, onProgress, shouldStop);
                    }
                    else
                    {
                        report(string.Format(CultureInfo.InvariantCulture, "正在从 {0} 下载...", src.Name));
                        Download(src.Url, tmpZip, 8, onProgress, shouldStop);
                    }

                    if (!File.Exists(tmpZip)) throw new Exception("未生成下载文件");
                    long gb = new FileInfo(tmpZip).Length;
                    if (gb < 5L * 1024 * 1024)
                        throw new Exception(string.Format(CultureInfo.InvariantCulture, "下载文件过小（{0:N1} MB）", gb / 1048576.0));
                    report(string.Format(CultureInfo.InvariantCulture, "下载完成，共 {0:N1} MB，正在解压...", gb / 1048576.0));

                    if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                    Directory.CreateDirectory(tmpDir);
                    ZipFile.ExtractToDirectory(tmpZip, tmpDir);

                    string found = null;
                    string[] hits = Directory.GetFiles(tmpDir, "ffmpeg.exe", SearchOption.AllDirectories);
                    for (int i = 0; i < hits.Length; i++)
                    {
                        if (string.Equals(Path.GetFileName(Path.GetDirectoryName(hits[i])), "bin", StringComparison.OrdinalIgnoreCase))
                        { found = hits[i]; break; }
                    }
                    if (found == null && hits.Length > 0) found = hits[0];
                    if (found == null) throw new Exception("解压后未找到 ffmpeg.exe");

                    string srcDir = Path.GetDirectoryName(found);
                    if (Directory.Exists(AppConfig.FfmpegDir)) Directory.Delete(AppConfig.FfmpegDir, true);
                    Directory.CreateDirectory(AppConfig.FfmpegDir);
                    CopyDir(srcDir, AppConfig.FfmpegDir);

                    string final = Path.Combine(AppConfig.FfmpegDir, "ffmpeg.exe");
                    if (!File.Exists(final)) throw new Exception("安装后未找到 ffmpeg.exe");

                    TryDelete(tmpZip);
                    try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
                    report("FFmpeg 安装完成");
                    return final;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    report(string.Format(CultureInfo.InvariantCulture, "来源 {0} 失败：{1}", src.Name, lastError));
                }
            }

            throw new Exception("所有来源均失败，最后一个错误：" + lastError);
        }

        /// <summary>
        /// 从 GitHub release JSON 中挑出 win64 的 zip 资产。
        /// 注意：github.com 主站不可达，资产必须走 API 端点 +
        /// Accept: application/octet-stream 才能下载。
        /// </summary>
        private static void PickGithubAsset(string json, out string name, out string url, out long size)
        {
            name = null; url = null; size = 0;

            // 优先 gpl-shared，其次 gpl，最后任意 win64 zip
            string[] prefers = new string[] { "*win64*gpl-shared*.zip", "*win64*gpl*.zip", "*win64*.zip" };

            var assets = ParseAssets(json);
            for (int pi = 0; pi < prefers.Length && name == null; pi++)
            {
                string pat = "^" + Regex.Escape(prefers[pi]).Replace("\\*", ".*") + "$";
                var re = new Regex(pat, RegexOptions.IgnoreCase);
                for (int i = 0; i < assets.Count; i++)
                {
                    if (re.IsMatch(assets[i].Name))
                    {
                        name = assets[i].Name;
                        url = assets[i].Url;
                        size = assets[i].Size;
                        break;
                    }
                }
            }
        }

        private class Asset
        {
            public string Name;
            public string Url;
            public long Size;
        }

        /// <summary>极简 JSON 解析：提取 assets 数组里的 name / browser_download_url / size。</summary>
        private static List<Asset> ParseAssets(string json)
        {
            var list = new List<Asset>();
            int assetsIdx = json.IndexOf("\"assets\"", StringComparison.Ordinal);
            if (assetsIdx < 0) return list;

            int i = assetsIdx;
            while (true)
            {
                int objStart = json.IndexOf('{', i);
                if (objStart < 0) break;
                int objEnd = IndexOfMatchingBrace(json, objStart);
                if (objEnd < 0) break;

                string obj = json.Substring(objStart, objEnd - objStart + 1);
                string nm = JsonString(obj, "name");
                string dl = JsonString(obj, "browser_download_url");
                if (nm != null && dl != null)
                {
                    var a = new Asset();
                    a.Name = nm;
                    a.Url = dl;
                    a.Size = JsonLong(obj, "size");
                    list.Add(a);
                }

                i = objEnd + 1;
                // 到 assets 数组结束就停
                int arrEnd = json.IndexOf(']', objEnd);
                int nextObj = json.IndexOf('{', objEnd);
                if (nextObj < 0 || (arrEnd >= 0 && arrEnd < nextObj)) break;
            }
            return list;
        }

        private static int IndexOfMatchingBrace(string s, int start)
        {
            int depth = 0;
            bool inStr = false, esc = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (esc) { esc = false; continue; }
                    if (c == '\\') { esc = true; continue; }
                    if (c == '"') { inStr = false; continue; }
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static string JsonString(string obj, string key)
        {
            Match m = Regex.Match(obj, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return UnescapeJson(m.Groups[1].Value);
        }

        private static long JsonLong(string obj, string key)
        {
            Match m = Regex.Match(obj, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
            if (!m.Success) return 0;
            long v;
            if (long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        private static string UnescapeJson(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'u' && i + 4 < s.Length)
                    {
                        string hex = s.Substring(i + 1, 4);
                        int code;
                        if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                    }
                    else sb.Append(n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>GitHub 资产下载：用 API 端点 + octet-stream 绕开 github.com 主站不可达。</summary>
        private static void DownloadGithubAsset(string apiUrl, string outFile, int parts, Action<string> onProgress, Func<bool> shouldStop)
        {
            DownloadWithAccept(apiUrl, outFile, parts, onProgress, shouldStop, "application/octet-stream");
        }

        private static void DownloadWithAccept(string url, string outFile, int parts, Action<string> onProgress, Func<bool> shouldStop, string accept)
        {
            // 用带 Accept 头的请求走一遍分段下载逻辑
            DownloadInternal(url, outFile, parts, onProgress, shouldStop, accept);
        }

        private static void DownloadInternal(string url, string outFile, int parts, Action<string> onProgress, Func<bool> shouldStop, string accept)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 64;

            long total = 0;
            bool rangesOk = false;

            var head = (HttpWebRequest)WebRequest.Create(url);
            head.Method = "HEAD";
            head.UserAgent = AppConfig.UserAgent;
            head.Accept = accept;
            head.Timeout = 30000;
            head.AllowAutoRedirect = true;
            using (var resp = head.GetResponse())
            {
                total = resp.ContentLength;
                string ar = resp.Headers["Accept-Ranges"];
                if (ar != null && ar.IndexOf("bytes", StringComparison.OrdinalIgnoreCase) >= 0) rangesOk = true;
            }
            if (total <= 0) throw new Exception("未能取得文件大小");
            if (!rangesOk || total < 4L * 1024 * 1024) parts = 1;

            var dir = Path.GetDirectoryName(outFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            long step = total / parts;
            var partPaths = new string[parts];
            var counters = new long[parts];
            var errors = new string[parts];
            var threads = new Thread[parts];
            var starts = new long[parts];
            var ends = new long[parts];

            string baseName = outFile + ".part";
            for (int i = 0; i < parts; i++)
            {
                starts[i] = i * step;
                ends[i] = (i == parts - 1) ? total - 1 : ((i + 1) * step - 1);
                partPaths[i] = baseName + "." + i.ToString(CultureInfo.InvariantCulture);
                if (File.Exists(partPaths[i])) File.Delete(partPaths[i]);
            }

            for (int i = 0; i < parts; i++)
            {
                int idx = i;
                threads[idx] = new Thread(delegate()
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(url);
                        req.UserAgent = AppConfig.UserAgent;
                        req.Accept = accept;
                        req.Timeout = 60000;
                        req.ReadWriteTimeout = 180000;
                        req.AllowAutoRedirect = true;
                        if (parts > 1) req.AddRange(starts[idx], ends[idx]);

                        using (var resp = req.GetResponse())
                        using (var input = resp.GetResponseStream())
                        using (var fs = new FileStream(partPaths[idx], FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                        {
                            byte[] buf = new byte[131072];
                            while (true)
                            {
                                if (shouldStop != null && shouldStop()) break;
                                int n = input.Read(buf, 0, buf.Length);
                                if (n <= 0) break;
                                fs.Write(buf, 0, n);
                                counters[idx] += n;
                            }
                            fs.Flush();
                        }
                    }
                    catch (Exception ex) { errors[idx] = ex.Message; }
                });
                threads[idx].IsBackground = true;
                threads[idx].Start();
            }

            long lastReported = -1;
            while (true)
            {
                bool allDone = true;
                for (int i = 0; i < parts; i++)
                    if (threads[i] != null && threads[i].IsAlive) { allDone = false; break; }

                long got = 0;
                for (int i = 0; i < parts; i++) got += counters[i];
                if (onProgress != null && got != lastReported)
                {
                    lastReported = got;
                    int pct = (int)Math.Min(100, got * 100 / total);
                    onProgress(string.Format(CultureInfo.InvariantCulture,
                        "正在下载 FFmpeg：{0:N1} / {1:N1} MB（{2}%）",
                        got / 1048576.0, total / 1048576.0, pct));
                }

                if (allDone) break;
                if (shouldStop != null && shouldStop()) break;
                Thread.Sleep(120);
            }

            for (int i = 0; i < parts; i++)
                if (threads[i] != null) { try { threads[i].Join(120000); } catch { } }

            var problems = new List<string>();
            for (int i = 0; i < parts; i++)
            {
                long expect = ends[i] - starts[i] + 1;
                long actual = File.Exists(partPaths[i]) ? new FileInfo(partPaths[i]).Length : 0;
                if (parts > 1 && actual != expect)
                    problems.Add(string.Format(CultureInfo.InvariantCulture, "第 {0} 段大小不符（{1}/{2} 字节）", i + 1, actual, expect));
                if (errors[i] != null) problems.Add(errors[i]);
            }
            if (problems.Count > 0) { CleanupParts(partPaths); throw new Exception("下载失败：" + string.Join("；", problems.ToArray())); }

            using (var outFs = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                for (int i = 0; i < parts; i++)
                {
                    using (var inFs = new FileStream(partPaths[i], FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        byte[] buf = new byte[1048576];
                        while (true)
                        {
                            int n = inFs.Read(buf, 0, buf.Length);
                            if (n <= 0) break;
                            outFs.Write(buf, 0, n);
                        }
                    }
                }
            }
            CleanupParts(partPaths);

            long fin = new FileInfo(outFile).Length;
            if (fin != total)
                throw new Exception(string.Format(CultureInfo.InvariantCulture, "下载不完整：{0:N1}/{1:N1} MB", fin / 1048576.0, total / 1048576.0));
        }

        private static void CopyDir(string src, string dst)
        {
            if (!Directory.Exists(dst)) Directory.CreateDirectory(dst);
            string[] files = Directory.GetFiles(src);
            for (int i = 0; i < files.Length; i++)
            {
                string target = Path.Combine(dst, Path.GetFileName(files[i]));
                File.Copy(files[i], target, true);
            }
            string[] dirs = Directory.GetDirectories(src);
            for (int i = 0; i < dirs.Length; i++)
            {
                CopyDir(dirs[i], Path.Combine(dst, Path.GetFileName(dirs[i])));
            }
        }

        private static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }
}
