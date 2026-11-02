using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("JellyScale")]
[assembly: AssemblyDescription("Watch your Jellyfin server on your TV, with this PC as a bridge over Tailscale.")]
[assembly: AssemblyCompany("Demolisher9")]
[assembly: AssemblyProduct("JellyScale")]
[assembly: AssemblyCopyright("Copyright (C) 2026 Demolisher9")]
[assembly: AssemblyVersion("0.4.0.0")]
[assembly: AssemblyFileVersion("0.4.0.0")]
[assembly: AssemblyInformationalVersion("0.4.0")]
[assembly: ComVisible(false)]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace JellyScale
{
    static class Program
    {
        // Command line (used by JellyScale-Setup.exe; output goes to the installer's log):
        //   JellyScale.exe --setup [--import <dir>] [--autostart on|off]
        //                                             get Caddy, add the firewall rule, import old settings,
        //                                             set Start with Windows
        //   JellyScale.exe --stop                     close the window, turn the bridge OFF if it is on
        //   JellyScale.exe --uninstall                --stop, then remove the firewall rule and Start with Windows,
        //                                             and set the network JellyScale made Private back to Public
        //   JellyScale.exe --autostart                (Start with Windows task) open in the tray and turn ON
        //   JellyScale.exe --demo[-off|-on|-error|-public|-interrupted|-autostart]   preview the UI with a fake backend
        [STAThread]
        static int Main(string[] args)
        {
            Paths.Init();
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (verb == "--setup" || verb == "--stop" || verb == "--uninstall") return RunVerb(verb, args);

            bool demo = verb.StartsWith("--demo");
            bool autostart = verb == "--autostart" || verb == "--demo-autostart";
            bool createdNew;
            var single = new Mutex(true, demo ? @"Local\JellyScale-UI-Demo" : @"Local\JellyScale-UI", out createdNew);
            if (!createdNew)
            {
                if (!demo && !autostart) Native.Broadcast(AppInfo.ShowMessage); // bring the running window forward
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                Log.Write("Unhandled: " + e.Exception);
                MessageBox.Show("JellyScale ran into an unexpected problem:\n\n" + e.Exception.Message, "JellyScale", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal: " + e.ExceptionObject);

            Application.Run(new MainForm(demo ? (IBackend)new DemoBackend(verb) : new RealBackend(), autostart));
            GC.KeepAlive(single);
            return 0;
        }

        /// <summary>The value after `name` on the command line, or null.</summary>
        static string Option(string[] args, string name)
        {
            for (int i = 1; i < args.Length - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        static void Say(string text)
        {
            Log.Write(text);
            // The installer log reads plain ANSI text, so keep this ASCII.
            var sb = new StringBuilder();
            foreach (char c in text.Replace("…", "...").Replace("—", "-").Replace("–", "-").Replace("’", "'"))
                sb.Append(c < 128 ? c : '?');
            try { Console.WriteLine(sb.ToString()); } catch { }
        }

        static int RunVerb(string verb, string[] args)
        {
            try
            {
                if (verb == "--setup")
                {
                    string import = Option(args, "--import"), autostart = Option(args, "--autostart");
                    CloseOtherWindows();
                    using (OperationLock.Acquire(30000))
                    {
                        Installer.Setup(import, Say);
                        // Not essential, so a failure here doesn't fail setup.
                        try
                        {
                            if (autostart == "on") { AutoStart.Enable(); Say("JellyScale will start and turn on when you sign in to Windows."); }
                            else if (autostart == "off" && AutoStart.IsEnabled()) { AutoStart.Disable(); Say("JellyScale won't start with Windows."); }
                        }
                        catch (Exception ex)
                        {
                            Log.Write(ex.ToString());
                            Say("Couldn't set up Start with Windows: " + ex.Message + " You can turn it on later in JellyScale's menu.");
                        }
                    }
                    Say("JellyScale is set up.");
                    return 0;
                }

                CloseOtherWindows();
                using (OperationLock.Acquire(30000))
                {
                    if (Bridge.GetState() != BridgeState.Off)
                    {
                        Say("Turning JellyScale off and restoring sleep settings...");
                        Bridge.Stop();
                        Say("Sleep settings restored; the Balanced power plan is active.");
                    }
                    else Say("JellyScale is off.");
                    if (verb == "--uninstall")
                    {
                        Say("Removing the firewall rule...");
                        Installer.RemoveFirewallRule();
                        // The bridge is already off, so these are tidy-ups: report problems but don't block uninstall.
                        try
                        {
                            if (AutoStart.IsEnabled()) { Say("Removing Start with Windows..."); AutoStart.Disable(); }
                        }
                        catch (Exception ex) { Log.Write(ex.ToString()); Say("Couldn't remove Start with Windows: " + ex.Message); }
                        try { HomeNetwork.RestoreForUninstall(Say); }
                        catch (Exception ex) { Log.Write(ex.ToString()); Say("Couldn't check the network type: " + ex.Message); }
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write(ex.ToString());
                Say((ex is BridgeException ? "" : "Unexpected error: ") + ex.Message);
                return 1;
            }
        }

        /// <summary>Asks a running JellyScale window to close (killing it if it doesn't), so files can be replaced.</summary>
        static void CloseOtherWindows()
        {
            int me = Process.GetCurrentProcess().Id;
            var others = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.Exe))
                .Where(p => p.Id != me && string.Equals(Native.ProcessPath(p.Id), Paths.Exe, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (others.Count == 0) return;
            Say("Closing the JellyScale window...");
            Native.Broadcast(AppInfo.ExitMessage);
            foreach (var p in others)
            {
                using (p)
                {
                    try
                    {
                        if (!p.WaitForExit(5000)) { p.Kill(); p.WaitForExit(3000); }
                    }
                    catch { }
                }
            }
        }
    }
}
