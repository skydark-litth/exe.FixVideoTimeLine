using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FixVideoTimeLine
{
    /// <summary>
    /// 现代（Vista 风格）文件/文件夹选择框，基于 IFileDialog COM 接口。
    ///
    /// 为什么不用 .NET 自带的：
    ///   - OpenFileDialog 在 .NET Framework 下本就是现代样式（AutoUpgradeEnabled 默认 true），
    ///     但这里仍统一走 COM，保证两个对话框外观与行为完全一致。
    ///   - FolderBrowserDialog 在 .NET Framework 4.8.1 中没有 AutoUpgradeEnabled 属性
    ///     （该属性是 .NET Core 3.0+ 才加的，Microsoft Learn 的 netframework 文档页
    ///      混入了新版属性，具有误导性），只能显示老式树状界面，因此必须走 COM。
    ///
    /// 关键坑：IFileOpenDialog 的 vtable 槽位必须完整声明全部 25 个方法。
    ///   少写 AddPlace/SetDefaultExtension/Close/SetClientGuid/ClearClientData/SetFilter
    ///   这 6 个后，GetResults 会落到错误槽位，调用直接 AccessViolationException + 进程崩溃。
    /// </summary>
    internal static class NativeDialog
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct COMDLG_FILTERSPEC
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
        }

        // IModalWindow + IFileDialog（完整 23 个方法，一个都不能少）
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes,
                [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct, SizeParamIndex = 0)]
                COMDLG_FILTERSPEC[] rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder([MarshalAs(UnmanagedType.Interface)] IShellItem psi);
            void SetFolder([MarshalAs(UnmanagedType.Interface)] IShellItem psi);
            void GetFolder([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void AddPlace([MarshalAs(UnmanagedType.Interface)] IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter([MarshalAs(UnmanagedType.Interface)] IntPtr pFilter);
        }

        // IFileOpenDialog = IFileDialog + 2 个方法（共 25 个槽位）
        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes,
                [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct, SizeParamIndex = 0)]
                COMDLG_FILTERSPEC[] rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder([MarshalAs(UnmanagedType.Interface)] IShellItem psi);
            void SetFolder([MarshalAs(UnmanagedType.Interface)] IShellItem psi);
            void GetFolder([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void AddPlace([MarshalAs(UnmanagedType.Interface)] IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter([MarshalAs(UnmanagedType.Interface)] IntPtr pFilter);
            // ---- IFileOpenDialog ----
            void GetResults([MarshalAs(UnmanagedType.Interface)] out IEnumShellItems ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumShellItems
        {
            [PreserveSig] int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
            [PreserveSig] int Skip(uint celt);
            [PreserveSig] int Reset();
            [PreserveSig] int Clone(out IEnumShellItems ppenum);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRCW { }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        private const uint FOS_PICKFOLDERS = 0x0020;
        private const uint FOS_FORCEFILESYSTEM = 0x0040;
        private const uint FOS_ALLOWMULTISELECT = 0x0200;
        private const uint FOS_PATHMUSTEXIST = 0x0800;
        private const uint FOS_FILEMUSTEXIST = 0x1000;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const int S_OK = 0;
        private static readonly Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        private static IFileOpenDialog Create()
        {
            return (IFileOpenDialog)new FileOpenDialogRCW();
        }

        private static void ApplyStart(IFileOpenDialog dlg, string initial)
        {
            if (string.IsNullOrEmpty(initial)) return;
            try
            {
                IShellItem start;
                Guid iid = IID_IShellItem;
                SHCreateItemFromParsingName(initial, IntPtr.Zero, ref iid, out start);
                dlg.SetFolder(start);
            }
            catch { }
        }

        private static string PathOf(IShellItem item)
        {
            string p;
            item.GetDisplayName(SIGDN_FILESYSPATH, out p);
            return p;
        }

        private static COMDLG_FILTERSPEC[] ParseFilter(string filter)
        {
            if (string.IsNullOrEmpty(filter)) return null;
            string[] parts = filter.Split('|');
            int n = parts.Length / 2;
            if (n <= 0) return null;
            var arr = new COMDLG_FILTERSPEC[n];
            for (int i = 0; i < n; i++)
            {
                arr[i] = new COMDLG_FILTERSPEC();
                arr[i].pszName = parts[i * 2];
                arr[i].pszSpec = parts[i * 2 + 1];
            }
            return arr;
        }

        /// <summary>选择文件夹（现代样式）。返回 null 表示用户取消。</summary>
        public static string PickFolder(string title, string initial, IntPtr owner)
        {
            try
            {
                IFileOpenDialog dlg = Create();
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                ApplyStart(dlg, initial);
                if (!string.IsNullOrEmpty(title)) dlg.SetTitle(title);
                dlg.SetOkButtonLabel("选择此文件夹");
                int hr = dlg.Show(owner);
                if (hr != S_OK) return null;
                IShellItem res;
                dlg.GetResult(out res);
                return PathOf(res);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>选择文件（可多选，现代样式）。返回 null 表示用户取消。</summary>
        public static string[] PickFiles(string title, string filter, string initial, IntPtr owner)
        {
            try
            {
                IFileOpenDialog dlg = Create();
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST
                                    | FOS_FILEMUSTEXIST | FOS_ALLOWMULTISELECT);
                COMDLG_FILTERSPEC[] spec = ParseFilter(filter);
                if (spec != null) dlg.SetFileTypes((uint)spec.Length, spec);
                ApplyStart(dlg, initial);
                if (!string.IsNullOrEmpty(title)) dlg.SetTitle(title);
                dlg.SetOkButtonLabel("选择文件");
                int hr = dlg.Show(owner);
                if (hr != S_OK) return null;

                var list = new List<string>();
                IEnumShellItems en;
                dlg.GetResults(out en);
                IntPtr one;
                uint fetched;
                while (en.Next(1, out one, out fetched) == S_OK && fetched == 1)
                {
                    try
                    {
                        IShellItem it = (IShellItem)Marshal.GetObjectForIUnknown(one);
                        list.Add(PathOf(it));
                        Marshal.ReleaseComObject(it);
                    }
                    catch { }
                    finally { Marshal.Release(one); }
                }
                if (list.Count == 0) return null;
                return list.ToArray();
            }
            catch
            {
                return null;
            }
        }
    }
}
