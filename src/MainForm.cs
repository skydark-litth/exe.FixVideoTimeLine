using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FixVideoTimeLine
{
    internal class MainForm : Form
    {
        // ---- 控件 ----
        private Button _btnPickFiles, _btnPickDir, _btnClear, _btnRun, _btnCancel, _btnOpen, _btnFf;
        private CheckBox _chkSub, _chkSkip;
        private RadioButton _rbSub, _rbOver, _rbReplace;
        private Label _lblSubDesc, _lblOverDesc, _lblReplaceDesc;
        private Label _lblCount, _lblHint, _lblStatus, _lblFf;
        private ProgressBar _pgBar;
        private ProgressBarSkin _pgSkin;
        private RichTextBox _txtLog;

        // ---- 字体 ----
        private Font _uiFont;
        private Font _logFont;
        private Font _logFontBold;
        private Font _summaryFont;

        // ---- 状态 ----
        private readonly List<string> _inputPaths = new List<string>();
        private List<string> _scanFiles = new List<string>();
        private string _lastOutput;
        private string _lastOutputDir;   // 输出目录（弹窗只给目录，不罗列文件）
        private volatile bool _stopRequested;
        private volatile System.Diagnostics.Process _currentProcess;
        private bool _running;

        // ---- 日志配色（GUI 面向最终用户；LOG 文件保留全量明细） ----
        private static readonly Color CInfo = Color.FromArgb(0x2C, 0x2C, 0x2A);      // 普通信息
        private static readonly Color CMuted = Color.FromArgb(0x5F, 0x5E, 0x5A);     // 次要信息
        private static readonly Color CSep = Color.FromArgb(0x44, 0x44, 0x41);       // 文件分隔
        private static readonly Color CTask = Color.FromArgb(0x1B, 0x4F, 0x8A);      // 任务开始
        private static readonly Color COk = Color.FromArgb(0x0F, 0x6E, 0x56);        // 成功
        private static readonly Color CDegraded = Color.FromArgb(0x85, 0x4F, 0x0B);  // 降级成功
        private static readonly Color CSkip = Color.FromArgb(0x5F, 0x5E, 0x5A);      // 跳过
        private static readonly Color CFail = Color.FromArgb(0xA3, 0x2D, 0x2D);      // 失败
        private static readonly Color CStopped = Color.FromArgb(0x99, 0x35, 0x56);   // 已停止
        private static readonly Color CWarn = Color.FromArgb(0x85, 0x4F, 0x0B);      // 警告
        private static readonly Color CErrorBack = Color.FromArgb(0xA3, 0x2D, 0x2D); // 程序级错误底色
        private static readonly Color CErrorFore = Color.White;
        private static readonly Color CSumItem = Color.FromArgb(0x79, 0x1F, 0x1F);   // 汇总列表项

        public MainForm()
        {
            InitializeUi();

            UpdateFfmpegLabel();
            AppendLog("提示：可直接把视频文件或文件夹拖入本窗口；在「② 输出」选择修复结果的存放方式。");
            AppendMuted("本窗口只显示执行概要；完整明细见 log 目录下的日志文件。");

            Shown += delegate { CheckFfmpegOnStartup(); };
            FormClosed += delegate
            {
                if (_pgSkin != null) _pgSkin.Dispose();
                if (_logFontBold != null) _logFontBold.Dispose();
                if (_summaryFont != null) _summaryFont.Dispose();
                if (_logFont != null) _logFont.Dispose();
                if (_uiFont != null) _uiFont.Dispose();
            };
        }

        private void InitializeUi()
        {
            Text = AppConfig.AppName + "  v" + AppConfig.AppVer;
            ClientSize = new Size(760, 752);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            _uiFont = new Font("Microsoft YaHei UI", 9f);
            _logFont = new Font("Consolas", 9f);
            _logFontBold = new Font(_logFont, FontStyle.Bold);
            _summaryFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            Font = _uiFont;
            AllowDrop = true;

            // ---- ① 输入 ----
            var gb1 = new GroupBox();
            gb1.Text = "① 选择要修复的视频或文件夹";
            gb1.Location = new Point(12, 8);
            gb1.Size = new Size(736, 96);
            Controls.Add(gb1);

            _btnPickFiles = MakeButton("选择视频文件...", 14, 24, 126, 30);
            _btnPickFiles.Click += delegate { OnPickFiles(); };
            gb1.Controls.Add(_btnPickFiles);

            _btnPickDir = MakeButton("选择文件夹...", 148, 24, 126, 30);
            _btnPickDir.Click += delegate { OnPickDir(); };
            gb1.Controls.Add(_btnPickDir);

            _btnClear = MakeButton("清空", 282, 24, 70, 30);
            _btnClear.Click += delegate { OnClear(); };
            gb1.Controls.Add(_btnClear);

            _chkSub = new CheckBox();
            _chkSub.Text = "包含子文件夹";
            _chkSub.Location = new Point(368, 29);
            _chkSub.Size = new Size(130, 22);
            _chkSub.CheckedChanged += delegate { if (_inputPaths.Count > 0) ApplyInput(_inputPaths); };
            gb1.Controls.Add(_chkSub);

            _lblCount = new Label();
            _lblCount.Text = "尚未选择";
            _lblCount.Location = new Point(508, 31);
            _lblCount.Size = new Size(216, 20);
            _lblCount.ForeColor = Color.DimGray;
            gb1.Controls.Add(_lblCount);

            _lblHint = new Label();
            _lblHint.Text = "把视频文件或文件夹直接拖到本窗口也可以";
            _lblHint.Location = new Point(14, 62);
            _lblHint.Size = new Size(710, 20);
            _lblHint.ForeColor = Color.DimGray;
            gb1.Controls.Add(_lblHint);

            // ---- ② 输出 ----
            var gb2 = new GroupBox();
            gb2.Text = "② 输出";
            gb2.Location = new Point(12, 110);
            gb2.Size = new Size(736, 248);
            Controls.Add(gb2);

            _rbSub = new RadioButton();
            _rbSub.Text = "输出到子目录 genpts（推荐）";
            _rbSub.Location = new Point(14, 19);
            _rbSub.Size = new Size(320, 22);
            _rbSub.Checked = true;
            _rbSub.CheckedChanged += delegate { if (_rbSub.Checked) OnOutputModeChanged(); };
            gb2.Controls.Add(_rbSub);

            _lblSubDesc = new Label();
            _lblSubDesc.Text = "在源视频所在目录下新建 genpts 子目录，输出文件名与源文件相同；\r\n原文件保持不变，可反复重跑。";
            _lblSubDesc.AutoSize = false;
            _lblSubDesc.Location = new Point(34, 42);
            _lblSubDesc.Size = new Size(690, 38);
            _lblSubDesc.ForeColor = Color.DimGray;
            gb2.Controls.Add(_lblSubDesc);

            _rbOver = new RadioButton();
            _rbOver.Text = "与原文件在同一目录";
            _rbOver.Location = new Point(14, 84);
            _rbOver.Size = new Size(320, 22);
            _rbOver.CheckedChanged += delegate { if (_rbOver.Checked) OnOutputModeChanged(); };
            gb2.Controls.Add(_rbOver);

            _lblOverDesc = new Label();
            _lblOverDesc.Text = "输出到源文件所在的同一文件夹，主文件名后加 _genpts 后缀，\r\n如 片头.mp4 → 片头_genpts.mp4；原文件保持不变。";
            _lblOverDesc.AutoSize = false;
            _lblOverDesc.Location = new Point(34, 107);
            _lblOverDesc.Size = new Size(690, 38);
            _lblOverDesc.ForeColor = Color.DimGray;
            gb2.Controls.Add(_lblOverDesc);

            _rbReplace = new RadioButton();
            _rbReplace.Text = "覆盖原文件（直接替换，不可恢复）";
            _rbReplace.Location = new Point(14, 149);
            _rbReplace.Size = new Size(360, 22);
            _rbReplace.ForeColor = Color.Firebrick;
            _rbReplace.CheckedChanged += delegate { if (_rbReplace.Checked) OnOutputModeChanged(); };
            gb2.Controls.Add(_rbReplace);

            _lblReplaceDesc = new Label();
            _lblReplaceDesc.Text = "修复成功后用结果直接替换原文件，文件名与位置不变；此操作不可恢复。\r\n内部会先写临时文件、确认修复成功才替换；失败或停止时原文件保持不动。";
            _lblReplaceDesc.AutoSize = false;
            _lblReplaceDesc.Location = new Point(34, 172);
            _lblReplaceDesc.Size = new Size(690, 38);
            _lblReplaceDesc.ForeColor = Color.Firebrick;
            gb2.Controls.Add(_lblReplaceDesc);

            _chkSkip = new CheckBox();
            _chkSkip.Text = "输出已存在时跳过（不勾选则覆盖）";
            _chkSkip.Location = new Point(14, 216);
            _chkSkip.Size = new Size(360, 22);
            gb2.Controls.Add(_chkSkip);

            // ---- ③ 执行 ----
            var gb4 = new GroupBox();
            gb4.Text = "③ 执行";
            gb4.Location = new Point(12, 364);
            gb4.Size = new Size(736, 140);
            Controls.Add(gb4);

            _btnRun = MakeButton("开始修复", 14, 22, 130, 34);
            _btnRun.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            _btnRun.Click += delegate { RunFix(); };
            gb4.Controls.Add(_btnRun);

            _btnCancel = MakeButton("停止", 154, 22, 90, 34);
            _btnCancel.Enabled = false;
            _btnCancel.Click += delegate { OnCancel(); };
            gb4.Controls.Add(_btnCancel);

            _btnOpen = MakeButton("打开输出目录", 604, 22, 118, 34);
            _btnOpen.Click += delegate { OnOpenOutput(); };
            gb4.Controls.Add(_btnOpen);

            _lblStatus = new Label();
            _lblStatus.Text = "就绪";
            _lblStatus.Location = new Point(14, 60);
            _lblStatus.Size = new Size(708, 18);
            _lblStatus.ForeColor = Color.DimGray;
            gb4.Controls.Add(_lblStatus);

            // 进度条加高到 45px，内部由 ProgressBarSkin 补画两行文字：
            //   总进度：NN%
            //   当前：文件名 (NN%)
            _pgBar = new ProgressBar();
            _pgBar.Location = new Point(14, 78);
            _pgBar.Size = new Size(708, 45);
            _pgBar.Minimum = 0;
            _pgBar.Maximum = 100;
            gb4.Controls.Add(_pgBar);
            _pgSkin = new ProgressBarSkin(_pgBar, _logFont);

            // ---- 日志 ----
            _txtLog = new RichTextBox();
            _txtLog.Location = new Point(12, 516);
            _txtLog.Size = new Size(736, 188);
            _txtLog.ReadOnly = true;
            _txtLog.WordWrap = false;                       // 配合 Both 提供横向滚动条
            _txtLog.ScrollBars = RichTextBoxScrollBars.Both;
            _txtLog.BackColor = Color.White;
            _txtLog.Font = _logFont;
            _txtLog.MaxLength = int.MaxValue;               // 不设上限，执行过程全量展示
            _txtLog.DetectUrls = false;
            Controls.Add(_txtLog);

            // ---- 底部 ----
            _btnFf = MakeButton("检测 / 下载 FFmpeg", 12, 716, 152, 26);
            _btnFf.Click += delegate { OnFetchFfmpeg(); };
            Controls.Add(_btnFf);

            _lblFf = new Label();
            _lblFf.Text = "FFmpeg：检测中...";
            _lblFf.Location = new Point(172, 721);
            _lblFf.Size = new Size(576, 20);
            _lblFf.AutoEllipsis = true;
            Controls.Add(_lblFf);

            // ---- 拖放 ----
            DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
            };
            DragDrop += delegate(object s, DragEventArgs e)
            {
                string[] items = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (items != null && items.Length > 0) ApplyInput(new List<string>(items));
            };
        }

        private static Button MakeButton(string text, int x, int y, int w, int h)
        {
            var b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            return b;
        }

        // ---------------- 跨线程 UI 助手 ----------------

        /// <summary>在 UI 线程上同步执行；已在 UI 线程则直接执行。</summary>
        private void Ui(Action act)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { Invoke(act); }
                catch (InvalidOperationException) { }
            }
            else
            {
                act();
            }
        }

        /// <summary>在 UI 线程上弹消息框，返回用户选择。</summary>
        private DialogResult UiMessage(string text, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            DialogResult r = DialogResult.OK;
            Ui(delegate
            {
                r = MessageBox.Show(this, text, AppConfig.AppName, buttons, icon);
            });
            return r;
        }

        // ---------------- 日志框 ----------------

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_VSCROLL = 0x0115;
        private const int SB_BOTTOM = 7;

        /// <summary>把 RichTextBox 滚到底部。RichTextBox 不会自动滚到底（TextBox 会），
        /// 且每行用 ScrollToCaret 是 O(n²) 的性能杀手，故统一用 WM_VSCROLL(SB_BOTTOM)。</summary>
        private static void ScrollToBottom(RichTextBox rtb)
        {
            try
            {
                if (rtb.IsHandleCreated) SendMessage(rtb.Handle, WM_VSCROLL, (IntPtr)SB_BOTTOM, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>普通信息（默认样式）。</summary>
        private void AppendLog(string text)
        {
            AppendLine(text, CInfo, _logFont);
        }

        private void AppendMuted(string text) { AppendLine(text, CMuted, _logFont); }
        private void AppendWarn(string text) { AppendLine(text, CWarn, _logFont); }
        private void AppendSeparator(string text) { AppendLine(text, CSep, _logFont); }
        private void AppendTask(string text) { AppendLine(text, CTask, _logFont); }
        private void AppendOk(string text) { AppendLine(text, COk, _logFont); }
        private void AppendDegraded(string text) { AppendLine(text, CDegraded, _logFont); }
        private void AppendSkip(string text) { AppendLine(text, CSkip, _logFont); }
        private void AppendFail(string text) { AppendLine(text, CFail, _logFont); }
        private void AppendStopped(string text) { AppendLine(text, CStopped, _logFont); }

        /// <summary>程序级错误：白字红底加粗，视觉上显著区别于业务失败。</summary>
        private void AppendError(string text)
        {
            Ui(delegate
            {
                RichTextBox rtb = _txtLog;
                if (rtb == null) return;
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionLength = 0;
                rtb.SelectionColor = CErrorFore;
                rtb.SelectionBackColor = CErrorBack;
                rtb.SelectionFont = _logFontBold;
                rtb.AppendText(text + "\r\n");
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionLength = 0;
                rtb.SelectionBackColor = rtb.BackColor;
                rtb.SelectionColor = CInfo;
                rtb.SelectionFont = _logFont;
                ScrollToBottom(rtb);
            });
        }

        /// <summary>汇总标题：唯一放大加粗的内容。</summary>
        private void AppendSummaryTitle(string text)
        {
            Ui(delegate
            {
                RichTextBox rtb = _txtLog;
                if (rtb == null) return;
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionLength = 0;
                rtb.SelectionColor = CFail;
                rtb.SelectionBackColor = rtb.BackColor;
                rtb.SelectionFont = _summaryFont;
                rtb.AppendText(text + "\r\n");
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionLength = 0;
                rtb.SelectionColor = CInfo;
                rtb.SelectionFont = _logFont;
                ScrollToBottom(rtb);
            });
        }

        private void AppendSummaryItem(string text) { AppendLine(text, CSumItem, _logFont); }

        private void AppendBlank()
        {
            AppendLine(string.Empty, CInfo, _logFont);
        }

        private void AppendLine(string text, Color color, Font font)
        {
            Ui(delegate { AppendLineCore(text, color, font); });
        }

        private void AppendLineCore(string text, Color color, Font font)
        {
            RichTextBox rtb = _txtLog;
            if (rtb == null) return;
            Font f = (font == null) ? _logFont : font;

            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionLength = 0;
            rtb.SelectionColor = color;
            rtb.SelectionBackColor = rtb.BackColor;
            rtb.SelectionFont = f;
            rtb.AppendText(text + "\r\n");

            // 复位插入点样式，避免下一行继承本次颜色
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionLength = 0;
            rtb.SelectionColor = CInfo;
            rtb.SelectionBackColor = rtb.BackColor;
            rtb.SelectionFont = _logFont;

            ScrollToBottom(rtb);
        }

        private void SetStatus(string text)
        {
            Ui(delegate { _lblStatus.Text = text; });
        }

        private void SetProgress(int pct)
        {
            Ui(delegate
            {
                int v = pct;
                if (v < _pgBar.Minimum) v = _pgBar.Minimum;
                if (v > _pgBar.Maximum) v = _pgBar.Maximum;
                _pgBar.Value = v;
            });
        }

        /// <summary>设置进度条内的两行文字；两参均为 null 表示清空。</summary>
        private void SetProgressText(string line1, string line2)
        {
            ProgressBarSkin skin = _pgSkin;
            if (skin == null) return;
            Ui(delegate
            {
                if (line1 == null && line2 == null) skin.ClearText();
                else skin.SetText(line1, line2);
            });
        }

        /// <summary>启用/禁用一组控件（UI 线程）。</summary>
        private void SetBusy(bool busy)
        {
            Ui(delegate
            {
                _btnRun.Enabled = !busy;
                _btnPickFiles.Enabled = !busy;
                _btnPickDir.Enabled = !busy;
                _btnClear.Enabled = !busy;
                _btnCancel.Enabled = busy;
                _rbSub.Enabled = !busy;
                _rbOver.Enabled = !busy;
                _rbReplace.Enabled = !busy;
            });
        }

        // ---------------- 输出模式 ----------------

        private OutputMode CurrentOutputMode
        {
            get
            {
                if (_rbReplace != null && _rbReplace.Checked) return OutputMode.Overwrite;
                if (_rbOver != null && _rbOver.Checked) return OutputMode.SameDirectory;
                return OutputMode.SubDirectory;
            }
        }

        private void OnOutputModeChanged()
        {
            if (_running) return;
            OutputMode mode = CurrentOutputMode;

            // 覆盖模式下「跳过已存在」无意义（目标就是源文件本身），禁用并取消勾选
            bool overwrite = mode == OutputMode.Overwrite;
            _chkSkip.Enabled = !overwrite;
            if (overwrite) _chkSkip.Checked = false;

            if (mode == OutputMode.SameDirectory)
            {
                AppendLog("输出方式：与源文件同目录，主文件名加 _genpts 后缀，原文件不变。");
            }
            else if (mode == OutputMode.Overwrite)
            {
                AppendLog("输出方式：覆盖原文件。修复成功后直接替换原文件（不可恢复），失败或停止时原文件不动。");
            }
            else
            {
                AppendLog("输出方式：输出到每个源文件所在目录的 genpts 子目录，原文件不变。");
            }
        }

        // ---------------- 输入 ----------------

        private void OnPickFiles()
        {
            var exts = new List<string>();
            for (int i = 0; i < AppConfig.VideoExt.Length; i++) exts.Add("*" + AppConfig.VideoExt[i]);
            string filter = "视频文件|" + string.Join(";", exts.ToArray()) + "|所有文件|*.*";
            string[] files = NativeDialog.PickFiles("选择要修复的视频文件（可多选）", filter, null, Handle);
            if (files != null) ApplyInput(new List<string>(files));
        }

        private void OnPickDir()
        {
            string dir = NativeDialog.PickFolder("选择包含待修复视频的文件夹", null, Handle);
            if (!string.IsNullOrEmpty(dir))
            {
                var list = new List<string>();
                list.Add(dir);
                ApplyInput(list);
            }
        }

        private void OnClear()
        {
            if (_running) return;
            _inputPaths.Clear();
            _scanFiles = new List<string>();
            _lblCount.Text = "尚未选择";
            _lblCount.ForeColor = Color.DimGray;
            SetProgress(0);
            SetProgressText(null, null);
            SetStatus("就绪");
            AppendLog("已清空选择。");
        }

        private void ApplyInput(List<string> paths)
        {
            if (_running) return;

            _inputPaths.Clear();
            _inputPaths.AddRange(paths);

            // 即时阶段只做「本层」快速统计，绝不递归：勾选包含子文件夹时，子目录可能成百上千、
            // 甚至含无权限目录，实时递归既会卡界面，又可能因单个坏目录丢失全部结果。
            // 真正的递归遍历推迟到点击「开始修复」后的后台线程（见 RunFix）。
            var scan = InputScanner.Expand(paths, false);
            _scanFiles = scan.Files;

            int show = Math.Min(3, paths.Count);
            var head = new List<string>();
            for (int i = 0; i < show; i++) head.Add(paths[i]);
            string tail = paths.Count > 3 ? " ..." : "";
            AppendLog("已选择：" + string.Join(" ; ", head.ToArray()) + tail);

            if (_chkSub.Checked)
            {
                // 递归模式：不承诺具体数量，开始时再统计
                _lblCount.Text = "含子文件夹 · 开始时统计";
                _lblCount.ForeColor = Color.DimGray;
                SetStatus("将在点击「开始修复」后遍历子文件夹并统计视频数量");
                AppendLog("已勾选「包含子文件夹」：为避免卡顿，开始修复时才递归遍历统计。");
            }
            else if (_scanFiles.Count == 0)
            {
                _lblCount.Text = "未找到视频文件";
                _lblCount.ForeColor = Color.OrangeRed;
                SetStatus("所选内容里没有可处理的视频");
            }
            else
            {
                _lblCount.Text = string.Format(CultureInfo.InvariantCulture, "已选 {0} 个视频", _scanFiles.Count);
                _lblCount.ForeColor = Color.ForestGreen;
                SetStatus(string.Format(CultureInfo.InvariantCulture, "待处理 {0} 个文件", _scanFiles.Count));
                AppendLog(string.Format(CultureInfo.InvariantCulture, "匹配到 {0} 个视频文件", _scanFiles.Count));
            }
            SetProgress(0);
        }

        // ---------------- FFmpeg ----------------

        private void CheckFfmpegOnStartup()
        {
            // 仅更新界面提示，不写文件日志，避免每次启动产生杂散日志。
            string ff = FfmpegManager.Find();
            if (ff == null)
            {
                AppendWarn("FFmpeg 未检测到，将在首次修复时自动下载。");
            }
        }

        private void UpdateFfmpegLabel()
        {
            string ff = FfmpegManager.Find();
            if (ff != null)
            {
                _lblFf.Text = "FFmpeg：已就绪  ·  " + FfmpegManager.Version(ff);
                _lblFf.ForeColor = Color.ForestGreen;
            }
            else
            {
                _lblFf.Text = "FFmpeg：未检测到（首次修复时自动下载，约 85 ~ 115 MB）";
                _lblFf.ForeColor = Color.OrangeRed;
            }
        }

        private void OnFetchFfmpeg()
        {
            if (_running) return;

            string ff = FfmpegManager.Find();
            if (ff != null)
            {
                AppendLog("FFmpeg：" + ff);
                AppendLog("版本：" + FfmpegManager.Version(ff));
                UpdateFfmpegLabel();
                return;
            }

            _running = true;
            _stopRequested = false;
            SetBusy(true);

            var worker = new System.Threading.Thread(delegate()
            {
                try
                {
                    AppendLog("exe 目录内没有 FFmpeg，开始自动下载...");
                    Log.Open();
                    Log.Info("===== 手动获取 FFmpeg =====");
                    string got = FfmpegManager.Install(
                        delegate(string m) { SetStatus(m); AppendMuted(m); Log.Dl(m); },
                        delegate { return _stopRequested; });
                    AppendOk("FFmpeg 已安装：" + got);
                    AppendOk("版本：" + FfmpegManager.Version(got));
                }
                catch (Exception ex)
                {
                    AppendError("FFmpeg 获取失败：" + ex.Message);
                    Log.Err("FFmpeg 获取失败：" + ex.Message);
                    SetStatus("FFmpeg 获取失败");
                }
                finally
                {
                    Log.Close();
                    SetProgress(0);
                    UpdateFfmpegLabelSafe();
                    _running = false;
                    SetBusy(false);
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void UpdateFfmpegLabelSafe()
        {
            Ui(delegate { UpdateFfmpegLabel(); });
        }

        private string EnsureFfmpeg()
        {
            string ff = FfmpegManager.Find();
            if (ff != null) return ff;

            Log.Warn("[FFmpeg 未检测到，开始自动下载]");
            AppendWarn("FFmpeg 未检测到，开始自动下载（约 85 ~ 115 MB）...");

            ff = FfmpegManager.Install(
                delegate(string m) { SetStatus(m); Log.Dl(m); },
                delegate { return _stopRequested; });

            Log.Ok("[FFmpeg 已安装] " + ff);
            AppendOk("FFmpeg 已就绪：" + ff);
            UpdateFfmpegLabel();
            return ff;
        }

        // ---------------- 执行 ----------------

        private void RunFix()
        {
            if (_running) return;

            if (_inputPaths.Count == 0)
            {
                MessageBox.Show(this, "请先选择要修复的视频文件或文件夹。", AppConfig.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 在 UI 线程快照关键选项
            bool includeSub = _chkSub.Checked;
            OutputMode mode = CurrentOutputMode;
            bool overwrite = mode == OutputMode.Overwrite;
            bool skipExists = !overwrite && _chkSkip.Checked;
            var inputs = new List<string>(_inputPaths);

            _running = true;
            _stopRequested = false;
            SetBusy(true);
            SetProgress(0);
            SetScanUi(true, "正在扫描视频文件...");

            // 扫描（可能遍历海量子目录）与修复都放后台线程，避免界面冻结
            var worker = new System.Threading.Thread(delegate()
            {
                RunScanThenFix(inputs, includeSub, mode, skipExists);
            });
            worker.IsBackground = true;
            worker.Start();
        }

        /// <summary>后台编排：先（必要时递归）扫描，扫完在 UI 线程确认，再修复。</summary>
        private void RunScanThenFix(List<string> inputs, bool includeSub, OutputMode mode, bool skipExists)
        {
            List<string> files;
            int skippedDirs = 0;
            try
            {
                int dirCount = 0;
                DateTime lastUi = DateTime.MinValue;
                var scan = InputScanner.Expand(inputs, includeSub,
                    delegate(string dir)
                    {
                        if (_stopRequested) return;
                        dirCount++;
                        DateTime now = DateTime.Now;
                        // 节流：约每 40 个目录或每 150ms 刷新一次状态，避免海量 Invoke 卡界面
                        if (dirCount % 40 == 0 || (now - lastUi).TotalMilliseconds >= 150)
                        {
                            lastUi = now;
                            SetStatus(string.Format(CultureInfo.InvariantCulture,
                                "正在扫描（已遍历 {0} 个文件夹）：{1}", dirCount, dir));
                        }
                    },
                    delegate { return _stopRequested; });

                files = scan.Files;
                skippedDirs = scan.SkippedDirs;
            }
            catch (Exception ex)
            {
                UiMessage("扫描输入时出错：" + ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Error);
                FinishRunUi();
                return;
            }

            if (_stopRequested)
            {
                AppendStopped("已停止：扫描被用户中断。");
                SetStatus("已停止扫描");
                FinishRunUi();
                return;
            }

            // 扫描结束：进度条恢复为块状，用于后续修复百分比
            SetScanUi(false, null);

            if (files.Count == 0)
            {
                string msg = includeSub
                    ? "所选文件夹（含所有子文件夹）里没有找到可处理的视频文件。"
                    : "所选内容里没有找到可处理的视频文件。";
                if (skippedDirs > 0)
                    msg += string.Format(CultureInfo.InvariantCulture,
                        "\r\n\r\n另有 {0} 个文件夹因无访问权限被跳过。", skippedDirs);
                UiMessage(msg, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("未找到视频文件");
                FinishRunUi();
                return;
            }

            if (skippedDirs > 0)
            {
                AppendWarn(string.Format(CultureInfo.InvariantCulture,
                    "提示：有 {0} 个文件夹因无访问权限被跳过（不影响其余文件夹）。", skippedDirs));
            }
            AppendLog(string.Format(CultureInfo.InvariantCulture, "扫描完成，共找到 {0} 个视频文件。", files.Count));
            _scanFiles = new List<string>(files);
            SetStatus(string.Format(CultureInfo.InvariantCulture, "待处理 {0} 个文件", files.Count));

            // 在 UI 线程弹确认（含覆盖模式的两次危险确认）
            if (!ConfirmRun(files, mode))
            {
                SetStatus("已取消");
                FinishRunUi();
                return;
            }

            RunFixWorker(files, skipExists, mode);
        }

        /// <summary>扫描结束 / 取消后统一复位忙碌状态（后台线程调用）。</summary>
        private void FinishRunUi()
        {
            SetScanUi(false, null);
            _running = false;
            _currentProcess = null;
            SetBusy(false);
        }

        /// <summary>构造预览并在 UI 线程弹确认；返回是否继续。
        /// 弹窗正文由 DialogText 统一构造（只给汇总，不罗列文件清单）。</summary>
        private bool ConfirmRun(List<string> files, OutputMode mode)
        {
            bool overwrite = mode == OutputMode.Overwrite;

            bool ok = false;
            Ui(delegate
            {
                if (overwrite)
                {
                    DialogResult a1 = MessageBox.Show(this, DialogText.ConfirmOverwrite(files, mode),
                        AppConfig.AppName + " — 危险操作确认",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (a1 != DialogResult.Yes) { ok = false; return; }

                    DialogResult a2 = MessageBox.Show(this, DialogText.ConfirmSecond(files.Count),
                        AppConfig.AppName + " — 再次确认",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    ok = (a2 == DialogResult.Yes);
                }
                else
                {
                    DialogResult ans = MessageBox.Show(this, DialogText.ConfirmRun(files, mode),
                        AppConfig.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    ok = (ans == DialogResult.Yes);
                }
            });
            return ok;
        }

        /// <summary>切换扫描阶段的界面：滚动进度条 + 状态文字（扫描期进度条内不显示文字）。</summary>
        private void SetScanUi(bool scanning, string status)
        {
            Ui(delegate
            {
                // Marquee 动画会持续重绘，补画文字会被盖住，因此扫描期一律清空
                if (_pgSkin != null) _pgSkin.ClearText();
                _pgBar.Style = scanning ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
                if (!scanning) _pgBar.Value = 0;
                if (!string.IsNullOrEmpty(status)) _lblStatus.Text = status;
            });
        }

        /// <summary>真正的修复流程（后台线程）。所有 UI 访问必须经由 Ui()/日志助手/SetStatus()。</summary>
        private void RunFixWorker(List<string> files, bool skipExists, OutputMode mode)
        {
            bool overwrite = mode == OutputMode.Overwrite;
            // 清理上次异常退出可能残留的临时文件与空目录（仅动运行目录\temp，幂等）
            try { OutputPlanner.SweepStaleTemp(); } catch { }

            string modeLabel = mode == OutputMode.SameDirectory
                ? "同目录加 _genpts 后缀（源文件不变）"
                : (overwrite ? "覆盖原文件（成功后直接替换，不可恢复）" : "genpts 子目录（源文件不变）");
            Log.Open();
            Log.Info(string.Format(CultureInfo.InvariantCulture, "===== {0} v{1} 开始 =====", AppConfig.AppName, AppConfig.AppVer));
            Log.Info("日志文件：" + Log.FilePath);
            Log.Info("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "待处理 {0} 个视频　输出方式：{1}　跳过已存在：{2}　修复策略：先增强，失败自动降级基础",
                files.Count, modeLabel, skipExists));
            // 完整清单只写日志文件（弹窗不罗列，避免长列表挤掉结论文字）
            for (int i = 0; i < files.Count; i++)
            {
                string dst;
                try { dst = OutputPlanner.FinalPath(files[i], mode); }
                catch { dst = "(无法计算)"; }
                Log.Info(string.Format(CultureInfo.InvariantCulture,
                    "  [{0}/{1}] {2}  ->  {3}", i + 1, files.Count, files[i], dst));
            }
            AppendMuted("日志文件：" + Log.FilePath);
            AppendLog("输出方式：" + modeLabel);

            int okCount = 0, failCount = 0, skipCount = 0, degradedCount = 0, spaceSkipCount = 0;
            var failStats = new Dictionary<string, List<FailureKind>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string ff = EnsureFfmpeg();
                Log.Info("FFmpeg：" + ff);
                string ffVer = FfmpegManager.Version(ff);
                Log.Info("FFmpeg 版本：" + ffVer);
                AppendMuted("FFmpeg：" + ff);

                for (int idx = 0; idx < files.Count; idx++)
                {
                    if (_stopRequested) break;

                    string file = files[idx];
                    string name = Path.GetFileName(file);
                    string tag = string.Format(CultureInfo.InvariantCulture, "[{0}/{1}]", idx + 1, files.Count);
                    string finalPath = OutputPlanner.FinalPath(file, mode);
                    // 覆盖模式：finalPath 即源文件本身，ffmpeg 实际写 temp 下的临时工作文件，成功后再替换
                    string workFile = OutputPlanner.WorkingPath(file, mode);

                    Log.Step(string.Format(CultureInfo.InvariantCulture, "{0} 处理：{1}", tag, file));
                    AppendSeparator(string.Format(CultureInfo.InvariantCulture, "──── {0} {1} ────", tag, name));

                    // 非覆盖模式下工作文件不应等于源文件（genpts 子目录 / 主名加 _genpts），保险再拦一次
                    if (!overwrite && string.Equals(workFile, file, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Err("输出路径与源文件相同，已跳过");
                        AppendFail(string.Format(CultureInfo.InvariantCulture,
                            "{0} {1} 跳过：输出路径与源文件相同", tag, name));
                        RecordFail(failStats, file, FailureKind.Other);
                        failCount++;
                        continue;
                    }
                    if (skipExists && File.Exists(finalPath))
                    {
                        Log.Warn("输出已存在，跳过：" + finalPath);
                        AppendSkip(string.Format(CultureInfo.InvariantCulture,
                            "{0} {1} 跳过：输出已存在", tag, name));
                        skipCount++;
                        continue;
                    }

                    // 磁盘空间预检：目标卷放不下就跳过本条、继续后续任务（不计失败）
                    long needBytes = 0;
                    try { needBytes = new FileInfo(file).Length; } catch { }
                    if (needBytes > 0)
                    {
                        // 覆盖模式：临时文件写在运行目录，最终落回源文件所在卷，两块盘都要够
                        string targetForSpace = overwrite ? AppConfig.Root : finalPath;
                        long freeBytes = SpaceGuard.FreeBytes(targetForSpace);
                        if (!SpaceGuard.HasRoom(targetForSpace, needBytes))
                        {
                            string freeText = FormatSize(freeBytes);
                            Log.Warn(string.Format(CultureInfo.InvariantCulture,
                                "{0} 磁盘空间不足，跳过：需要约 {1}，可用 {2}", tag, FormatSize(needBytes), freeText));
                            AppendSkip(string.Format(CultureInfo.InvariantCulture,
                                "{0} {1} 跳过：磁盘空间不足（需要约 {2}，可用 {3}）",
                                tag, name, FormatSize(needBytes), freeText));
                            spaceSkipCount++;
                            continue;
                        }
                    }

                    Log.Info("输入：" + file);
                    if (overwrite)
                    {
                        Log.Info("临时输出：" + workFile);
                        Log.Info("成功后替换：" + finalPath);
                        AppendMuted("临时文件：" + workFile);
                    }
                    else
                    {
                        Log.Info("输出：" + finalPath);
                        AppendMuted("输出：" + finalPath);
                    }

                    int overall = (int)((long)idx * 100 / files.Count);
                    SetStatus(string.Format(CultureInfo.InvariantCulture, "修复中 {0}...", tag));
                    SetProgress(0);
                    AppendTask(string.Format(CultureInfo.InvariantCulture, "{0} {1} 修复中..", tag, name));
                    SetProgressText(
                        string.Format(CultureInfo.InvariantCulture, "总进度：{0}%", overall),
                        string.Format(CultureInfo.InvariantCulture, "当前：{0} (0%)", name));

                    int lastPct = -1;
                    var result = FixEngine.FixOne(
                        ff, file, workFile,
                        // ffmpeg 原始输出**不再进 GUI**（受众是开发者/管理员），只写文件日志
                        delegate(string line)
                        {
                            if (line == null) return;
                            Log.Ff("    " + line);
                        },
                        delegate(int p)
                        {
                            SetProgress(p);
                            if (p != lastPct)
                            {
                                lastPct = p;
                                SetProgressText(
                                    string.Format(CultureInfo.InvariantCulture, "总进度：{0}%", overall),
                                    string.Format(CultureInfo.InvariantCulture, "当前：{0} ({1}%)", name, p));
                            }
                        },
                        delegate(string m) { Log.Info("  " + m); AppendMuted("  " + m); },
                        delegate { return _stopRequested; },
                        delegate(System.Diagnostics.Process p) { _currentProcess = p; });

                    SetProgressText(null, null);

                    if (result.ExitCode == 0)
                    {
                        string donePath;
                        if (overwrite)
                        {
                            // 用修复结果安全替换原文件；替换失败计失败并清理临时文件，源文件不动
                            try
                            {
                                OutputPlanner.Commit(file, workFile);
                                Log.Ok("已用修复结果覆盖原文件：" + file);
                            }
                            catch (Exception cex)
                            {
                                // 走到这里源文件一定还在（File.Replace 未发生，或兜底已还原备份）。
                                // 保留临时文件以便排查 / 手动恢复，不自动删除。
                                Log.Err("替换原文件失败：" + cex.Message);
                                AppendFail(string.Format(CultureInfo.InvariantCulture,
                                    "{0} {1} 修复失败：修复已完成但替换原文件失败（{2}）", tag, name, cex.Message));
                                AppendMuted("  源文件未改动；修复结果临时文件保留在：" + workFile);
                                RecordFail(failStats, file, FailureClassifier.Classify(result.Lines, result.ExitCode));
                                failCount++;
                                SetProgress(0);
                                continue;
                            }
                            donePath = file;
                        }
                        else
                        {
                            donePath = finalPath;
                        }

                        double mb = 0;
                        try { mb = Math.Round(new FileInfo(donePath).Length / 1048576.0, 1); }
                        catch { }
                        _lastOutput = donePath;
                        try { _lastOutputDir = Path.GetDirectoryName(donePath); }
                        catch { _lastOutputDir = null; }

                        if (result.Degraded)
                        {
                            degradedCount++;
                            Log.Warn(string.Format(CultureInfo.InvariantCulture, "[成功·已降级] {0}（{1} MB）", donePath, mb));
                            AppendDegraded(string.Format(CultureInfo.InvariantCulture,
                                "{0} {1} 已降级完成（{2} MB）{3}",
                                tag, name, mb, overwrite ? "，原文件已被替换" : ""));
                        }
                        else
                        {
                            Log.Ok(string.Format(CultureInfo.InvariantCulture, "[成功] {0}（{1} MB）", donePath, mb));
                            AppendOk(string.Format(CultureInfo.InvariantCulture,
                                "{0} {1} 修复完成（{2} MB）{3}",
                                tag, name, mb, overwrite ? "，原文件已覆盖" : ""));
                        }
                        okCount++;
                        SetProgress(100);
                    }
                    else if (result.ExitCode == FixEngine.StoppedExitCode)
                    {
                        if (overwrite) OutputPlanner.DiscardWorking(workFile, finalPath, mode);
                        Log.Warn("当前文件处理被用户停止。");
                        AppendStopped(string.Format(CultureInfo.InvariantCulture,
                            "{0} {1} 已停止：处理被中断，不计入失败，源文件未改动", tag, name));
                        SetProgress(0);
                        break;
                    }
                    else
                    {
                        if (overwrite) OutputPlanner.DiscardWorking(workFile, finalPath, mode);
                        FailureKind kind = FailureClassifier.Classify(result.Lines, result.ExitCode);
                        string reason = FailureClassifier.Describe(kind);
                        Log.Err(string.Format(CultureInfo.InvariantCulture,
                            "[失败] 增强与基础模式均失败，退出码 {0}，原因：{1}（{2}）",
                            result.ExitCode, reason, kind.ToString()));
                        AppendFail(string.Format(CultureInfo.InvariantCulture,
                            "{0} {1} 修复失败：{2}（源文件未改动）", tag, name, reason));

                        // 详细 stderr 只进文件日志；GUI 仅在汇总里给条数
                        string[] lastLines = FixEngine.Tail(result.Lines, 10);
                        for (int i = 0; i < lastLines.Length; i++)
                        {
                            Log.FfErr("    " + lastLines[i]);
                        }
                        RecordFail(failStats, file, kind);
                        failCount++;
                        SetProgress(0);
                    }
                }

                ReportFailures(failStats);

                // 结束汇总必须写进「执行过程展示框」——最终用户只看这个框，
                // 状态栏那行字只是附属提示，不能作为唯一出口。
                int skippedTotal = skipCount + spaceSkipCount;
                int pending = Math.Max(0, files.Count - okCount - failCount - skippedTotal);
                string[] sumLines = DialogText.SummaryLines(okCount, failCount, skippedTotal,
                    degradedCount, spaceSkipCount, _stopRequested, pending);
                AppendBlank();
                for (int i = 0; i < sumLines.Length; i++)
                {
                    if (i == 0) AppendSummaryTitle(sumLines[i]);
                    else AppendSummaryItem(sumLines[i]);
                }

                if (_stopRequested)
                {
                    SetStatus(string.Format(CultureInfo.InvariantCulture,
                        "已停止：成功 {0} · 失败 {1} · 跳过 {2}", okCount, failCount, skippedTotal));
                    Log.Warn(string.Format(CultureInfo.InvariantCulture,
                        "===== 用户停止：成功 {0} · 失败 {1} · 跳过 {2}（其中空间不足 {3}） =====",
                        okCount, failCount, skipCount, spaceSkipCount));
                    AppendStopped("已停止：处理被用户中断。");
                }
                else
                {
                    SetStatus(string.Format(CultureInfo.InvariantCulture,
                        "完成：成功 {0} · 失败 {1} · 跳过 {2}", okCount, failCount, skippedTotal));
                    Log.Info(string.Format(CultureInfo.InvariantCulture,
                        "===== 处理结束：成功 {0} · 失败 {1} · 跳过 {2} · 其中降级成功 {3} · 空间不足跳过 {4} =====",
                        okCount, failCount, skipCount, degradedCount, spaceSkipCount));

                    ShowSummary(okCount, failCount, skippedTotal, degradedCount, spaceSkipCount, failStats);
                }
            }
            catch (Exception ex)
            {
                Log.Err("[错误] " + ex.Message);
                Log.Err(ex.ToString());
                AppendError("程序错误：" + ex.Message);
                SetStatus("出错");
                UiMessage(ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetProgressText(null, null);
                // 整个队列处理完毕（正常结束 / 用户停止 / 出错）后，一次性清理 temp：
                // 删除残留临时文件并自底向上删空目录，直到 temp 根本身。
                try { OutputPlanner.CleanupTempRoot(); } catch { }
                Log.Close();
                _running = false;
                _currentProcess = null;
                SetBusy(false);
            }
        }

        // ---------------- 失败汇总 ----------------

        /// <summary>记录一次失败（同一文件重复失败只记第一次原因）。</summary>
        private static void RecordFail(Dictionary<string, List<FailureKind>> stats, string file, FailureKind kind)
        {
            if (stats == null || string.IsNullOrEmpty(file)) return;
            List<FailureKind> list;
            if (!stats.TryGetValue(file, out list))
            {
                list = new List<FailureKind>();
                stats[file] = list;
            }
            list.Add(kind);
        }

        /// <summary>按原因分组输出失败汇总：LOG 文件给完整文件列表，GUI 只给条数。</summary>
        private void ReportFailures(Dictionary<string, List<FailureKind>> failStats)
        {
            if (failStats == null || failStats.Count == 0) return;

            var byKind = new Dictionary<FailureKind, List<string>>();
            foreach (KeyValuePair<string, List<FailureKind>> kv in failStats)
            {
                FailureKind k = (kv.Value != null && kv.Value.Count > 0) ? kv.Value[0] : FailureKind.Other;
                List<string> list;
                if (!byKind.TryGetValue(k, out list))
                {
                    list = new List<string>();
                    byKind[k] = list;
                }
                list.Add(kv.Key);
            }

            FailureKind[] order = FailureClassifier.AllKinds();

            // ---- LOG 文件：完整明细，按原因分组逐条列出 ----
            Log.Err(string.Format(CultureInfo.InvariantCulture,
                "===== 失败汇总：共 {0} 个文件 =====", failStats.Count));
            for (int i = 0; i < order.Length; i++)
            {
                List<string> list;
                if (!byKind.TryGetValue(order[i], out list)) continue;
                Log.Err(string.Format(CultureInfo.InvariantCulture,
                    "  因{0}失败 {1} 个：", FailureClassifier.Describe(order[i]), list.Count));
                for (int j = 0; j < list.Count; j++) Log.Err("    " + list[j]);
            }

            // ---- GUI：只给条数 ----
            AppendBlank();
            AppendSummaryTitle(string.Format(CultureInfo.InvariantCulture,
                "失败汇总：共 {0} 个文件失败", failStats.Count));
            for (int i = 0; i < order.Length; i++)
            {
                List<string> list;
                if (!byKind.TryGetValue(order[i], out list)) continue;
                AppendSummaryItem(string.Format(CultureInfo.InvariantCulture,
                    "    因{0}失败 {1} 个", FailureClassifier.Describe(order[i]), list.Count));
            }
            AppendSummaryItem("    详细文件列表见日志文件。");
        }

        private void ShowSummary(int okCount, int failCount, int skipCount, int degradedCount,
            int spaceSkipCount, Dictionary<string, List<FailureKind>> failStats)
        {
            var extra = new List<string>();
            if (degradedCount > 0)
                extra.Add(string.Format(CultureInfo.InvariantCulture, "其中 {0} 个为增强失败后降级完成", degradedCount));
            if (spaceSkipCount > 0)
                extra.Add(string.Format(CultureInfo.InvariantCulture, "跳过中有 {0} 个因磁盘空间不足", spaceSkipCount));

            string head = string.Format(CultureInfo.InvariantCulture,
                "处理结束：成功 {0} · 失败 {1} · 跳过 {2}。", okCount, failCount, skipCount);
            if (extra.Count > 0) head += "\r\n" + string.Join("\r\n", extra.ToArray());

            if (failCount == 0)
            {
                UiMessage(DialogText.SummaryOk(okCount, degradedCount, skipCount, _lastOutputDir, Log.FilePath),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                // 按原因分组的条数（不罗列文件）
                var byKind = new Dictionary<FailureKind, int>();
                if (failStats != null)
                {
                    foreach (KeyValuePair<string, List<FailureKind>> kv in failStats)
                    {
                        FailureKind k = (kv.Value != null && kv.Value.Count > 0) ? kv.Value[0] : FailureKind.Other;
                        int c;
                        byKind.TryGetValue(k, out c);
                        byKind[k] = c + 1;
                    }
                }
                UiMessage(DialogText.SummaryFail(head, byKind, Log.FilePath),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 0) return "未知";
            double mb = bytes / 1048576.0;
            if (mb >= 1024.0)
                return (mb / 1024.0).ToString("N2", CultureInfo.InvariantCulture) + " GB";
            return mb.ToString("N1", CultureInfo.InvariantCulture) + " MB";
        }

        private void OnCancel()
        {
            _stopRequested = true;
            var p = _currentProcess;
            if (p != null)
            {
                try { p.Kill(); } catch { }
            }
            AppendStopped("已发送停止指令。");
        }

        private void OnOpenOutput()
        {
            string dir = null;
            if (!string.IsNullOrEmpty(_lastOutput) && File.Exists(_lastOutput))
            {
                dir = Path.GetDirectoryName(_lastOutput);
            }
            else if (_inputPaths.Count > 0)
            {
                string p0 = _inputPaths[0];
                string baseDir = Directory.Exists(p0) ? p0 : Path.GetDirectoryName(p0);
                if (!string.IsNullOrEmpty(baseDir))
                {
                    if (CurrentOutputMode == OutputMode.SubDirectory)
                    {
                        string cand = Path.Combine(baseDir, AppConfig.OutSubDir);
                        dir = Directory.Exists(cand) ? cand : baseDir;
                    }
                    else
                    {
                        dir = baseDir;
                    }
                }
            }

            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\""); }
                catch (Exception ex) { AppendError("打开目录失败：" + ex.Message); }
            }
            else
            {
                MessageBox.Show("还没有可打开的输出目录。", AppConfig.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running)
            {
                DialogResult r = MessageBox.Show("正在修复中，确定要退出吗？", AppConfig.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
                _stopRequested = true;
                var p = _currentProcess;
                if (p != null) { try { p.Kill(); } catch { } }
            }
            base.OnFormClosing(e);
        }
    }
}
