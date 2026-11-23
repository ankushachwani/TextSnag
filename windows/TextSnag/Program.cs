using System;
using System.Threading;
using System.Windows.Forms;

namespace TextSnag
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Command-line modes (used for testing): `TextSnag --ocr <image>`, `TextSnag --selftest`, …
            if (args.Length > 0)
            {
                // A GUI app has no console: print to the terminal that started us, unless output is
                // already going somewhere (a pipe or file, e.g. in CI).
                if (Native.GetStdHandle(-11 /* STD_OUTPUT_HANDLE */) == IntPtr.Zero) Native.AttachConsole(-1 /* parent */);
                return Cli.Run(args);
            }

            // One copy at a time. Starting it again (Start menu, double-click) opens Settings instead.
            using (var instance = new Mutex(true, @"Local\TextSnag.Instance", out bool first))
            using (var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TextSnag.ShowSettings"))
            {
                if (!first)
                {
                    showSettings.Set();
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (sender, e) => Log.Write("unhandled: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (sender, e) => Log.Write("fatal: " + e.ExceptionObject);

                Application.Run(new TrayApp(showSettings));
                GC.KeepAlive(instance);
                return 0;
            }
        }
    }
}
