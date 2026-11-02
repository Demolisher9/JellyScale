using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace JellyScale
{
    static class AppIcon
    {
        static byte[] Resource(string name)
        {
            using (var s = typeof(AppIcon).Assembly.GetManifestResourceStream(name))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        static readonly byte[] Ico = Resource("JellyScale.ico");

        public static Icon Full() { return new Icon(new MemoryStream(Ico)); }
        public static Icon Small() { return new Icon(new MemoryStream(Ico), SystemInformation.SmallIconSize); }
        public static Bitmap Logo() { return new Bitmap(new MemoryStream(Resource("JellyScale-128.png"))); }

        /// <summary>Tray icon with a green status dot, shown while the bridge is ON.</summary>
        public static Icon SmallWithDot()
        {
            try
            {
                var size = SystemInformation.SmallIconSize;
                using (var baseIcon = Small())
                using (var bmp = new Bitmap(size.Width, size.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.DrawIcon(baseIcon, new Rectangle(0, 0, size.Width, size.Height));
                        float d = size.Width * 0.5f;
                        var r = new RectangleF(size.Width - d - 0.5f, size.Height - d - 0.5f, d, d);
                        using (var b = new SolidBrush(Theme.Green)) g.FillEllipse(b, r);
                        using (var p = new Pen(Theme.Bg, Math.Max(1f, size.Width / 16f))) g.DrawEllipse(p, r);
                    }
                    return Icon.FromHandle(bmp.GetHicon()); // lives for the whole session
                }
            }
            catch { return Small(); }
        }
    }

    class ImageBox : Control
    {
        public Image Image;
        public ImageBox() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            if (Image == null) return;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(Image, ClientRectangle);
        }
    }

    class MainForm : Form
    {
        readonly IBackend backend;

        // Model
        BridgeState state = BridgeState.Off;
        bool busy, quitting, balloonShown, pollInFlight, setupBusy, firstStateLoaded;
        int generation;
        string busyTitle, busyText, errorTitle, errorText, notice, lanIP, checkedIP;
        bool notPrivate;
        readonly int showMsg, exitMsg;

        // Start with Windows: launched by the sign-in task, the window starts hidden in the tray and turns the
        // bridge on. autoPending keeps the status poll out of the way until that first action has started.
        bool startHidden, autoPending, startsWithWindows, autoStartBusy, networkBusy;

        // Header
        ImageBox logo;
        Label appTitle, tagline;
        FlatButton menuButton;
        ContextMenuStrip appMenu;
        ToolStripMenuItem menuChangeServer, menuAutoStart;

        // Main view
        Panel mainView;
        PowerButton power;
        Label status, sub, footer;
        LinkLabel details;
        RoundPanel tvCard, serverCard;
        Label tvCaption, tvAddress, tvHint, serverCaption, serverValue;
        FlatButton copyButton, changeButton;

        // Setup view
        Panel setupView;
        Label setupTitle, setupIntro, fieldCaption, fieldHint, checkDetail;
        RoundPanel inputHost, checksCard;
        TextBox ipBox;
        CheckRow[] rows;
        LinkLabel networkLink;
        FlatButton privateButton, cancelButton, saveButton;

        // Tray
        NotifyIcon tray;
        Icon trayOff, trayOn;
        ContextMenuStrip trayMenu;
        ToolStripMenuItem trayOpen, trayToggle, trayExit;

        System.Windows.Forms.Timer animTimer, pollTimer;

        public MainForm(IBackend backend, bool autostart)
        {
            this.backend = backend;
            startHidden = autoPending = autostart;
            using (var g = CreateGraphics()) Theme.Scale = g.DpiX / 96f;
            try { showMsg = Native.RegisterWindowMessage(AppInfo.ShowMessage); exitMsg = Native.RegisterWindowMessage(AppInfo.ExitMessage); } catch { }

            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Text = AppInfo.Name;
            Font = Theme.Regular(9f);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Theme.S(420), Theme.S(590));
            try { Icon = AppIcon.Full(); } catch { }

            BuildHeader();
            BuildMainView();
            BuildSetupView();
            BuildTray();
            ResumeLayout(false);

            animTimer = new System.Windows.Forms.Timer { Interval = 33 };
            animTimer.Tick += delegate { Animate(); };
            animTimer.Start();
            pollTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            pollTimer.Tick += delegate { Poll(); };

            bool needsSetup = backend.ServerIP == null;
            mainView.Visible = !needsSetup;
            setupView.Visible = needsSetup;
            if (needsSetup) PrepareSetup(true);
            UpdateUi();

            if (!IsHandleCreated) CreateHandle();
            bool starts = false;
            RunAsync(() => { starts = backend.StartsWithWindows(); }, () => { startsWithWindows = starts; }, ex => { });
            if (autostart) BeginInvoke((MethodInvoker)BeginAutoStart);
        }

        // ------------------------------------------------------------------ building

        void BuildHeader()
        {
            logo = new ImageBox { Bounds = Theme.R(24, 20, 40, 40) };
            try { logo.Image = AppIcon.Logo(); } catch { }
            appTitle = Theme.Label("JellyScale", Theme.Semibold(15f), Theme.Text, Theme.Bg, ContentAlignment.MiddleLeft);
            appTitle.Bounds = Theme.R(71, 14, 250, 30);
            tagline = Theme.Label("Your Jellyfin server, on your TV", Theme.Regular(9f), Theme.Muted, Theme.Bg, ContentAlignment.TopLeft);
            tagline.Bounds = Theme.R(73, 43, 280, 18);
            menuButton = new FlatButton { Style = FlatButton.Kind.Dots, Bounds = Theme.R(358, 24, 38, 30), AccessibleName = "More options" };

            appMenu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = Theme.Regular(9.5f) };
            menuChangeServer = new ToolStripMenuItem("Change server…", null, delegate { ChangeServer(); });
            appMenu.Items.Add(menuChangeServer);
            menuAutoStart = new ToolStripMenuItem("Start with Windows: Off", null, delegate { ToggleStartWithWindows(); })
            {
                ToolTipText = "When you sign in, open JellyScale in the notification area and turn the bridge on."
            };
            appMenu.Items.Add(menuAutoStart);
            appMenu.Opening += delegate
            {
                menuAutoStart.Text = "Start with Windows: " + (startsWithWindows ? "On" : "Off");
                menuAutoStart.Enabled = !autoStartBusy;
            };
            appMenu.Items.Add(new ToolStripMenuItem("Open JellyScale folder", null, delegate { OpenFolder(); }));
            appMenu.Items.Add(new ToolStripSeparator());
            appMenu.Items.Add(new ToolStripMenuItem("About JellyScale", null, delegate { ShowAbout(); }));
            foreach (ToolStripItem item in appMenu.Items) { item.ForeColor = Theme.Text; item.Padding = new Padding(Theme.S(6), Theme.S(3), Theme.S(6), Theme.S(3)); }
            menuButton.Click += delegate { appMenu.Show(menuButton, new Point(menuButton.Width - appMenu.PreferredSize.Width, menuButton.Height + Theme.S(4))); };

            Controls.AddRange(new Control[] { logo, appTitle, tagline, menuButton });
        }

        void BuildMainView()
        {
            mainView = new Panel { Bounds = Theme.R(0, 72, 420, 518), BackColor = Theme.Bg };

            power = new PowerButton { Bounds = Theme.R(126, 10, 168, 168) };
            power.Activated += delegate { Toggle(); };

            status = Theme.Label("OFF", Theme.Semibold(19f), Theme.Text, Theme.Bg, ContentAlignment.MiddleCenter);
            status.Bounds = Theme.R(24, 184, 372, 38);
            sub = Theme.Label("", Theme.Regular(10f), Theme.Muted, Theme.Bg, ContentAlignment.TopCenter);
            sub.Bounds = Theme.R(30, 223, 360, 42);
            sub.AutoEllipsis = true;
            details = new LinkLabel
            {
                Text = "Show details", Font = Theme.Regular(8.75f), BackColor = Theme.Bg, LinkColor = Theme.Cyan, ActiveLinkColor = Color.White,
                LinkBehavior = LinkBehavior.HoverUnderline, TextAlign = ContentAlignment.MiddleCenter, AutoSize = false, Visible = false,
                Bounds = Theme.R(24, 265, 372, 20)
            };
            details.LinkClicked += delegate { MessageBox.Show(this, errorText, "JellyScale", MessageBoxButtons.OK, MessageBoxIcon.Warning); };

            tvCard = new RoundPanel { Bounds = Theme.R(24, 292, 372, 94) };
            tvCaption = Theme.Label("TV ADDRESS", Theme.Semibold(7.5f), Theme.Faint, Theme.Card, ContentAlignment.MiddleLeft);
            tvCaption.Bounds = Theme.R(16, 10, 200, 18);
            tvAddress = Theme.Label("", Theme.Semibold(13.5f), Theme.Text, Theme.Card, ContentAlignment.MiddleLeft);
            tvAddress.Bounds = Theme.R(13, 28, 266, 32);
            copyButton = new FlatButton { Text = "Copy", Bounds = Theme.R(286, 30, 70, 30), Font = Theme.Semibold(9f) };
            copyButton.Click += delegate { CopyAddress(); };
            tvHint = Theme.Label("", Theme.Regular(8.75f), Theme.Muted, Theme.Card, ContentAlignment.MiddleLeft);
            tvHint.Bounds = Theme.R(16, 63, 344, 20);
            tvHint.AutoEllipsis = true;
            tvHint.Click += delegate { if (notPrivate && state == BridgeState.On) MakePrivate(); };
            tvCard.Controls.AddRange(new Control[] { tvCaption, tvAddress, copyButton, tvHint });

            serverCard = new RoundPanel { Bounds = Theme.R(24, 396, 372, 62) };
            serverCaption = Theme.Label("JELLYFIN SERVER  ·  OVER TAILSCALE", Theme.Semibold(7.5f), Theme.Faint, Theme.Card, ContentAlignment.MiddleLeft);
            serverCaption.Bounds = Theme.R(16, 10, 260, 18);
            serverValue = Theme.Label("", Theme.Regular(10.5f), Theme.Text, Theme.Card, ContentAlignment.MiddleLeft);
            serverValue.Bounds = Theme.R(14, 29, 260, 24);
            changeButton = new FlatButton { Text = "Change", Bounds = Theme.R(286, 16, 70, 30), Font = Theme.Semibold(9f) };
            changeButton.Click += delegate { ChangeServer(); };
            serverCard.Controls.AddRange(new Control[] { serverCaption, serverValue, changeButton });

            footer = Theme.Label("While ON: High Performance power plan, sleep and hibernate set to Never.\nTurning OFF restores your sleep timers and selects Balanced.",
                Theme.Regular(8.25f), Theme.Faint, Theme.Bg, ContentAlignment.TopCenter);
            footer.Bounds = Theme.R(16, 470, 388, 40);

            mainView.Controls.AddRange(new Control[] { power, status, sub, details, tvCard, serverCard, footer });
            Controls.Add(mainView);
        }

        void BuildSetupView()
        {
            setupView = new Panel { Bounds = Theme.R(0, 72, 420, 518), BackColor = Theme.Bg };

            setupTitle = Theme.Label("Connect your server", Theme.Semibold(15f), Theme.Text, Theme.Bg, ContentAlignment.MiddleLeft);
            setupTitle.Bounds = Theme.R(22, 14, 372, 34);
            setupIntro = Theme.Label("Enter the Tailscale IP of the computer that runs Jellyfin (not this PC). JellyScale checks the connection before saving.",
                Theme.Regular(9.5f), Theme.Muted, Theme.Bg, ContentAlignment.TopLeft);
            setupIntro.Bounds = Theme.R(24, 52, 372, 40);

            fieldCaption = Theme.Label("SERVER TAILSCALE IP", Theme.Semibold(7.5f), Theme.Faint, Theme.Bg, ContentAlignment.MiddleLeft);
            fieldCaption.Bounds = Theme.R(24, 102, 372, 18);
            inputHost = new RoundPanel { Bounds = Theme.R(24, 122, 372, 46), Fill = Theme.Input, Border = Color.FromArgb(52, 62, 120), Radius = 12 };
            ipBox = new TextBox
            {
                BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Regular(13f),
                MaxLength = 15, AccessibleName = "Server Tailscale IP"
            };
            ipBox.Bounds = new Rectangle(Theme.S(14), (inputHost.Height - ipBox.PreferredHeight) / 2, inputHost.Width - Theme.S(28), ipBox.PreferredHeight);
            ipBox.GotFocus += delegate { inputHost.Border = Theme.Violet; };
            ipBox.LostFocus += delegate { inputHost.Border = Color.FromArgb(52, 62, 120); };
            ipBox.TextChanged += delegate { if (checkedIP != null && !setupBusy) { checkedIP = null; saveButton.Text = "Check & save"; } };
            ipBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SaveClicked(); }
                else if (e.KeyCode == Keys.Escape && cancelButton.Visible) { e.SuppressKeyPress = true; CloseSetup(); }
            };
            inputHost.Controls.Add(ipBox);
            fieldHint = Theme.Label("Looks like 100.x.y.z. Find it in the Tailscale app. Jellyfin must use port 8096.",
                Theme.Regular(8.75f), Theme.Faint, Theme.Bg, ContentAlignment.TopLeft);
            fieldHint.Bounds = Theme.R(24, 174, 372, 34);

            checksCard = new RoundPanel { Bounds = Theme.R(24, 214, 372, 202) };
            rows = new CheckRow[Checks.Labels.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new CheckRow { Text = Checks.Labels[i], BackColor = Theme.Card, Bounds = Theme.R(14, 14 + i * 28, 344, 26) };
                checksCard.Controls.Add(rows[i]);
            }
            checkDetail = Theme.Label("", Theme.Regular(8.75f), Theme.Red, Theme.Card, ContentAlignment.TopLeft);
            checkDetail.Bounds = Theme.R(16, 132, 342, 48);
            networkLink = new LinkLabel
            {
                Text = "Open network settings", Font = Theme.Regular(8.75f), BackColor = Theme.Card, LinkColor = Theme.Cyan, ActiveLinkColor = Color.White,
                LinkBehavior = LinkBehavior.HoverUnderline, AutoSize = false, Visible = false, Bounds = Theme.R(16, 170, 190, 20)
            };
            networkLink.LinkClicked += delegate { OpenNetworkSettings(); };
            privateButton = new FlatButton { Text = "Make Private", Bounds = Theme.R(222, 164, 136, 30), Font = Theme.Semibold(9f), Visible = false };
            privateButton.Click += delegate { MakePrivate(); };
            checksCard.Controls.AddRange(new Control[] { checkDetail, networkLink, privateButton });

            cancelButton = new FlatButton { Text = "Back", Bounds = Theme.R(24, 446, 110, 40) };
            cancelButton.Click += delegate { CloseSetup(); };
            saveButton = new FlatButton { Text = "Check & save", Style = FlatButton.Kind.Primary, Bounds = Theme.R(236, 446, 160, 40), Font = Theme.Semibold(10f) };
            saveButton.Click += delegate { SaveClicked(); };

            setupView.Controls.AddRange(new Control[] { setupTitle, setupIntro, fieldCaption, inputHost, fieldHint, checksCard, cancelButton, saveButton });
            Controls.Add(setupView);
        }

        void BuildTray()
        {
            try { trayOff = AppIcon.Small(); trayOn = AppIcon.SmallWithDot(); } catch { }
            trayMenu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = Theme.Regular(9.5f) };
            trayOpen = new ToolStripMenuItem("Open JellyScale", null, delegate { ShowFromTray(); }) { Font = Theme.Semibold(9.5f) };
            trayToggle = new ToolStripMenuItem("Turn ON", null, delegate { if (!busy) { if (state == BridgeState.Off) ShowFromTray(); Toggle(); } });
            trayExit = new ToolStripMenuItem("Exit", null, delegate { ExitApp(); });
            trayMenu.Items.AddRange(new ToolStripItem[] { trayOpen, trayToggle, new ToolStripSeparator(), trayExit });
            foreach (ToolStripItem item in trayMenu.Items) { item.ForeColor = Theme.Text; item.Padding = new Padding(Theme.S(6), Theme.S(3), Theme.S(6), Theme.S(3)); }
            tray = new NotifyIcon { Icon = trayOff, Text = "JellyScale", ContextMenuStrip = trayMenu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
            tray.BalloonTipClicked += delegate { ShowFromTray(); };
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleTitleBar(Handle, Theme.Bg, Theme.Text);
        }

        protected override void SetVisibleCore(bool value)
        {
            // Started by Start with Windows: Application.Run shows the form, but it should stay in the tray.
            if (startHidden && value)
            {
                startHidden = false;
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (setupView.Visible) ipBox.Focus(); else power.Focus();
            Poll();
            pollTimer.Start();
        }

        protected override void WndProc(ref Message m)
        {
            if (showMsg != 0 && m.Msg == showMsg) { ShowFromTray(); return; }
            if (exitMsg != 0 && m.Msg == exitMsg)
            {
                quitting = true;
                if (tray != null) tray.Visible = false;
                Application.Exit();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!quitting && e.CloseReason == CloseReason.UserClosing)
            {
                if (busy || setupBusy) { e.Cancel = true; return; }
                if (state == BridgeState.On) { e.Cancel = true; HideToTray(); return; }
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.OnFormClosed(e);
        }

        void HideToTray()
        {
            Hide();
            if (!balloonShown && tray != null)
            {
                balloonShown = true;
                tray.ShowBalloonTip(6000, "JellyScale is still ON", "Your TV can keep watching. Right-click the JellyScale icon here to turn it off.", ToolTipIcon.Info);
            }
        }

        void ShowFromTray()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        void ExitApp()
        {
            if (busy || setupBusy) return;
            if (state == BridgeState.On)
            {
                var answer = MessageBox.Show(Visible ? this : null,
                    "JellyScale is ON.\n\nTurn it off before exiting?\n\nYes: turn off, then exit.\nNo: exit and leave the bridge running. Open JellyScale later to turn it off.",
                    "JellyScale", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.Yes) { TurnOff(null, Quit); return; }
            }
            Quit();
        }

        void Quit()
        {
            quitting = true;
            if (tray != null) tray.Visible = false;
            Close();
        }

        // ------------------------------------------------------------------ async helpers

        void RunAsync(Action work, Action done, Action<Exception> failed)
        {
            var t = new Thread(() =>
            {
                Exception error = null;
                try { work(); }
                catch (Exception ex) { error = ex; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (error == null) done();
                        else failed(error);
                    });
                }
                catch (InvalidOperationException) { } // window already gone
            });
            t.IsBackground = true;
            t.Start();
        }

        void UI(MethodInvoker action)
        {
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        static string Describe(Exception ex)
        {
            if (ex is BridgeException) return ex.Message;
            Log.Write(ex.ToString());
            return "Something went wrong: " + ex.Message;
        }

        BridgeState SafeState()
        {
            try { return backend.GetState(); } catch { return state; }
        }

        // ------------------------------------------------------------------ bridge actions

        void Toggle()
        {
            if (busy) return;
            if (backend.ServerIP == null) { ShowFromTray(); PrepareSetup(true); return; }
            if (state == BridgeState.Off) TurnOn();
            else TurnOff(null, null);
        }

        void SetBusy(string title)
        {
            generation++;
            busy = title != null;
            busyTitle = title;
            busyText = "";
            if (busy) { errorTitle = null; errorText = null; notice = null; }
            UpdateUi();
        }

        void Progress(string text)
        {
            UI(delegate { busyText = text; if (busy) sub.Text = text; });
        }

        void TurnOn() { TurnOn(false); }

        /// <summary>waitForServer: keep retrying for a few minutes while the server is unreachable (right after
        /// sign-in Tailscale may still be connecting). Nothing is changed on the PC until the server answers.</summary>
        void TurnOn(bool waitForServer)
        {
            SetBusy("Turning on…");
            TurnOnResult result = null;
            var deadline = DateTime.UtcNow.AddMinutes(3);
            RunAsync(() =>
                {
                    while (true)
                    {
                        try { result = backend.TurnOn(Progress); return; }
                        catch (ServerUnreachableException)
                        {
                            if (!waitForServer || DateTime.UtcNow > deadline) throw;
                            Progress("Waiting for Tailscale to reach your server…");
                            Thread.Sleep(10000);
                        }
                    }
                },
                () =>
                {
                    SetBusy(null);
                    state = BridgeState.On;
                    if (result.LanIP != null) lanIP = result.LanIP;
                    notPrivate = result.NetworkNotPrivate;
                    UpdateUi();
                    if (!Visible && tray != null)
                    {
                        balloonShown = true; // the "still ON" tip would only repeat this
                        if (notPrivate)
                            tray.ShowBalloonTip(6000, "JellyScale is ON", "Your network is set to Public, so the TV can't connect yet. Click here to open JellyScale and fix it.", ToolTipIcon.Warning);
                        else
                            tray.ShowBalloonTip(5000, "JellyScale is ON", "Your TV can connect. Right-click the JellyScale icon here to turn it off.", ToolTipIcon.Info);
                    }
                },
                ex =>
                {
                    SetBusy(null);
                    state = SafeState();
                    if (waitForServer) ShowFromTray(); // started with Windows: show what went wrong
                    ShowError("Couldn't turn on", Describe(ex));
                });
        }

        /// <summary>Started by Start with Windows: turn the bridge on from the tray. If Windows restarted while it was
        /// on, the old sleep settings are restored first. The window only appears when it needs the user.</summary>
        void BeginAutoStart()
        {
            if (backend.ServerIP == null) { autoPending = false; ShowFromTray(); return; }
            BridgeState s = state;
            string ip = null;
            RunAsync(() => { s = backend.GetState(); ip = backend.LanIP(); },
                () =>
                {
                    autoPending = false;
                    firstStateLoaded = true;
                    state = s;
                    lanIP = ip;
                    pollTimer.Start();
                    if (s == BridgeState.On) { UpdateUi(); return; }
                    if (s == BridgeState.Interrupted)
                        TurnOff("Your sleep settings were restored after a restart.", delegate { TurnOn(true); });
                    else
                        TurnOn(true);
                },
                ex =>
                {
                    autoPending = false;
                    Log.Write("Start with Windows: status check failed: " + ex.Message);
                    ShowFromTray();
                });
        }

        void ToggleStartWithWindows()
        {
            if (autoStartBusy) return;
            bool target = !startsWithWindows;
            if (target)
            {
                var answer = MessageBox.Show(this,
                    "Start JellyScale with Windows?\n\nWhen you sign in, JellyScale opens in the notification area and turns the bridge on, so the TV can connect without anyone opening the app.\n\nWhile the bridge is on, this PC doesn't go to sleep.",
                    "JellyScale", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (answer != DialogResult.OK) return;
            }
            autoStartBusy = true;
            RunAsync(() => backend.SetStartWithWindows(target),
                () =>
                {
                    autoStartBusy = false;
                    startsWithWindows = target;
                    if (!busy && errorTitle == null)
                        notice = target ? "JellyScale will start and turn on when you sign in to Windows." : "JellyScale won't start with Windows any more.";
                    UpdateUi();
                },
                ex =>
                {
                    autoStartBusy = false;
                    MessageBox.Show(this, Describe(ex), "JellyScale", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
        }

        /// <summary>Asks which network it is, confirms with the user, then sets it to Private. Uninstall sets it back.</summary>
        void MakePrivate()
        {
            if (networkBusy) return;
            SetNetworkBusy(true, null);
            string name = null;
            RunAsync(() => { name = backend.HomeNetworkName(); },
                () =>
                {
                    SetNetworkBusy(false, null);
                    if (name == null)
                    {
                        MessageBox.Show(this, "JellyScale couldn't tell which network is your home network.\n\nUse Open network settings to set it to Private.",
                            "JellyScale", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        OpenNetworkSettings();
                        return;
                    }
                    var answer = MessageBox.Show(this,
                        "Set \"" + name + "\" to Private?\n\nOnly do this for your own home network. Windows treats Private networks as trusted, and that's what lets your TV reach this PC.\n\nIf you uninstall JellyScale, it sets this network back to Public.",
                        "JellyScale", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer != DialogResult.Yes) return;
                    SetNetworkBusy(true, "Setting…");
                    RunAsync(() => { name = backend.MakeNetworkPrivate(); },
                        () =>
                        {
                            SetNetworkBusy(false, null);
                            notPrivate = false;
                            if (setupView.Visible)
                            {
                                rows[3].Status = CheckStatus.Ok;
                                checkDetail.Text = "\"" + name + "\" is now Private, so your TV can connect.";
                                checkDetail.ForeColor = Theme.Green;
                                networkLink.Visible = false;
                                privateButton.Visible = false;
                            }
                            else if (!busy && errorTitle == null) notice = "\"" + name + "\" is now Private, so your TV can connect.";
                            UpdateUi();
                        },
                        ex =>
                        {
                            SetNetworkBusy(false, null);
                            MessageBox.Show(this, Describe(ex), "JellyScale", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        });
                },
                ex =>
                {
                    SetNetworkBusy(false, null);
                    MessageBox.Show(this, Describe(ex), "JellyScale", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
        }

        void SetNetworkBusy(bool on, string buttonText)
        {
            networkBusy = on;
            privateButton.Enabled = !on;
            privateButton.Text = buttonText ?? "Make Private";
        }

        void TurnOff(string successNotice, Action then)
        {
            SetBusy("Turning off…");
            RunAsync(() => backend.TurnOff(Progress),
                () =>
                {
                    SetBusy(null);
                    state = BridgeState.Off;
                    notPrivate = false;
                    notice = successNotice ?? "Sleep timers restored. The Balanced power plan is active.";
                    UpdateUi();
                    if (then != null) then();
                },
                ex =>
                {
                    SetBusy(null);
                    state = SafeState();
                    ShowError("Couldn't turn off", Describe(ex));
                });
        }

        void ShowError(string title, string text)
        {
            errorTitle = title;
            errorText = text;
            UpdateUi();
            if (!Visible && tray != null) tray.ShowBalloonTip(6000, title, text, ToolTipIcon.Warning);
        }

        void Poll()
        {
            if (busy || setupBusy || pollInFlight || autoPending) return;
            pollInFlight = true;
            int gen = generation;
            BridgeState s = state;
            string ip = null;
            RunAsync(() => { s = backend.GetState(); ip = backend.LanIP(); },
                () =>
                {
                    pollInFlight = false;
                    if (busy || gen != generation) return;
                    var previous = state;
                    bool first = !firstStateLoaded;
                    firstStateLoaded = true;
                    state = s;
                    lanIP = ip;
                    if (s == BridgeState.Interrupted && backend.ServerIP != null)
                    {
                        if (!Visible && tray != null && previous == BridgeState.On)
                            tray.ShowBalloonTip(6000, "JellyScale turned off", "The bridge stopped unexpectedly, so your sleep settings were restored. Open JellyScale to turn it on again.", ToolTipIcon.Warning);
                        TurnOff(previous == BridgeState.On && !first
                            ? "The bridge stopped unexpectedly, so JellyScale turned off and restored your sleep settings."
                            : "JellyScale was left on (for example by a restart). Your sleep settings have been restored.", null);
                        return;
                    }
                    UpdateUi();
                },
                ex => { pollInFlight = false; Log.Write("Status check failed: " + ex.Message); });
        }

        void Animate()
        {
            if (!Visible || WindowState == FormWindowState.Minimized) return;
            if (mainView.Visible && power.Animating) power.Tick(0.033f);
            if (setupView.Visible) foreach (var r in rows) r.Tick(0.033f);
        }

        // ------------------------------------------------------------------ main view

        void UpdateUi()
        {
            bool on = state == BridgeState.On, interrupted = state == BridgeState.Interrupted;
            power.State = busy ? PowerButton.Mode.Busy : on ? PowerButton.Mode.On : interrupted ? PowerButton.Mode.Attention : PowerButton.Mode.Off;
            details.Visible = false;

            if (busy)
            {
                status.Text = busyTitle; status.ForeColor = Theme.Text;
                sub.Text = busyText; sub.ForeColor = Theme.Muted;
            }
            else if (errorTitle != null)
            {
                status.Text = errorTitle; status.ForeColor = Theme.Red;
                sub.Text = errorText; sub.ForeColor = Theme.Muted;
                var need = TextRenderer.MeasureText(errorText, sub.Font, new Size(sub.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                details.Visible = need.Height > sub.Height || errorText.Length > 110;
            }
            else if (on)
            {
                status.Text = "ON"; status.ForeColor = Theme.Green;
                sub.Text = notice ?? "Your TV can connect now. Keep this PC on and awake."; sub.ForeColor = Theme.Muted;
            }
            else if (interrupted)
            {
                status.Text = "Needs attention"; status.ForeColor = Theme.Amber;
                sub.Text = "JellyScale was left on without the bridge running. Press the button to restore your sleep settings."; sub.ForeColor = Theme.Muted;
            }
            else
            {
                status.Text = "OFF"; status.ForeColor = Theme.Text;
                sub.Text = notice ?? "Press the button to start the bridge for your TV."; sub.ForeColor = Theme.Muted;
            }

            // TV address card
            string address = lanIP != null ? "http://" + lanIP + ":" + AppInfo.TvPort : "No home network found";
            tvAddress.Text = address;
            tvAddress.ForeColor = lanIP == null ? Theme.Muted : on ? Theme.Text : Color.FromArgb(200, 205, 235);
            FitFont(tvAddress, 13.5f, 9.5f);
            copyButton.Enabled = lanIP != null;
            tvHint.Cursor = Cursors.Default;
            if (lanIP == null) { tvHint.Text = "Connect this PC to your home Wi-Fi or Ethernet."; tvHint.ForeColor = Theme.Amber; }
            else if (on && notPrivate) { tvHint.Text = "Network is set to Public, so the TV can't connect. Fix it ›"; tvHint.ForeColor = Theme.Amber; tvHint.Cursor = Cursors.Hand; }
            else if (on) { tvHint.Text = "Enter this address in your TV's Jellyfin app."; tvHint.ForeColor = Theme.Muted; }
            else { tvHint.Text = "Turn JellyScale on, then enter this in your TV's Jellyfin app."; tvHint.ForeColor = Theme.Faint; }

            // Server card
            serverValue.Text = backend.ServerIP != null ? backend.ServerIP + "  :  " + AppInfo.ServerPort : "Not set";
            changeButton.Enabled = !busy;
            menuChangeServer.Enabled = !busy && !setupView.Visible;

            // Tray
            if (tray != null)
            {
                tray.Text = on ? "JellyScale - ON" : busy ? "JellyScale - working…" : interrupted ? "JellyScale - needs attention" : "JellyScale - OFF";
                var icon = on ? trayOn : trayOff;
                if (icon != null && tray.Icon != icon) tray.Icon = icon;
                trayToggle.Text = on || interrupted ? "Turn OFF" : "Turn ON";
                trayToggle.Enabled = !busy && !setupBusy && backend.ServerIP != null;
                trayExit.Enabled = !busy && !setupBusy;
            }
        }

        static void FitFont(Label label, float max, float min)
        {
            float size = max;
            while (size > min)
            {
                using (var f = Theme.Semibold(size))
                    if (TextRenderer.MeasureText(label.Text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width <= label.Width - Theme.S(6)) break;
                size -= 0.5f;
            }
            if (Math.Abs(label.Font.Size - size) > 0.01f) label.Font = Theme.Semibold(size);
        }

        void CopyAddress()
        {
            if (lanIP == null) return;
            try { Clipboard.SetText("http://" + lanIP + ":" + AppInfo.TvPort); }
            catch { return; }
            copyButton.Text = "Copied";
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += delegate { copyButton.Text = "Copy"; t.Dispose(); };
            t.Start();
        }

        void ChangeServer()
        {
            if (busy) return;
            if (state != BridgeState.Off)
            {
                MessageBox.Show(this, "Turn JellyScale off before changing the server.", "JellyScale", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            PrepareSetup(false);
        }

        void OpenFolder()
        {
            try { Process.Start("explorer.exe", "\"" + Paths.Root + "\""); } catch { }
        }

        void OpenNetworkSettings()
        {
            try { Process.Start("ms-settings:network-status"); } catch { }
        }

        void ShowAbout()
        {
            MessageBox.Show(this,
                "JellyScale " + AppInfo.Version + "\nby " + AppInfo.Publisher +
                "\n\nWatch your Jellyfin server on your TV, with this PC as a bridge over Tailscale. Made for TVs that have a Jellyfin app but can't install Tailscale." +
                "\n\nIndependent project, not affiliated with Jellyfin or Tailscale. The bridge runs Caddy (Apache License 2.0)." +
                "\n\nInstalled in: " + Paths.Root,
                "About JellyScale", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ------------------------------------------------------------------ setup view

        void PrepareSetup(bool firstRun)
        {
            checkedIP = null;
            ipBox.Text = backend.ServerIP ?? "";
            ipBox.ReadOnly = false;
            foreach (var r in rows) r.Status = CheckStatus.Pending;
            checkDetail.Text = "";
            networkLink.Visible = false;
            privateButton.Visible = false;
            cancelButton.Visible = !firstRun;
            cancelButton.Enabled = true;
            saveButton.Text = "Check & save";
            saveButton.Enabled = true;
            setupTitle.Text = firstRun ? "Connect your server" : "Change server";
            mainView.Visible = false;
            setupView.Visible = true;
            menuChangeServer.Enabled = false;
            if (IsHandleCreated) { ipBox.Focus(); ipBox.SelectAll(); }
            try { Native.SetCueBanner(ipBox.Handle, "100.x.y.z"); } catch { }
        }

        void CloseSetup()
        {
            if (setupBusy) return;
            setupView.Visible = false;
            mainView.Visible = true;
            UpdateUi();
            power.Focus();
        }

        void SetRow(int index, CheckStatus st, string detail)
        {
            rows[index].Status = st;
            if (detail != null)
            {
                checkDetail.Text = detail;
                checkDetail.ForeColor = st == CheckStatus.Fail ? Theme.Red : Theme.Amber;
                bool link = index == 3 && st == CheckStatus.Warn;
                networkLink.Visible = link;
                privateButton.Visible = link;
                checkDetail.Height = Theme.S(link ? 32 : 48);
            }
        }

        void SaveClicked()
        {
            if (setupBusy) return;
            if (checkedIP != null) { FinishSetup(); return; }

            setupBusy = true;
            ipBox.ReadOnly = true;
            cancelButton.Enabled = false;
            saveButton.Enabled = false;
            saveButton.Text = "Checking…";
            foreach (var r in rows) r.Status = CheckStatus.Pending;
            checkDetail.Text = "";
            networkLink.Visible = false;
            privateButton.Visible = false;
            UpdateUi();

            string input = ipBox.Text, ip = null;
            RunAsync(() =>
                {
                    ip = backend.CheckServer(input, (i, st, d) => UI(delegate { SetRow(i, st, d); }));
                    if (ip != null) backend.SaveServer(ip);
                },
                () =>
                {
                    setupBusy = false;
                    ipBox.ReadOnly = false;
                    cancelButton.Enabled = true;
                    saveButton.Enabled = true;
                    if (ip == null)
                    {
                        saveButton.Text = "Check again";
                        ipBox.Focus();
                        UpdateUi();
                        return;
                    }
                    ipBox.Text = ip;
                    checkedIP = ip;
                    if (rows.Any(r => r.Status == CheckStatus.Warn))
                    {
                        saveButton.Text = "Continue";
                        saveButton.Focus();
                        UpdateUi();
                    }
                    else
                    {
                        saveButton.Text = "Saved";
                        saveButton.Enabled = false;
                        var t = new System.Windows.Forms.Timer { Interval = 650 };
                        t.Tick += delegate { t.Dispose(); FinishSetup(); };
                        t.Start();
                    }
                },
                ex =>
                {
                    setupBusy = false;
                    ipBox.ReadOnly = false;
                    cancelButton.Enabled = true;
                    saveButton.Enabled = true;
                    saveButton.Text = "Check again";
                    checkDetail.Text = Describe(ex);
                    checkDetail.ForeColor = Theme.Red;
                    UpdateUi();
                });
        }

        void FinishSetup()
        {
            checkedIP = null;
            notice = "Server saved. Press the button to start the bridge.";
            errorTitle = null;
            setupView.Visible = false;
            mainView.Visible = true;
            UpdateUi();
            power.Focus();
            Poll();
        }
    }
}
