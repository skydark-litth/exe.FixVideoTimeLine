using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FixVideoTimeLine
{
    /// <summary>
    /// 磁盘可用空间预检：队列里每个任务开始前检查目标卷剩余空间，
    /// 不足则由调用方跳过该条并继续执行后续任务（不中断整个队列）。
    /// 只做只读查询，不修改任何文件。
    /// </summary>
    internal static class SpaceGuard
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directory,
            out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

        /// <summary>额外余量：除文件本身外再留 32 MB，避免边界情况下写满磁盘。</summary>
        private const long MarginBytes = 32L * 1024 * 1024;

        /// <summary>返回该路径所在卷的可用字节数；探测失败返回 -1。</summary>
        public static long FreeBytes(string path)
        {
            try
            {
                string dir = path;
                if (File.Exists(path)) dir = Path.GetDirectoryName(path);

                // 向上找到第一个已存在的目录，供 API 定位卷
                while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    dir = Path.GetDirectoryName(dir);
                }
                if (string.IsNullOrEmpty(dir)) dir = AppConfig.Root;

                ulong avail, total, free;
                if (!GetDiskFreeSpaceEx(dir, out avail, out total, out free)) return -1;
                return (long)free;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 判断 path 所在卷能否再放下 needBytes（含余量）。
        /// 探测失败时返回 true（保守放行），由 ffmpeg 自行报错，避免误跳过可正常处理的任务。
        /// </summary>
        public static bool HasRoom(string path, long needBytes)
        {
            long free = FreeBytes(path);
            if (free < 0) return true;
            if (needBytes <= 0) return free >= MarginBytes;
            // 防溢出：needBytes 可能是 long.MaxValue 这类极端值
            if (needBytes > long.MaxValue - MarginBytes) return false;
            return free >= needBytes + MarginBytes;
        }
    }
}
