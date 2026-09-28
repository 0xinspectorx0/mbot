using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicPlayerApp
{
    internal static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                // Acilis hatasini gorunur yap (sessiz kapanmayi onle)
                MessageBox.Show(
                    "Program baslatilamadi:\n\n" + ex.Message +
                    "\n\nKlasor yapisi:\n- Exe ile birlikte Images, Fishes, ChatResources klasorleri ayni klasorde olmali." +
                    "\n- Ornek duzen: mbot\\Metin2AutoFishCSharp.exe + mbot\\Images\\ ...",
                    "Metin2AutoFishCSharp",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
