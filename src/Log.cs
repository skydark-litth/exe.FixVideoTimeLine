using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FixVideoTimeLine
{
    /// <summary>
    /// 运行日志：写入 &lt;exe目录&gt;\log\yyyy-MM-dd_HH-mm-ss.log（同秒重复自动加序号）。
    /// UTF-8 无 BOM，逐行追加，含毫秒时间戳。
    /// 级别：INFO|STEP|OK|WARN|ERROR|FF|FFERR|DL|END
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _file;
        private static bool _ready;
        private static int _count;

        public static string FilePath { get { return _file; } }
        public static bool IsReady { get { return _ready; } }

        /// <summary>打开本次运行的日志文件。</summary>
        public static void Open()
        {
            lock (Gate)
            {
                if (_ready) return;
                try
                {
                    string dir = AppConfig.LogDir;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                    string path = Path.Combine(dir, stamp + ".log");
                    int n = 1;
                    while (File.Exists(path))
                    {
                        path = Path.Combine(dir, stamp + "_" + n.ToString(CultureInfo.InvariantCulture) + ".log");
                        n++;
                    }
                    _file = path;
                    _ready = true;
                    _count = 0;
                }
                catch
                {
                    _ready = false;
                    _file = null;
                }
            }
        }

        /// <summary>写一行。</summary>
        public static void Write(string level, string text)
        {
            lock (Gate)
            {
                if (!_ready) Open();
                if (!_ready) return;
                _count++;
                string line = string.Format(CultureInfo.InvariantCulture,
                    "{0} [{1}] {2}", DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), level, text);
                try
                {
                    File.AppendAllText(_file, line + "\r\n", new UTF8Encoding(false));
                }
                catch { }
            }
        }

        public static void Info(string t) { Write("INFO", t); }
        public static void Step(string t) { Write("STEP", t); }
        public static void Ok(string t) { Write("OK", t); }
        public static void Warn(string t) { Write("WARN", t); }
        public static void Err(string t) { Write("ERROR", t); }
        public static void Ff(string t) { Write("FF", t); }
        public static void FfErr(string t) { Write("FFERR", t); }
        public static void Dl(string t) { Write("DL", t); }

        /// <summary>收尾：写结束标记。</summary>
        public static void Close()
        {
            lock (Gate)
            {
                if (!_ready) return;
                _count++;
                string line = string.Format(CultureInfo.InvariantCulture,
                    "{0} [{1}] {2}", DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    "END", string.Format(CultureInfo.InvariantCulture, "日志结束，共 {0} 行", _count));
                try
                {
                    File.AppendAllText(_file, line + "\r\n", new UTF8Encoding(false));
                }
                catch { }
                _ready = false;
            }
        }
    }
}
