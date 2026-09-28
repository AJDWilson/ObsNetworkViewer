using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ObsNetworkViewer
{
    static class Program
    {
        public static Icon LoadAppIcon()
        {
            using (Stream stream = typeof(Program).Assembly.GetManifestResourceStream("ObsNetworkViewer.app.ico"))
                return stream != null ? new Icon(stream) : null;
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
