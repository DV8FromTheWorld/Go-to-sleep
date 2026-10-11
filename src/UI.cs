using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace GoToSleep
{
    static class Theme
    {
        public static readonly Color Back = Color.FromArgb(12, 14, 20);
        public static readonly Color Panel = Color.FromArgb(28, 32, 44);
        public static readonly Color Text = Color.FromArgb(230, 232, 238);
        public static readonly Color Dim = Color.FromArgb(140, 146, 160);
        public static readonly Color Button = Color.FromArgb(36, 41, 56);
        public static readonly Color ButtonHover = Color.FromArgb(52, 59, 80);
        public static readonly Color Warn = Color.FromArgb(240, 170, 80);
        public static readonly Color WarnButton = Color.FromArgb(110, 70, 24);

        public static Font Font(float size, FontStyle style = FontStyle.Regular)
        {
            return new Font("Segoe UI", size, style);
        }

        public static void SetText(Control c, string text)
        {
            if (c.Text != text) c.Text = text;
        }
    }

    // Mouse-only button: it can never hold keyboard focus, so stray Enter/Space presses can't trigger a shutdown.
    class FlatButton : Button
    {
        public FlatButton()
        {
            SetStyle(ControlStyles.Selectable, false);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Theme.ButtonHover;
            BackColor = Theme.Button;
            ForeColor = Theme.Text;
            Cursor = Cursors.Hand;
            TabStop = false;
        }
    }

    class LockView
    {
        public string Clock = "", Headline = "", Until = "", Tomorrow = "";
        public string GraceText = "", BorrowText = "", BorrowWarning = "", PoolInfo = "";
        public bool GraceEnabled, BorrowEnabled;
    }

    class LockForm : Form
    {
        public event EventHandler ShutdownClicked, SleepClicked, GraceClicked, BorrowClicked;
#if DEBUG
        public event EventHandler DebugExitClicked;
#endif
        public bool AllowClose;
        public readonly Rectangle TargetBounds;
        readonly bool preview;
        FlowLayoutPanel stack;
        Label clock, headline, until, poolInfo, warning, tomorrow;
        FlatButton grace, borrow;
        FlowLayoutPanel confirmRow;
        bool confirmingBorrow;
        LockView view = new LockView();

        public LockForm(Rectangle bounds, bool primary, bool preview)
        {
            this.preview = preview;
            TargetBounds = bounds;
            Text = "Go To Sleep";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            ShowInTaskbar = preview;
            TopMost = !preview;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            KeyPreview = true;
            if (primary) Build();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (!preview) cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        void Build()
        {
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            int width = (int)(500 * scale);

            stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Back,
            };

            clock = MakeLabel(Theme.Font(54, FontStyle.Bold), Theme.Text, width, 0);
            headline = MakeLabel(Theme.Font(22), Theme.Text, width, 4);
            until = MakeLabel(Theme.Font(12), Theme.Dim, width, 28);
            var shutdown = MakeButton("Shut down", width, scale);
            var sleep = MakeButton("Sleep", width, scale);
            grace = MakeButton("", width, scale);
            borrow = MakeButton("", width, scale);
            poolInfo = MakeLabel(Theme.Font(10.5f), Theme.Dim, width, (int)(6 * scale));
            warning = MakeLabel(Theme.Font(11), Theme.Warn, width, (int)(4 * scale));
            warning.Visible = false;

            // Shown after clicking borrow: explanation above, then Start / Cancel side by side.
            int half = (width - (int)(10 * scale)) / 2;
            var start = MakeButton("Start borrowing", half, scale);
            start.BackColor = Theme.WarnButton;
            start.Margin = new Padding(0, 0, (int)(10 * scale), 0);
            var cancel = MakeButton("Cancel", half, scale);
            cancel.Margin = new Padding(0);
            confirmRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Back,
                Margin = new Padding(0, 0, 0, (int)(10 * scale)),
                Visible = false,
            };
            confirmRow.Controls.Add(start);
            confirmRow.Controls.Add(cancel);
            start.Click += (s, e) => { SetConfirming(false); Raise(BorrowClicked); };
            cancel.Click += (s, e) => SetConfirming(false);
            tomorrow = MakeLabel(Theme.Font(11), Theme.Dim, width, 0);
            tomorrow.Margin = new Padding(0, (int)(24 * scale), 0, 0);

            shutdown.Click += (s, e) => Raise(ShutdownClicked);
            sleep.Click += (s, e) => Raise(SleepClicked);
            grace.Click += (s, e) => Raise(GraceClicked);
            borrow.Click += (s, e) => SetConfirming(!confirmingBorrow);

            stack.Controls.AddRange(new Control[] { clock, headline, until, shutdown, sleep, grace, borrow, warning, confirmRow, poolInfo, tomorrow });
#if DEBUG
            var debugExit = MakeButton("DEBUG: Exit (debug build only)", width, scale);
            debugExit.BackColor = Color.FromArgb(120, 30, 30);
            debugExit.Margin = new Padding(0, (int)(24 * scale), 0, 0);
            debugExit.Click += (s, e) => Raise(DebugExitClicked);
            stack.Controls.Add(debugExit);
#endif
            Controls.Add(stack);
            stack.SizeChanged += (s, e) => Center();
            Center();
        }

        static Label MakeLabel(Font font, Color color, int width, int bottomMargin)
        {
            return new Label
            {
                AutoSize = true,
                MinimumSize = new Size(width, 0),
                MaximumSize = new Size(width, 0),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = font,
                ForeColor = color,
                Margin = new Padding(0, 0, 0, bottomMargin),
            };
        }

        static FlatButton MakeButton(string text, int width, float scale)
        {
            return new FlatButton
            {
                Text = text,
                Font = Theme.Font(13),
                Size = new Size(width, (int)(54 * scale)),
                Margin = new Padding(0, (int)(5 * scale), 0, (int)(5 * scale)),
            };
        }

        void Center()
        {
            if (stack == null) return;
            stack.Location = new Point(Math.Max(0, (ClientSize.Width - stack.Width) / 2),
                                       Math.Max(0, (ClientSize.Height - stack.Height) / 2));
        }

        void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        void SetConfirming(bool on)
        {
            confirmingBorrow = on;
            UpdateView(view);
        }

        public void UpdateView(LockView v)
        {
            view = v;
            if (stack == null) return;
            if (!v.BorrowEnabled) confirmingBorrow = false;
            bool confirming = confirmingBorrow;
            Theme.SetText(clock, v.Clock);
            Theme.SetText(headline, v.Headline);
            Theme.SetText(until, v.Until);
            Theme.SetText(poolInfo, v.PoolInfo);
            Theme.SetText(tomorrow, v.Tomorrow);
            Theme.SetText(grace, v.GraceText);
            grace.Enabled = v.GraceEnabled;
            Theme.SetText(borrow, v.BorrowText);
            borrow.Enabled = v.BorrowEnabled;
            borrow.BackColor = confirming ? Theme.ButtonHover : Theme.Button;
            Theme.SetText(warning, v.BorrowWarning);
            warning.Visible = confirming;
            confirmRow.Visible = confirming;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!preview && !AllowClose && e.CloseReason != CloseReason.WindowsShutDown) e.Cancel = true;
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (preview && keyData == Keys.Escape)
            {
                Close();
                return true;
            }
            if (keyData == (Keys.Alt | Keys.F4)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_SYSCOMMAND = 0x0112;
            if (!preview && m.Msg == WM_SYSCOMMAND)
            {
                int cmd = m.WParam.ToInt32() & 0xFFF0;
                if (cmd == 0xF020 /* minimize */ || cmd == 0xF060 /* close */ || cmd == 0xF010 /* move */ || cmd == 0xF000 /* size */)
                    return;
            }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!preview && WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            Center();
        }
    }

    // Moon in the notification area. Keeps the status and "I'm done" reachable after a popup is dismissed.
    class Tray : IDisposable
    {
        public event EventHandler DoneClicked, ShowClicked;
#if DEBUG
        public event EventHandler DebugExitClicked;
#endif
        readonly NotifyIcon icon;
        readonly ToolStripMenuItem status, pool, done, show;

        public Tray()
        {
            status = new ToolStripMenuItem { Enabled = false };
            pool = new ToolStripMenuItem { Enabled = false };
            done = new ToolStripMenuItem("I'm done, lock now");
            show = new ToolStripMenuItem("Show countdown");
            done.Click += (s, e) => Raise(DoneClicked);
            show.Click += (s, e) => Raise(ShowClicked);

            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { status, pool, new ToolStripSeparator(), done, show });
#if DEBUG
            var debugExit = new ToolStripMenuItem("DEBUG: Exit");
            debugExit.Click += (s, e) => Raise(DebugExitClicked);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(debugExit);
#endif
            icon = new NotifyIcon { Icon = MakeMoon(), ContextMenuStrip = menu, Text = "Go To Sleep", Visible = true };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Raise(ShowClicked); };
        }

        void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Update(string statusText, string poolText, bool canFinish, bool canShow)
        {
            if (status.Text != statusText) status.Text = statusText;
            pool.Visible = poolText != null;
            if (poolText != null && pool.Text != poolText) pool.Text = poolText;
            done.Enabled = canFinish;
            show.Enabled = canShow;
            string tip = statusText.Length > 63 ? statusText.Substring(0, 63) : statusText;  // NotifyIcon limit
            if (icon.Text != tip) icon.Text = tip;
        }

        static Icon MakeMoon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var moon = new SolidBrush(Color.FromArgb(244, 204, 96)))
                        g.FillEllipse(moon, 2, 2, 28, 28);
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    using (var cut = new SolidBrush(Color.Transparent))
                        g.FillEllipse(cut, 11, -3, 24, 24);
                }
                return (Icon)Icon.FromHandle(bmp.GetHicon()).Clone();
            }
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }
    }

    // Small always-on-top corner box that never steals focus from your game/video.
    class Popup : Form
    {
        public event EventHandler ButtonClicked, DismissClicked;
        readonly Label label;
        readonly FlatButton button;
        readonly bool top;

        public Popup(bool top)
        {
            this.top = top;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14, 10, 14, 10);

            var flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Panel,
            };
            label = new Label { AutoSize = true, Font = Theme.Font(12), ForeColor = Theme.Text, Margin = new Padding(0, 6, 10, 6) };
            button = new FlatButton { AutoSize = true, Font = Theme.Font(10), Visible = false, Padding = new Padding(8, 2, 8, 2) };
            var dismiss = new FlatButton
            {
                Text = "\u00D7",
                AutoSize = true,
                Font = Theme.Font(11),
                BackColor = Theme.Panel,
                ForeColor = Theme.Dim,
                Margin = new Padding(8, 0, 0, 0),
                Padding = new Padding(4, 0, 4, 0),
            };
            button.Click += (s, e) => { if (ButtonClicked != null) ButtonClicked(this, EventArgs.Empty); };
            dismiss.Click += (s, e) => { if (DismissClicked != null) DismissClicked(this, EventArgs.Empty); };
            flow.Controls.Add(label);
            flow.Controls.Add(button);
            flow.Controls.Add(dismiss);
            Controls.Add(flow);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        // Primary monitor first, then the rest. Skip any monitor an exclusive-fullscreen game covers, since nothing can
        // draw over it. If every monitor is covered (e.g. a single screen), fall back to the primary.
        public static int PickScreen(Rectangle[] screensPrimaryFirst, bool exclusiveFullscreen, Rectangle? foreground)
        {
            if (!exclusiveFullscreen || !foreground.HasValue) return 0;
            for (int i = 0; i < screensPrimaryFirst.Length; i++)
                if (!foreground.Value.Contains(screensPrimaryFirst[i])) return i;
            return 0;
        }

        static Screen ChooseScreen()
        {
            var screens = Screen.AllScreens.OrderBy(s => s.Primary ? 0 : 1).ToArray();
            int i = PickScreen(screens.Select(s => s.Bounds).ToArray(),
                               Native.ExclusiveFullscreenRunning(),
                               Native.WindowBounds(Native.GetForegroundWindow()));
            return screens[i];
        }

        public void Display(string text, string buttonText)
        {
            Theme.SetText(label, text);
            button.Visible = buttonText != null;
            if (buttonText != null) Theme.SetText(button, buttonText);
            PerformLayout();
            var wa = ChooseScreen().WorkingArea;
            var loc = new Point(wa.Right - Width - 16, top ? wa.Top + 16 : wa.Bottom - Height - 16);
            if (Location != loc) Location = loc;
            if (!Visible) Show();
            Native.TopMost(Handle);
        }
    }
}
