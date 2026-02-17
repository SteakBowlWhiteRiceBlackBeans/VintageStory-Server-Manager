using System;
using System.Windows.Forms;

namespace Drewski_s_Server_Manager
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}
