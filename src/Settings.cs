using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace ObsNetworkViewer
{
    sealed class Settings
    {
        public int ObsPort = 4455;
        public string ObsPassword = "";
        public bool AlwaysOnTop;

        static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OBSNetworkViewer", "settings.ini");
            }
        }

        public static Settings Load()
        {
            var settings = new Settings();
            try
            {
                if (!File.Exists(FilePath))
                    return settings;

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                        values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                string value;
                int port;
                if (values.TryGetValue("ObsPort", out value) && int.TryParse(value, out port))
                    settings.ObsPort = port;
                if (values.TryGetValue("ObsPassword", out value) && value.Length > 0)
                    settings.ObsPassword = Unprotect(value);
                if (values.TryGetValue("AlwaysOnTop", out value))
                    settings.AlwaysOnTop = value == "1";
            }
            catch (Exception) { }
            return settings;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "ObsPort=" + ObsPort,
                    "ObsPassword=" + (ObsPassword.Length > 0 ? Protect(ObsPassword) : ""),
                    "AlwaysOnTop=" + (AlwaysOnTop ? "1" : "0")
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save settings:\n" + ex.Message, "OBS Network Viewer",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static string Protect(string text)
        {
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        static string Unprotect(string base64)
        {
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(base64), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception)
            {
                return "";
            }
        }
    }

    sealed class SettingsForm : Form
    {
        readonly NumericUpDown portInput;
        readonly TextBox passwordInput;

        public SettingsForm(Settings settings)
        {
            Text = "OBS WebSocket settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(380, 190);

            var hint = new Label
            {
                Text = "In OBS open Tools > WebSocket Server Settings, tick \"Enable WebSocket server\" " +
                       "and copy the port and password here.",
                Location = new Point(12, 12),
                Size = new Size(356, 40)
            };

            var portLabel = new Label { Text = "Server port:", Location = new Point(12, 66), AutoSize = true };
            portInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 65535,
                Value = Math.Max(1, Math.Min(65535, settings.ObsPort)),
                Location = new Point(120, 63),
                Width = 90
            };

            var passwordLabel = new Label { Text = "Server password:", Location = new Point(12, 100), AutoSize = true };
            passwordInput = new TextBox
            {
                Text = settings.ObsPassword,
                UseSystemPasswordChar = true,
                Location = new Point(120, 97),
                Width = 248
            };

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(212, 148), Width = 75 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(293, 148), Width = 75 };
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.AddRange(new Control[] { hint, portLabel, portInput, passwordLabel, passwordInput, ok, cancel });
        }

        public int Port { get { return (int)portInput.Value; } }
        public string Password { get { return passwordInput.Text; } }
    }
}
