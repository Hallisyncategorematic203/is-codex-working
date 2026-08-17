using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace IsCodexWorking
{
    internal static class StatusPalette
    {
        public static Color ColorFor(PublicState state)
        {
            switch (state)
            {
                case PublicState.Working: return Color.FromArgb(36, 204, 112);
                case PublicState.WaitingForYou: return Color.FromArgb(224, 170, 24);
                case PublicState.Stuck: return Color.FromArgb(239, 130, 43);
                case PublicState.Done: return Color.FromArgb(64, 151, 218);
                case PublicState.LimitReached: return Color.FromArgb(218, 74, 74);
                case PublicState.Error: return Color.FromArgb(205, 61, 61);
                default: return Color.FromArgb(151, 160, 171);
            }
        }

        public static Color TrayColorFor(PublicState state)
        {
            return ColorFor(state);
        }

        public static string LabelFor(PublicState state)
        {
            return PublicCopy.TitleFor(state);
        }

        public static string SubtitleFor(PublicState state)
        {
            return PublicCopy.SubtitleFor(state);
        }
    }

    internal static class StatusPainter
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon CreateTrayIcon(PublicState state)
        {
            using (Bitmap bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                DrawTrayGlyph(g, state, new RectangleF(2, 3, 28, 26));
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle)) return (Icon)temp.Clone();
                }
                finally { DestroyIcon(handle); }
            }
        }

        private static void DrawTrayGlyph(Graphics g, PublicState state, RectangleF rect)
        {
            Color color = StatusPalette.TrayColorFor(state);
            using (Pen glyph = new Pen(color, HeartbeatStrokeWidth(rect)))
            {
                glyph.StartCap = LineCap.Round;
                glyph.EndCap = LineCap.Round;
                glyph.LineJoin = LineJoin.Round;
                DrawHeartbeat(g, glyph, rect);
            }
        }

        private static float HeartbeatStrokeWidth(RectangleF rect)
        {
            return Math.Max(3.2f, Math.Min(4.2f, rect.Width / 8.2f));
        }

        private static void DrawHeartbeat(Graphics g, Pen glyph, RectangleF rect)
        {
            float l = rect.Left, t = rect.Top, w = rect.Width, h = rect.Height;
            PointF[] pts = new PointF[]
            {
                new PointF(l+w*0.04f,t+h*0.55f), new PointF(l+w*0.24f,t+h*0.55f),
                new PointF(l+w*0.36f,t+h*0.22f), new PointF(l+w*0.49f,t+h*0.82f),
                new PointF(l+w*0.62f,t+h*0.39f), new PointF(l+w*0.73f,t+h*0.55f),
                new PointF(l+w*0.96f,t+h*0.55f)
            };
            g.DrawLines(glyph, pts);
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            int r = (int)(from.R + (to.R - from.R) * amount);
            int g = (int)(from.G + (to.G - from.G) * amount);
            int b = (int)(from.B + (to.B - from.B) * amount);
            return Color.FromArgb(r, g, b);
        }

        public static void Draw(Graphics g, PublicState state, RectangleF rect, bool active)
        {
            Color baseColor = StatusPalette.ColorFor(state);
            Color main = active ? baseColor : Blend(baseColor, Color.White, 0.58f);
            Color fill = active ? Color.FromArgb(22, baseColor) : Color.FromArgb(252, 252, 253);
            float penWidth = Math.Max(2.15f, rect.Width / 13f);
            // The non-heartbeat symbols need a small optical compensation at
            // popup size; all seven now share the same visible weight family.
            float glyphWidth = state == PublicState.Working ? penWidth : penWidth * 1.08f;
            float ringWidth = active ? Math.Max(penWidth, glyphWidth * 0.92f) : Math.Max(1.65f, penWidth * 0.82f);
            using (Brush fillBrush = new SolidBrush(fill))
            using (Pen ring = new Pen(main, ringWidth))
            using (Pen glyph = new Pen(main, glyphWidth))
            {
                glyph.StartCap = LineCap.Round;
                glyph.EndCap = LineCap.Round;
                glyph.LineJoin = LineJoin.Round;

                if (state == PublicState.Error)
                {
                    PointF a = new PointF(rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.12f);
                    PointF b = new PointF(rect.Left + rect.Width * 0.88f, rect.Top + rect.Height * 0.82f);
                    PointF c = new PointF(rect.Left + rect.Width * 0.12f, rect.Top + rect.Height * 0.82f);
                    g.FillPolygon(fillBrush, new PointF[] { a, b, c });
                    g.DrawPolygon(ring, new PointF[] { a, b, c });
                    g.DrawLine(glyph, rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.36f,
                        rect.Left + rect.Width * 0.50f, rect.Top + rect.Height * 0.60f);
                    using (Brush dot = new SolidBrush(main))
                        g.FillEllipse(dot, rect.Left + rect.Width * 0.46f, rect.Top + rect.Height * 0.68f,
                            rect.Width * 0.08f, rect.Width * 0.08f);
                    return;
                }

                g.FillEllipse(fillBrush, rect);
                g.DrawEllipse(ring, rect);

                float l = rect.Left, t = rect.Top, w = rect.Width, h = rect.Height;
                if (state == PublicState.Working)
                {
                    DrawHeartbeat(g, glyph, rect);
                }
                else if (state == PublicState.WaitingForYou)
                {
                    g.DrawEllipse(glyph, l+w*0.27f, t+h*0.27f, w*0.46f, h*0.46f);
                    g.DrawLine(glyph, l+w*0.50f, t+h*0.50f, l+w*0.50f, t+h*0.36f);
                    g.DrawLine(glyph, l+w*0.50f, t+h*0.50f, l+w*0.62f, t+h*0.56f);
                }
                else if (state == PublicState.Stuck)
                {
                    g.DrawLine(glyph, l+w*0.40f, t+h*0.34f, l+w*0.40f, t+h*0.66f);
                    g.DrawLine(glyph, l+w*0.60f, t+h*0.34f, l+w*0.60f, t+h*0.66f);
                }
                else if (state == PublicState.Done)
                {
                    g.DrawLine(glyph, l+w*0.30f, t+h*0.52f, l+w*0.44f, t+h*0.66f);
                    g.DrawLine(glyph, l+w*0.44f, t+h*0.66f, l+w*0.72f, t+h*0.36f);
                }
                else if (state == PublicState.LimitReached)
                {
                    g.DrawLine(glyph, l+w*0.50f, t+h*0.30f, l+w*0.50f, t+h*0.58f);
                    using (Brush dot = new SolidBrush(main))
                        g.FillEllipse(dot, l+w*0.46f, t+h*0.68f, w*0.08f, w*0.08f);
                }
                else
                {
                    using (Brush dot = new SolidBrush(main))
                        g.FillEllipse(dot, l+w*0.40f, t+h*0.40f, w*0.20f, h*0.20f);
                }
            }
        }
    }

    internal sealed class StatusPopupForm : Form
    {
        [DllImport("gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private readonly ToolTip _toolTip = new ToolTip();
        private readonly Timer _visibleRefreshTimer = new Timer();
        private StatusSnapshot _snapshot;
        private Rectangle _detailsRect;
        private int _hoverIndex = -1;
        private const int PopupWidth = 342;
        private const int PopupBaseHeight = 196;
        private const int FirstRowTop = 101;
        private const int RowStep = 15;
        private const int MoreTop = 150;
        private const int MoreHeight = 17;
        private const int MoreDividerGap = 8;
        private const int BaseDividerY = 151;
        private const int DetailsGap = 3;
        private const int DetailsTextOffset = 13;
        private const int DetailsHeight = 38;

        private struct PopupLayout
        {
            public int DividerY;
            public int DetailsTextY;
            public Rectangle DetailsRect;
            public int MoreTextY;
            public int Height;
        }
        private readonly PublicState[] _states = new PublicState[]
        {
            PublicState.Working, PublicState.WaitingForYou, PublicState.Stuck,
            PublicState.Done, PublicState.LimitReached, PublicState.Error, PublicState.Idle
        };

        public event Action DetailsRequested;
        public event Action RefreshRequested;

        public StatusPopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            Size = new Size(PopupWidth, PopupBaseHeight);
            DoubleBuffered = true;
            TopMost = true;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _visibleRefreshTimer.Interval = 5000;
            _visibleRefreshTimer.Tick += delegate
            {
                if (!Visible) return;
                Action refresh = RefreshRequested;
                if (refresh != null) refresh();
                else Invalidate();
            };
            VisibleChanged += delegate
            {
                if (Visible) _visibleRefreshTimer.Start();
                else _visibleRefreshTimer.Stop();
            };
            Deactivate += delegate { Hide(); };
            MouseMove += OnPopupMouseMove;
            MouseLeave += delegate { _hoverIndex = -1; _toolTip.Hide(this); };
            MouseUp += OnPopupMouseUp;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int CS_DROPSHADOW = 0x00020000;
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyRoundedRegion();
            Invalidate(true);
        }

        private void ApplyRoundedRegion()
        {
            IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 18, 18);
            try { Region = System.Drawing.Region.FromHrgn(rgn); }
            finally { DeleteObject(rgn); }
        }

        public void UpdateSnapshot(StatusSnapshot snapshot)
        {
            _snapshot = snapshot == null ? null : snapshot.Clone();
            PopupLayout layout = CalculateLayout(_snapshot == null || _snapshot.Groups == null ? 0 : _snapshot.Groups.Length);
            if (Width != PopupWidth || Height != layout.Height)
            {
                Size = new Size(PopupWidth, layout.Height);
                if (IsHandleCreated) ApplyRoundedRegion();
            }
            if (Visible) Invalidate();
        }

        public void ShowNearTray()
        {
            Point cursor = Cursor.Position;
            Screen screen = Screen.FromPoint(cursor);
            Rectangle area = screen.WorkingArea;
            int x = cursor.X - Width + 26;
            int y = area.Bottom - Height - 8;
            if (x < area.Left + 8) x = area.Left + 8;
            if (x + Width > area.Right - 8) x = area.Right - Width - 8;
            if (y < area.Top + 8) y = area.Top + 8;
            Location = new Point(x, y);
            Show();
            Invalidate(true);
            Activate();
            BringToFront();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            StatusSnapshot snap = _snapshot;
            if (snap == null) return;

            int iconSize = 29;
            int startX = 22;
            int topY = 18;
            int gap = 16;
            int i;
            for (i = 0; i < _states.Length; i++)
            {
                int x = startX + i * (iconSize + gap);
                RectangleF box = new RectangleF(x, topY, iconSize, iconSize);
                StatusPainter.Draw(g, _states[i], box, snap.IsStateLit(_states[i]));
            }

            using (Font titleFont = new Font("Segoe UI Semibold", 14f, FontStyle.Bold, GraphicsUnit.Point))
            using (Font subtitleFont = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (Font rowFont = new Font("Segoe UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point))
            using (Brush titleBrush = new SolidBrush(Color.FromArgb(28, 32, 38)))
            using (Brush labelBrush = new SolidBrush(Color.FromArgb(119, 126, 136)))
            using (Brush valueBrush = new SolidBrush(Color.FromArgb(48, 53, 61)))
            using (Pen separator = new Pen(Color.FromArgb(235, 238, 242), 1f))
            {
                g.DrawString(snap.PublicTitle, titleFont, titleBrush, 22, 62);
                g.DrawString(DisplaySubtitleFor(snap), subtitleFont, labelBrush, 22, 83);

                GroupStatusSnapshot[] groups = snap.Groups ?? new GroupStatusSnapshot[0];
                if (groups.Length <= 1)
                {
                    g.DrawString("Last work", Font, labelBrush, 22, 101);
                    string last = TimeTextFor(snap);
                    SizeF lastSize = g.MeasureString(last, Font);
                    g.DrawString(last, Font, valueBrush, Width - 22 - lastSize.Width, 101);

                    g.DrawString("Project", Font, labelBrush, 22, 125);
                    string project = TrimToWidth(g, snap.Project ?? "Unknown project", Font, 190);
                    SizeF projectSize = g.MeasureString(project, Font);
                    g.DrawString(project, Font, valueBrush, Width - 22 - projectSize.Width, 125);
                }
                else
                {
                    int rowCount = Math.Min(3, groups.Length);
                    for (int row = 0; row < rowCount; row++)
                    {
                        GroupStatusSnapshot group = groups[row];
                        if (group == null) continue;
                        string project = TrimToWidth(g, group.Project ?? "Unknown project", rowFont, 118);
                        string title = TrimToWidth(g, group.PublicTitle, rowFont, 100);
                        string left = "● " + project;
                        int rowTop = FirstRowTop + row * RowStep;
                        using (Brush rowBrush = new SolidBrush(StatusPalette.ColorFor(group.State)))
                            g.DrawString(left, rowFont, rowBrush, 22, rowTop);
                        g.DrawString(title, rowFont, valueBrush, 151, rowTop);
                        string age = TimeTextFor(group);
                        SizeF ageSize = g.MeasureString(age, rowFont);
                        g.DrawString(age, rowFont, labelBrush, Width - 22 - ageSize.Width, rowTop);
                    }
                    if (groups.Length > 3)
                    {
                        string more = "+" + (groups.Length - 3) + " more";
                        PopupLayout moreLayout = CalculateLayout(groups.Length);
                        g.DrawString(more, rowFont, labelBrush, 22, moreLayout.MoreTextY);
                    }
                }

                PopupLayout layout = CalculateLayout(groups.Length);
                g.DrawLine(separator, 22, layout.DividerY, Width - 22, layout.DividerY);
                using (Font detailsFont = new Font("Segoe UI Semibold", 9f, FontStyle.Bold, GraphicsUnit.Point))
                using (Brush detailsBrush = new SolidBrush(Color.FromArgb(67, 73, 82)))
                {
                    g.DrawString("Details", detailsFont, detailsBrush, 22, layout.DetailsTextY);
                    using (Pen chevronPen = new Pen(Color.FromArgb(120, 126, 136), 1.6f))
                    {
                        chevronPen.StartCap = LineCap.Round;
                        chevronPen.EndCap = LineCap.Round;
                        g.DrawLine(chevronPen, Width - 28, layout.DetailsTextY + 2, Width - 23, layout.DetailsTextY + 7);
                        g.DrawLine(chevronPen, Width - 23, layout.DetailsTextY + 7, Width - 28, layout.DetailsTextY + 12);
                    }
                }
                _detailsRect = layout.DetailsRect;
            }
        }

        private void OnPopupMouseMove(object sender, MouseEventArgs e)
        {
            int iconSize = 29;
            int startX = 22;
            int topY = 18;
            int gap = 16;
            int hit = -1;
            int i;
            for (i = 0; i < _states.Length; i++)
            {
                Rectangle r = new Rectangle(startX + i * (iconSize + gap) - 4, topY - 4, iconSize + 8, iconSize + 8);
                if (r.Contains(e.Location)) { hit = i; break; }
            }
            if (hit != _hoverIndex)
            {
                _hoverIndex = hit;
                _toolTip.Hide(this);
                if (hit >= 0)
                {
                    _toolTip.Show(StatusPalette.LabelFor(_states[hit]), this, e.X + 10, e.Y + 14, 1400);
                }
            }
            Cursor = _detailsRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        private void OnPopupMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _detailsRect.Contains(e.Location))
            {
                Action handler = DetailsRequested;
                if (handler != null) handler();
            }
        }

        private static string TimeText(DateTime utc)
        {
            if (utc == DateTime.MinValue) return "-";
            TimeSpan age = DateTime.UtcNow - utc;
            if (age.TotalSeconds < 0) age = TimeSpan.Zero;
            if (age.TotalMinutes < 1) return Math.Max(0, (int)age.TotalSeconds) + " sec ago";
            if (age.TotalHours < 1) return (int)age.TotalMinutes + " min ago";
            if (age.TotalDays < 1) return (int)age.TotalHours + " hr ago";
            return (int)age.TotalDays + " d ago";
        }

        internal static string DisplaySubtitleFor(StatusSnapshot snapshot)
        {
            if (snapshot != null && snapshot.State == PublicState.Working &&
                snapshot.BackgroundProcessAlive &&
                snapshot.BackgroundLastProgressUtc == DateTime.MinValue)
                return "Checking background progress";
            return snapshot == null ? PublicCopy.SubtitleFor(PublicState.Idle) : snapshot.PublicSubtitle;
        }

        private static string TimeTextFor(StatusSnapshot snapshot)
        {
            if (snapshot != null && BackgroundAwaitingProgress(snapshot.BackgroundProcessAlive,
                snapshot.BackgroundLastProgressUtc)) return "checking now";
            return TimeText(snapshot == null ? DateTime.MinValue : snapshot.LastWorkUtc);
        }

        private static string TimeTextFor(GroupStatusSnapshot group)
        {
            if (group != null && BackgroundAwaitingProgress(group.BackgroundJobActive,
                group.BackgroundLastProgressUtc)) return "checking now";
            return TimeText(group == null ? DateTime.MinValue : group.LastRealWorkUtc);
        }

        private static bool BackgroundAwaitingProgress(bool alive, DateTime lastProgressUtc)
        {
            return alive && lastProgressUtc == DateTime.MinValue;
        }

        private static PopupLayout CalculateLayout(int groupCount)
        {
            PopupLayout layout = new PopupLayout();
            layout.DividerY = groupCount > 3 ? MoreTop + MoreHeight + MoreDividerGap : BaseDividerY;
            layout.DetailsTextY = layout.DividerY + DetailsTextOffset;
            layout.DetailsRect = new Rectangle(14, layout.DividerY + DetailsGap, PopupWidth - 28, DetailsHeight);
            layout.MoreTextY = MoreTop;
            layout.Height = Math.Max(PopupBaseHeight, layout.DetailsRect.Bottom + 4);
            return layout;
        }

#if TEST_BUILD
        internal static int PopupHeightForTests(int groupCount)
        {
            return CalculateLayout(groupCount).Height;
        }

        internal static int PopupDividerYForTests(int groupCount)
        {
            return CalculateLayout(groupCount).DividerY;
        }

        internal static int PopupMoreTopForTests(int groupCount)
        {
            return CalculateLayout(groupCount).MoreTextY;
        }

        internal static int PopupMoreHeightForTests()
        {
            return MoreHeight;
        }

        internal static Rectangle PopupDetailsRectForTests(int groupCount)
        {
            return CalculateLayout(groupCount).DetailsRect;
        }
#endif


        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _visibleRefreshTimer.Dispose();
                _toolTip.Dispose();
            }
            base.Dispose(disposing);
        }

        private static string TrimToWidth(Graphics g, string text, Font font, float maxWidth)
        {
            if (g.MeasureString(text, font).Width <= maxWidth) return text;
            string ellipsis = "...";
            int len = text.Length;
            while (len > 1)
            {
                string candidate = text.Substring(0, len) + ellipsis;
                if (g.MeasureString(candidate, font).Width <= maxWidth) return candidate;
                len--;
            }
            return ellipsis;
        }
    }

    internal sealed class DetailsForm : Form
    {
        private readonly Label _title;
        private readonly TextBox _body;
        private readonly Button _copy;
        private readonly Timer _visibleRefreshTimer;
        private StatusSnapshot _snapshot;
        public event Action RefreshRequested;

        public DetailsForm()
        {
            Text = "Is Codex Working? - Details";
            Size = new Size(430, 330);
            MinimumSize = new Size(390, 280);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            _title = new Label();
            _title.AutoSize = false;
            _title.Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold);
            _title.Location = new Point(24, 22);
            _title.Size = new Size(360, 32);
            Controls.Add(_title);

            _body = new TextBox();
            _body.Multiline = true;
            _body.ReadOnly = true;
            _body.BorderStyle = BorderStyle.None;
            _body.ScrollBars = ScrollBars.Vertical;
            _body.TabStop = false;
            _body.BackColor = Color.White;
            _body.Location = new Point(26, 70);
            _body.Size = new Size(370, 155);
            Controls.Add(_body);

            _visibleRefreshTimer = new Timer();
            _visibleRefreshTimer.Interval = 5000;
            _visibleRefreshTimer.Tick += delegate
            {
                if (!Visible) return;
                Action refresh = RefreshRequested;
                if (refresh != null) refresh();
            };
            VisibleChanged += delegate
            {
                if (Visible) _visibleRefreshTimer.Start();
                else _visibleRefreshTimer.Stop();
            };

            _copy = new Button();
            _copy.Text = "Copy diagnostics";
            _copy.Location = new Point(26, 238);
            _copy.Size = new Size(130, 32);
            _copy.Click += delegate { CopyDiagnostics(); };
            Controls.Add(_copy);

            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
        }

        public void UpdateSnapshot(StatusSnapshot snapshot)
        {
            _snapshot = snapshot == null ? null : snapshot.Clone();
            if (_snapshot == null) return;
            _title.Text = _snapshot.PublicTitle;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(StatusPopupForm.DisplaySubtitleFor(_snapshot));
            sb.AppendLine("Project: " + (_snapshot.Project ?? "Unknown project"));
            sb.AppendLine("Last real work: " + (_snapshot.LastWorkUtc == DateTime.MinValue ? "-" : _snapshot.LastWorkUtc.ToLocalTime().ToString("G")));
            sb.AppendLine("Reason: " + (_snapshot.Reason ?? "-"));
            sb.AppendLine("Open turns: " + _snapshot.OpenTurnCount);
            sb.AppendLine("Codex process: " + (_snapshot.ProcessAlive ? "running" : "not checked / not running"));
            sb.AppendLine("Background job: " + (_snapshot.BackgroundProcessAlive ? (_snapshot.BackgroundProcessBusy ? "working" : "running") : "none detected"));
            sb.AppendLine("Confidence: " + (_snapshot.Confidence ?? "-"));
            if (_snapshot.Groups != null && _snapshot.Groups.Length > 1)
            {
                sb.AppendLine("Visible groups: " + _snapshot.Groups.Length);
                for (int i = 0; i < _snapshot.Groups.Length; i++)
                {
                    GroupStatusSnapshot group = _snapshot.Groups[i];
                    if (group == null) continue;
                    sb.AppendLine("  " + group.Project + ": " + group.PublicTitle +
                        " - " + group.PublicSubtitle);
                }
            }
            _body.Text = sb.ToString();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _visibleRefreshTimer != null) _visibleRefreshTimer.Dispose();
            base.Dispose(disposing);
        }

        private static string RedactPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "-";
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                    return "~" + path.Substring(home.Length);
            }
            catch { }
            return path;
        }

        private void CopyDiagnostics()
        {
            if (_snapshot == null) return;
            try { Clipboard.SetText(DiagnosticsText(_snapshot)); }
            catch { }
        }

        public static string DiagnosticsText(StatusSnapshot snapshot)
        {
            if (snapshot == null) return "No status available";
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Is Codex Working?");
            sb.AppendLine("State: " + snapshot.StateTitle);
            sb.AppendLine("Project: <redacted>");
            sb.AppendLine("Last work UTC: " + (snapshot.LastWorkUtc == DateTime.MinValue ? "-" : snapshot.LastWorkUtc.ToString("o")));
            sb.AppendLine("Reason: " + (snapshot.Reason ?? "-"));
            sb.AppendLine("Open turns: " + snapshot.OpenTurnCount);
            sb.AppendLine("Session groups: " + snapshot.GroupCount);
            sb.AppendLine("Process alive: " + snapshot.ProcessAlive);
            sb.AppendLine("Process busy: " + snapshot.ProcessBusy);
            sb.AppendLine("Background alive: " + snapshot.BackgroundProcessAlive);
            sb.AppendLine("Background busy: " + snapshot.BackgroundProcessBusy);
            sb.AppendLine("Background processes: " + snapshot.BackgroundProcessCount);
            sb.AppendLine("Confidence: " + (snapshot.Confidence ?? "-"));
            sb.AppendLine("Session: " + RedactPath(snapshot.SessionPath));
            return sb.ToString();
        }
    }
}
