using System;
using System.Drawing;
using System.Windows.Forms;

namespace FixVideoTimeLine
{
    /// <summary>
    /// 给原生 ProgressBar 叠加两行文字：子类化其窗口过程，在原生绘制完成之后补画。
    /// 这样可 100% 保留原生外观（含 Marquee 动画与真实配色），文字直接压在进度条内部。
    /// 注意：Marquee 动画会持续重绘，叠加文字会被不断覆盖，故仅在 Blocks 模式下补画。
    /// </summary>
    internal class ProgressBarSkin : NativeWindow, IDisposable
    {
        private const int WM_PAINT = 0x000F;

        private readonly ProgressBar _bar;
        private readonly Font _font;
        private readonly int _lineHeight;

        private string _line1;
        private string _line2;
        private string _show1;
        private string _show2;
        private bool _disposed;

        public ProgressBarSkin(ProgressBar bar, Font font)
        {
            _bar = bar;
            _font = font;
            // 用 TextRenderer 实测行高，自动适配 DPI；过矮时按下限兜底
            int h;
            try { h = TextRenderer.MeasureText("国Ag", font).Height; }
            catch { h = font.Height; }
            _lineHeight = Math.Max(12, h);

            _bar.HandleCreated += delegate { AssignHandle(_bar.Handle); };
            if (_bar.IsHandleCreated) AssignHandle(_bar.Handle);
        }

        /// <summary>文字可用宽度（随控件实际宽度动态取值，避免 DPI / 布局变化后失准）。</summary>
        private int TextMaxWidth
        {
            get
            {
                try { return Math.Max(40, _bar.ClientSize.Width - 16); }
                catch { return 40; }
            }
        }

        /// <summary>设置两行文字（超长自动中间省略）。</summary>
        public void SetText(string line1, string line2)
        {
            if (_line1 == line1 && _line2 == line2 && _show1 != null) return;
            _line1 = line1;
            _line2 = line2;
            int maxW = TextMaxWidth;
            _show1 = EllipsizeMiddle(line1, _font, maxW);
            _show2 = EllipsizeMiddle(line2, _font, maxW);
            Redraw();
        }

        /// <summary>清空文字（扫描等 Marquee 阶段使用）。</summary>
        public void ClearText()
        {
            SetText(null, null);
        }

        public void Redraw()
        {
            try { if (_bar.IsHandleCreated) _bar.Invalidate(); }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg != WM_PAINT) return;
            if (_bar.Style != ProgressBarStyle.Blocks) return;   // Marquee 动画会盖掉文字，不画
            if (string.IsNullOrEmpty(_show1) && string.IsNullOrEmpty(_show2)) return;

            try
            {
                using (Graphics g = Graphics.FromHwnd(_bar.Handle))
                {
                    int w = _bar.ClientSize.Width;
                    int h = _bar.ClientSize.Height;
                    int lineH = _lineHeight;
                    if (lineH * 2 > h) lineH = h / 2;            // 进度条过矮时压缩
                    int y1 = (h - lineH * 2) / 2;

                    if (!string.IsNullOrEmpty(_show1)) DrawLine(g, _show1, y1, w, lineH);
                    if (!string.IsNullOrEmpty(_show2)) DrawLine(g, _show2, y1 + lineH, w, lineH);
                }
            }
            catch { }
        }

        private void DrawLine(Graphics g, string text, int y, int width, int height)
        {
            TextRenderer.DrawText(g, text, _font, new Rectangle(0, y, width, height), Color.Black,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        // ---------------- 中间省略 ----------------

        /// <summary>
        /// 宽度不足时保留首尾、中间用 "..." 省略（如「很长_2026...最终版.mp4」）。
        /// 用二分找出可容纳的最大保留字符数，避免逐字符测量的线性开销。
        /// </summary>
        internal static string EllipsizeMiddle(string text, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (font == null || maxWidth <= 0) return text;
            if (TextRenderer.MeasureText(text, font).Width <= maxWidth) return text;

            const string Dots = "...";
            int dotsW = TextRenderer.MeasureText(Dots, font).Width;
            int avail = maxWidth - dotsW;
            if (avail <= 0) return Dots;

            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Fits(text, font, mid, avail)) lo = mid; else hi = mid - 1;
            }

            int keep = lo;
            if (keep <= 0) return Dots;
            if (keep >= text.Length) return text;

            int head = (keep + 1) / 2;
            int tail = keep - head;
            if (tail <= 0) return text.Substring(0, head) + Dots;
            return text.Substring(0, head) + Dots + text.Substring(text.Length - tail);
        }

        private static bool Fits(string text, Font font, int keep, int avail)
        {
            int head = (keep + 1) / 2;
            int tail = keep - head;
            string s = (tail <= 0)
                ? text.Substring(0, head)
                : text.Substring(0, head) + text.Substring(text.Length - tail);
            return TextRenderer.MeasureText(s, font).Width <= avail;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { ReleaseHandle(); } catch { }
        }
    }
}
