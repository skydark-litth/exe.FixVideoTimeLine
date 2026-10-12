using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
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
        private TextBox _txtLog;

        // ---- 状态 ----
        private readonly List<string> _inputPaths = new List<string>();
        private List<string> _scanFiles = new List<string>();
        private string _lastOutput;
        private volatile bool _stopRequested;
        private volatile System.Diagnostics.Process _currentProcess;
        private bool _running;

        public MainForm()
        {
            InitializeUi();

            UpdateFfmpegLabel();
            AppendLog("提示：可直接把视频文件或文件夹拖入本窗口；在「② 输出」选择修复结果的存放方式。");

            Shown += delegate { CheckFfmpegOnStartup(); };
        }

        private void InitializeUi()
        {
            Text = AppConfig.AppName + "  v" + AppConfig.AppVer;
            ClientSize = new Size(760, 736);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
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
            gb4.Size = new Size(736, 110);
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

            _pgBar = new ProgressBar();
            _pgBar.Location = new Point(14, 80);
            _pgBar.Size = new Size(708, 18);
            _pgBar.Minimum = 0;
            _pgBar.Maximum = 100;
            gb4.Controls.Add(_pgBar);

            // ---- 日志 ----
            _txtLog = new TextBox();
            _txtLog.Location = new Point(12, 482);
            _txtLog.Size = new Size(736, 222);
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Color.White;
            _txtLog.Font = new Font("Consolas", 9f);
            Controls.Add(_txtLog);

            // ---- 底部 ----
            _btnFf = MakeButton("检测 / 下载 FFmpeg", 12, 708, 152, 26);
            _btnFf.Click += delegate { OnFetchFfmpeg(); };
            Controls.Add(_btnFf);

            _lblFf = new Label();
            _lblFf.Text = "FFmpeg：检测中...";
            _lblFf.Location = new Point(172, 713);
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

        private void AppendLog(string text)
        {
            Ui(delegate
            {
                _txtLog.AppendText(text + "\r\n");
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.ScrollToCaret();
            });
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
            // 仅更新底部状态，不写文件日志，避免每次启动产生杂散日志。
            string ff = FfmpegManager.Find();
            if (ff == null)
            {
                AppendLog("FFmpeg 未检测到，将在首次修复时自动下载");
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
                        delegate(string m) { SetStatus(m); AppendLog(m); Log.Dl(m); },
                        delegate { return _stopRequested; });
                    AppendLog("FFmpeg 已安装：" + got);
                    AppendLog("版本：" + FfmpegManager.Version(got));
                }
                catch (Exception ex)
                {
                    AppendLog("[错误] " + ex.Message);
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
            AppendLog("[FFmpeg 未检测到，开始自动下载]");

            ff = FfmpegManager.Install(
                delegate(string m) { SetStatus(m); Log.Dl(m); },
                delegate { return _stopRequested; });

            Log.Ok("[FFmpeg 已安装] " + ff);
            AppendLog("[FFmpeg 已安装] " + ff);
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
                AppendLog("[已停止] 扫描被用户中断。");
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
                AppendLog(string.Format(CultureInfo.InvariantCulture,
                    "[提示] 有 {0} 个文件夹因无访问权限被跳过（不影响其余文件夹）。", skippedDirs));
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

        /// <summary>构造预览并在 UI 线程弹确认；返回是否继续。</summary>
        private bool ConfirmRun(List<string> files, OutputMode mode)
        {
            bool overwrite = mode == OutputMode.Overwrite;

            var preview = new List<string>();
            int pv = Math.Min(6, files.Count);
            for (int i = 0; i < pv; i++)
            {
                if (overwrite)
                {
                    preview.Add("  " + files[i] + "  ->  覆盖（同名同位置替换）");
                }
                else
                {
                    string target = OutputPlanner.FinalPath(files[i], mode);
                    preview.Add("  " + files[i] + "  ->  " + target);
                }
            }
            string previewText = string.Join("\r\n", preview.ToArray());
            if (files.Count > 6)
                previewText += string.Format(CultureInfo.InvariantCulture, "\r\n  ...（其余 {0} 个同理）", files.Count - 6);

            bool ok = false;
            Ui(delegate
            {
                if (overwrite)
                {
                    string warn1 = string.Format(CultureInfo.InvariantCulture,
                        "即将处理 {0} 个视频，输出方式：覆盖原文件。\r\n\r\n{1}\r\n\r\n" +
                        "【警告】修复成功后会用结果直接替换原文件，文件名与位置不变，此操作不可恢复！\r\n" +
                        "程序会先写临时文件、确认修复成功后才替换；失败或停止时原文件不动。\r\n\r\n确定要覆盖原文件吗？",
                        files.Count, previewText);
                    DialogResult a1 = MessageBox.Show(this, warn1, AppConfig.AppName + " — 危险操作确认",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (a1 != DialogResult.Yes) { ok = false; return; }

                    string warn2 = string.Format(CultureInfo.InvariantCulture,
                        "请再次确认：将直接覆盖 {0} 个原视频文件，覆盖后无法找回原文件。\r\n建议重要文件先自行备份。\r\n\r\n真的要继续吗？",
                        files.Count);
                    DialogResult a2 = MessageBox.Show(this, warn2, AppConfig.AppName + " — 再次确认",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    ok = (a2 == DialogResult.Yes);
                }
                else
                {
                    string modeText = mode == OutputMode.SameDirectory
                        ? "与源文件同目录（文件名加 _genpts）"
                        : "输出到 genpts 子目录";
                    string ask = string.Format(CultureInfo.InvariantCulture,
                        "即将处理 {0} 个视频，输出方式：{1}\r\n\r\n{2}\r\n\r\n源文件不会被修改。是否开始？",
                        files.Count, modeText, previewText);
                    DialogResult ans = MessageBox.Show(this, ask, AppConfig.AppName,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    ok = (ans == DialogResult.Yes);
                }
            });
            return ok;
        }

        /// <summary>切换扫描阶段的界面：滚动进度条 + 状态文字。</summary>
        private void SetScanUi(bool scanning, string status)
        {
            Ui(delegate
            {
                _pgBar.Style = scanning ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
                if (!scanning) _pgBar.Value = 0;
                if (!string.IsNullOrEmpty(status)) _lblStatus.Text = status;
            });
        }

        /// <summary>真正的修复流程（后台线程）。所有 UI 访问必须经由 Ui()/AppendLog()/SetStatus()。</summary>
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
            AppendLog("[日志文件] " + Log.FilePath);
            AppendLog("输出方式：" + modeLabel);

            int okCount = 0, failCount = 0, skipCount = 0, degradedCount = 0;

            try
            {
                string ff = EnsureFfmpeg();
                Log.Info("FFmpeg：" + ff);
                string ffVer = FfmpegManager.Version(ff);
                Log.Info("FFmpeg 版本：" + ffVer);
                AppendLog("[FFmpeg] " + ff);

                for (int idx = 0; idx < files.Count; idx++)
                {
                    if (_stopRequested) break;

                    string file = files[idx];
                    string tag = string.Format(CultureInfo.InvariantCulture, "[{0}/{1}]", idx + 1, files.Count);
                    string finalPath = OutputPlanner.FinalPath(file, mode);
                    // 覆盖模式：finalPath 即源文件本身，ffmpeg 实际写同目录临时工作文件，成功后再替换
                    string workFile = OutputPlanner.WorkingPath(file, mode);

                    Log.Step(string.Format(CultureInfo.InvariantCulture, "{0} 处理：{1}", tag, file));
                    AppendLog("");
                    AppendLog(string.Format(CultureInfo.InvariantCulture, "──── {0} {1} ────", tag, Path.GetFileName(file)));

                    // 非覆盖模式下工作文件不应等于源文件（genpts 子目录 / 主名加 _genpts），保险再拦一次
                    if (!overwrite && string.Equals(workFile, file, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Err("输出路径与源文件相同，已跳过");
                        AppendLog("[跳过] 输出路径与源文件相同");
                        failCount++;
                        continue;
                    }
                    if (skipExists && File.Exists(finalPath))
                    {
                        Log.Warn("输出已存在，跳过：" + finalPath);
                        AppendLog("[跳过] 输出已存在：" + finalPath);
                        skipCount++;
                        continue;
                    }

                    Log.Info("输入：" + file);
                    if (overwrite)
                    {
                        Log.Info("临时输出：" + workFile);
                        Log.Info("成功后替换：" + finalPath);
                        AppendLog("临时文件：" + workFile);
                    }
                    else
                    {
                        Log.Info("输出：" + finalPath);
                        AppendLog("输出：" + finalPath);
                    }
                    SetStatus(string.Format(CultureInfo.InvariantCulture, "修复中 {0}...", tag));
                    SetProgress(0);

                    var result = FixEngine.FixOne(
                        ff, file, workFile,
                        delegate(string line)
                        {
                            if (FixEngine.IsProgressLine(line)) return;
                            string t = line.Trim();
                            if (t.Length != 0)
                            {
                                AppendLog("  " + t);
                                Log.Ff("    " + t);
                            }
                        },
                        delegate(int p) { SetProgress(p); },
                        delegate(string m) { AppendLog("  " + m); },
                        delegate { return _stopRequested; },
                        delegate(System.Diagnostics.Process p) { _currentProcess = p; });

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
                                AppendLog("[失败] 修复已完成，但替换原文件失败：" + cex.Message);
                                AppendLog("源文件未改动；修复结果临时文件保留在：" + workFile);
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

                        if (result.Degraded)
                        {
                            degradedCount++;
                            Log.Warn(string.Format(CultureInfo.InvariantCulture, "[成功·已降级] {0}（{1} MB）", donePath, mb));
                            AppendLog(string.Format(CultureInfo.InvariantCulture,
                                "[成功·增强模式失败已降级基础] {0}（{1} MB）{2}",
                                donePath, mb, overwrite ? "，原文件已被替换" : ""));
                        }
                        else
                        {
                            Log.Ok(string.Format(CultureInfo.InvariantCulture, "[成功] {0}（{1} MB）", donePath, mb));
                            AppendLog(string.Format(CultureInfo.InvariantCulture,
                                "[成功] {0}（{1} MB）{2}",
                                donePath, mb, overwrite ? "，原文件已被覆盖" : ""));
                        }
                        okCount++;
                        SetProgress(100);
                    }
                    else if (result.ExitCode == FixEngine.StoppedExitCode)
                    {
                        if (overwrite) OutputPlanner.DiscardWorking(workFile, finalPath, mode);
                        Log.Warn("当前文件处理被用户停止。");
                        AppendLog("[已停止] 当前文件处理被中断，不计入失败，源文件未改动。");
                        SetProgress(0);
                        break;
                    }
                    else
                    {
                        if (overwrite) OutputPlanner.DiscardWorking(workFile, finalPath, mode);
                        Log.Err(string.Format(CultureInfo.InvariantCulture, "[失败] 增强与基础模式均失败，退出码 {0}", result.ExitCode));
                        AppendLog(string.Format(CultureInfo.InvariantCulture, "[失败] 增强与基础模式均失败，FFmpeg 退出码 {0}，源文件未改动", result.ExitCode));
                        string[] lastLines = FixEngine.Tail(result.Lines, 10);
                        for (int i = 0; i < lastLines.Length; i++)
                        {
                            Log.FfErr("    " + lastLines[i]);
                            AppendLog("    " + lastLines[i]);
                        }
                        AppendLog("[建议] 若源为 RMVB/AVI/WMV 等容器，可能目标容器不支持其编码。");
                        failCount++;
                        SetProgress(0);
                    }
                }

                if (_stopRequested)
                {
                    SetStatus(string.Format(CultureInfo.InvariantCulture,
                        "已停止：成功 {0} · 失败 {1} · 跳过 {2}", okCount, failCount, skipCount));
                    Log.Warn(string.Format(CultureInfo.InvariantCulture,
                        "===== 用户停止：成功 {0} · 失败 {1} · 跳过 {2} =====", okCount, failCount, skipCount));
                    AppendLog("[已停止] 处理被用户中断。");
                }
                else
                {
                    SetStatus(string.Format(CultureInfo.InvariantCulture,
                        "完成：成功 {0} · 失败 {1} · 跳过 {2}", okCount, failCount, skipCount));
                    Log.Info(string.Format(CultureInfo.InvariantCulture,
                        "===== 处理结束：成功 {0} · 失败 {1} · 跳过 {2} · 其中降级成功 {3} =====",
                        okCount, failCount, skipCount, degradedCount));

                    ShowSummary(okCount, failCount, skipCount, degradedCount);
                }
            }
            catch (Exception ex)
            {
                Log.Err("[错误] " + ex.Message);
                AppendLog("[错误] " + ex.Message);
                SetStatus("出错");
                UiMessage(ex.Message, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // 整个队列处理完毕（正常结束 / 用户停止 / 出错）后，一次性清理 temp：
                // 删除残留临时文件并自底向上删空目录，直到 temp 根本身。
                try { OutputPlanner.CleanupTempRoot(); } catch { }
                Log.Close();
                _running = false;
                _currentProcess = null;
                SetBusy(false);
            }
        }

        private void ShowSummary(int okCount, int failCount, int skipCount, int degradedCount)
        {
            if (failCount == 0)
            {
                string msg = string.Format(CultureInfo.InvariantCulture, "处理完成：成功 {0} 个。", okCount);
                if (degradedCount > 0)
                    msg += string.Format(CultureInfo.InvariantCulture, "（其中 {0} 个为增强失败后降级完成）", degradedCount);
                if (skipCount > 0)
                    msg += string.Format(CultureInfo.InvariantCulture, "跳过 {0} 个。", skipCount);
                if (!string.IsNullOrEmpty(_lastOutput))
                    msg += "\r\n\r\n最近输出：\r\n" + _lastOutput;
                msg += "\r\n\r\n日志：\r\n" + Log.FilePath;
                UiMessage(msg, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                UiMessage(string.Format(CultureInfo.InvariantCulture,
                    "处理结束：成功 {0} · 失败 {1} · 跳过 {2}。\r\n详情见下方日志与日志文件。",
                    okCount, failCount, skipCount),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnCancel()
        {
            _stopRequested = true;
            var p = _currentProcess;
            if (p != null)
            {
                try { p.Kill(); } catch { }
            }
            AppendLog("[已发送停止指令]");
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
                catch (Exception ex) { AppendLog("[错误] 打开目录失败：" + ex.Message); }
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
