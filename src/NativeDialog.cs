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
        private const uint FOS_PATHMUSTEXIST = 0x0800;
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

        // 说明：这里**曾经**有一个 PickFiles（多选文件）方法，已移除。
        // 它走 dlg.GetResults + IEnumShellItems 逐条枚举，但与 PickFolder 的唯一差别就在这一步——
        // PickFolder 用 GetResult 取单条，一直正常；PickFiles 用 GetResults 取枚举，
        // 出现「选完文件后返回空、界面仍显示尚未选择」的问题。
        // 排查记录：创建对话框、SetOptions、SetFileTypes、SetTitle、SetFolder、SetFileName
        // 全部调用成功（无异常、无 vtable 错位），故失败点只能落在 GetResults 的枚举上。
        // 由于该方法在异常时 catch{return null} 会完全静默，无法在无人值守环境复现点击，
        // 改用 .NET Framework 自带的 OpenFileDialog（net48 上本就是现代样式、原生支持多选与筛选），
        // 见 MainForm.OnPickFiles。文件夹选择仍走这里，因为 net48 的 FolderBrowserDialog 只能显示老式树状界面。
        //
        // 注意：下面两个接口的 GetResults / GetSelectedItems 槽位保留声明——
        // IFileOpenDialog 的 vtable 必须完整，缺槽位会让调用落到错误位置。

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
    }
}
