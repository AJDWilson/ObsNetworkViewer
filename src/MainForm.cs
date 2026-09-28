using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace ObsNetworkViewer
{
    sealed class MainForm : Form
    {
        const int RefreshSeconds = 10;
        const int OfflineAfterSeconds = 35;
        const int ForgetAfterSeconds = 300;

        sealed class Peer
        {
            public string Name;
            public IPAddress Address;
            public RecordState State;
            public DateTime LastSeen;
        }

        readonly Settings settings = Settings.Load();
        readonly ObsClient obs = new ObsClient();
        readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>(StringComparer.OrdinalIgnoreCase);
        readonly string localName = Environment.MachineName;

        readonly ListView list;
        readonly ToolStripStatusLabel statusLabel;
        readonly CheckBox alwaysOnTop;
        readonly Timer refreshTimer = new Timer { Interval = RefreshSeconds * 1000 };
        readonly Timer countdownTimer = new Timer { Interval = 1000 };
        readonly Font boldFont;

        PeerNetwork network;
        string networkError;
        RecordState localState = RecordState.Unknown;
        DateTime localCheckedAt;
        DateTime nextRefresh;
        bool hasChecked;
        bool checking;

        public MainForm()
        {
            Text = "OBS Network Viewer";
            Icon = Program.LoadAppIcon() ?? Icon;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(760, 340);
            MinimumSize = new Size(520, 220);
            StartPosition = FormStartPosition.CenterScreen;

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(6, 6, 6, 0) };
            var refreshButton = new Button { Text = "Refresh now", AutoSize = true };
            refreshButton.Click += delegate { RefreshNow(true); };
            var settingsButton = new Button { Text = "OBS settings...", AutoSize = true };
            settingsButton.Click += delegate { EditSettings(); };
            alwaysOnTop = new CheckBox { Text = "Always on top", AutoSize = true, Checked = settings.AlwaysOnTop, Margin = new Padding(12, 7, 3, 3) };
            alwaysOnTop.CheckedChanged += delegate
            {
                TopMost = settings.AlwaysOnTop = alwaysOnTop.Checked;
                settings.Save();
            };
            toolbar.Controls.AddRange(new Control[] { refreshButton, settingsButton, alwaysOnTop });

            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = new Font(Font.FontFamily, 10f)
            };
            list.Columns.Add("Computer", 220);
            list.Columns.Add("Status", 280);
            list.Columns.Add("IP address", 120);
            list.Columns.Add("Last update", 110);
            boldFont = new Font(list.Font, FontStyle.Bold);

            var statusStrip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(statusLabel);

            Controls.Add(list);
            Controls.Add(toolbar);
            Controls.Add(statusStrip);

            TopMost = settings.AlwaysOnTop;
            obs.Port = settings.ObsPort;
            obs.Password = settings.ObsPassword;

            refreshTimer.Tick += delegate { RefreshNow(false); };
            countdownTimer.Tick += delegate { UpdateStatusBar(); };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            try
            {
                network = new PeerNetwork();
                network.MessageReceived += OnPeerMessage;
                network.Start();
            }
            catch (Exception ex)
            {
                network = null;
                networkError = "Network sharing disabled: " + ex.Message;
            }

            RebuildList();
            RefreshNow(true);
            refreshTimer.Start();
            countdownTimer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            refreshTimer.Stop();
            countdownTimer.Stop();
            if (network != null)
            {
                network.Send("bye", localName, localState);
                network.Dispose();
                network = null;
            }
            obs.Dispose();
            base.OnFormClosing(e);
        }

        /// <param name="queryPeers">Ask every other instance to reply with its status immediately.</param>
        async void RefreshNow(bool queryPeers)
        {
            if (checking)
                return;
            checking = true;
            refreshTimer.Stop();
            refreshTimer.Start();
            nextRefresh = DateTime.Now.AddSeconds(RefreshSeconds);
            try
            {
                localState = await obs.GetRecordStateAsync();
            }
            finally
            {
                checking = false;
            }
            if (IsDisposed)
                return;

            localCheckedAt = DateTime.Now;
            hasChecked = true;
            if (network != null)
                network.Send(queryPeers ? "query" : "status", localName, localState);
            RebuildList();
        }

        void OnPeerMessage(PeerMessage message)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            try
            {
                BeginInvoke(new Action(() => HandlePeerMessage(message)));
            }
            catch (InvalidOperationException) { }
        }

        void HandlePeerMessage(PeerMessage message)
        {
            if (IsDisposed || string.IsNullOrEmpty(message.Name))
                return;

            if (message.Type == "bye")
            {
                peers.Remove(message.Name);
            }
            else
            {
                Peer peer;
                if (!peers.TryGetValue(message.Name, out peer))
                    peers[message.Name] = peer = new Peer { Name = message.Name };
                peer.Address = message.Address;
                peer.State = message.State;
                peer.LastSeen = DateTime.Now;

                if (message.Type == "query" && hasChecked && network != null)
                    network.Send("status", localName, localState);
            }
            RebuildList();
        }

        void EditSettings()
        {
            using (var dialog = new SettingsForm(settings))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                settings.ObsPort = dialog.Port;
                settings.ObsPassword = dialog.Password;
                settings.Save();
            }
            obs.Port = settings.ObsPort;
            obs.Password = settings.ObsPassword;
            obs.Disconnect();
            RefreshNow(false);
        }

        void RebuildList()
        {
            DateTime now = DateTime.Now;
            foreach (string stale in peers.Values.Where(p => (now - p.LastSeen).TotalSeconds > ForgetAfterSeconds).Select(p => p.Name).ToList())
                peers.Remove(stale);

            list.BeginUpdate();
            list.Items.Clear();
            list.Items.Add(CreateRow(localName + "  (this PC)", localState, false, "", hasChecked ? localCheckedAt : (DateTime?)null));
            foreach (Peer peer in peers.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                bool offline = (now - peer.LastSeen).TotalSeconds > OfflineAfterSeconds;
                list.Items.Add(CreateRow(peer.Name, peer.State, offline, peer.Address.ToString(), peer.LastSeen));
            }
            list.EndUpdate();

            Text = "OBS Network Viewer - " + localName + ": " + Describe(localState, false);
            UpdateStatusBar();
        }

        ListViewItem CreateRow(string name, RecordState state, bool offline, string address, DateTime? lastSeen)
        {
            var item = new ListViewItem(new[]
            {
                name,
                Describe(state, offline),
                address,
                lastSeen.HasValue ? lastSeen.Value.ToString("HH:mm:ss") : ""
            });

            if (offline)
            {
                item.ForeColor = Color.Gray;
            }
            else if (state == RecordState.Recording)
            {
                item.BackColor = Color.FromArgb(255, 200, 200);
                item.ForeColor = Color.DarkRed;
                item.Font = boldFont;
            }
            else if (state == RecordState.Paused)
            {
                item.BackColor = Color.FromArgb(255, 236, 179);
                item.Font = boldFont;
            }
            else if (state == RecordState.Idle)
            {
                item.BackColor = Color.FromArgb(220, 245, 220);
            }
            else
            {
                item.ForeColor = Color.DimGray;
            }
            return item;
        }

        static string Describe(RecordState state, bool offline)
        {
            if (offline)
                return "Offline (no reply)";
            switch (state)
            {
                case RecordState.Recording: return "\u25CF RECORDING";
                case RecordState.Paused: return "Recording paused";
                case RecordState.Idle: return "Not recording";
                case RecordState.NotRunning: return "OBS not running";
                case RecordState.WebSocketUnavailable: return "OBS running - WebSocket not reachable";
                case RecordState.PasswordRequired: return "OBS running - enter WebSocket password in OBS settings";
                case RecordState.AuthFailed: return "OBS running - wrong WebSocket password";
                default: return "Checking...";
            }
        }

        void UpdateStatusBar()
        {
            int seconds = Math.Max(0, (int)Math.Ceiling((nextRefresh - DateTime.Now).TotalSeconds));
            string text = checking ? "Checking OBS..." : "Next refresh in " + seconds + " s";
            text += "   |   " + (peers.Count + 1) + " computer(s)";
            if (networkError != null)
                text += "   |   " + networkError;
            statusLabel.Text = text;
        }
    }
}
