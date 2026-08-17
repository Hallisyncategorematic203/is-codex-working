using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace IsCodexWorking
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "IsCodexWorking.SingleInstance.5D514E64", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (TrayApplicationContext context = new TrayApplicationContext())
                {
                    Application.Run(context);
                }
            }
        }
    }

    internal sealed class TrayApplicationContext : ApplicationContext, IDisposable
    {
        private readonly NotifyIcon _tray;
        private readonly StatusPopupForm _popup;
        private readonly DetailsForm _details;
        private readonly MonitorEngine _engine;
        private readonly Control _dispatcher;
        private Icon _currentIcon;
        private StatusSnapshot _snapshot;
        private bool _trayClickSawPopupOpen;
        private bool _disposed;

        public TrayApplicationContext()
        {
            _dispatcher = new Control();
            _dispatcher.CreateControl();

            _popup = new StatusPopupForm();
            _details = new DetailsForm();
            _popup.DetailsRequested += ShowDetails;
            _popup.RefreshRequested += RefreshVisiblePopup;
            _details.RefreshRequested += RefreshVisibleDetails;

            _tray = new NotifyIcon();
            _currentIcon = StatusPainter.CreateTrayIcon(PublicState.Idle);
            _tray.Icon = _currentIcon;
            _tray.Text = "Is Codex Working?";
            _tray.MouseDown += OnTrayMouseDown;
            _tray.MouseUp += OnTrayMouseUp;
            _tray.ContextMenuStrip = BuildMenu();
            _tray.Visible = true;

            _engine = new MonitorEngine();
            _engine.SnapshotChanged += OnEngineSnapshot;
            _engine.GroupNotification += OnEngineGroupNotification;
            ApplySnapshot(_engine.Current, true);
            _engine.Start();
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem open = new ToolStripMenuItem("Open status");
            open.Click += delegate { TogglePopup(); };
            menu.Items.Add(open);

            ToolStripMenuItem details = new ToolStripMenuItem("Details");
            details.Click += delegate { ShowDetails(); };
            menu.Items.Add(details);

            ToolStripMenuItem copy = new ToolStripMenuItem("Copy diagnostics");
            copy.Click += delegate { CopyDiagnostics(); };
            menu.Items.Add(copy);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { ExitApplication(); };
            menu.Items.Add(exit);
            return menu;
        }

        private void OnEngineSnapshot(StatusSnapshot snapshot)
        {
            if (_disposed) return;
            try
            {
                _dispatcher.BeginInvoke((MethodInvoker)delegate { ApplySnapshot(snapshot, false); });
            }
            catch { }
        }

        private void OnEngineGroupNotification(GroupStatusSnapshot group)
        {
            if (_disposed || group == null) return;
            try
            {
                _dispatcher.BeginInvoke((MethodInvoker)delegate { NotifyGroup(group); });
            }
            catch { }
        }

        private void ApplySnapshot(StatusSnapshot snapshot, bool initial)
        {
            if (snapshot == null || _disposed) return;
            PublicState oldState = _snapshot == null ? snapshot.PrimaryState : _snapshot.PrimaryState;
            _snapshot = snapshot.Clone();
            _popup.UpdateSnapshot(_snapshot);
            _details.UpdateSnapshot(_snapshot);

            if (_currentIcon == null || oldState != _snapshot.PrimaryState || initial)
            {
                Icon next = StatusPainter.CreateTrayIcon(_snapshot.PrimaryState);
                Icon old = _currentIcon;
                _currentIcon = next;
                _tray.Icon = next;
                if (old != null) old.Dispose();
            }

            string tooltip = "Codex: " + _snapshot.PublicTitle;
            if (tooltip.Length > 60) tooltip = tooltip.Substring(0, 60);
            try { _tray.Text = tooltip; } catch { }

        }

        private void NotifyGroup(GroupStatusSnapshot group)
        {
            string title = group.PublicTitle;
            string text = (group.Project ?? "Unknown project") + " · " + group.PublicSubtitle;
            ToolTipIcon icon = ToolTipIcon.Info;
            switch (group.State)
            {
                case PublicState.Stuck:
                    icon = ToolTipIcon.Warning;
                    break;
                case PublicState.LimitReached:
                    icon = ToolTipIcon.Warning;
                    break;
                case PublicState.Error:
                    icon = ToolTipIcon.Error;
                    break;
            }
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text ?? string.Empty;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(3500);
            }
            catch { }
        }

        private void OnTrayMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                _trayClickSawPopupOpen = _popup.Visible;
        }

        private void OnTrayMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_trayClickSawPopupOpen)
            {
                _popup.Hide();
                _trayClickSawPopupOpen = false;
                return;
            }
            _trayClickSawPopupOpen = false;
            TogglePopup();
        }

        private void TogglePopup()
        {
            if (_popup.Visible) _popup.Hide();
            else
            {
                _snapshot = _engine.Current;
                _popup.UpdateSnapshot(_snapshot);
                _popup.ShowNearTray();
            }
        }

        private void RefreshVisiblePopup()
        {
            if (!_popup.Visible) return;
            _snapshot = _engine.Current;
            _popup.UpdateSnapshot(_snapshot);
        }

        private void RefreshVisibleDetails()
        {
            if (_disposed || !_details.Visible) return;
            _snapshot = _engine.Current;
            _details.UpdateSnapshot(_snapshot);
        }

        private void ShowDetails()
        {
            _popup.Hide();
            _snapshot = _engine.Current;
            _details.UpdateSnapshot(_snapshot);
            if (!_details.Visible) _details.Show();
            _details.Activate();
            _details.BringToFront();
        }

        private void CopyDiagnostics()
        {
            _snapshot = _engine.Current;
            if (_snapshot == null) return;
            try { Clipboard.SetText(DetailsForm.DiagnosticsText(_snapshot)); }
            catch { }
        }

        private void ExitApplication()
        {
            Dispose();
            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            Dispose();
            base.ExitThreadCore();
        }

        public new void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _engine.Dispose(); } catch { }
            try { _tray.Visible = false; } catch { }
            try { _tray.Dispose(); } catch { }
            try { if (_currentIcon != null) _currentIcon.Dispose(); } catch { }
            try { _popup.Dispose(); } catch { }
            try { _details.Dispose(); } catch { }
            try { _dispatcher.Dispose(); } catch { }
        }
    }
}
