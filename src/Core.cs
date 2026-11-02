// JellyScale (formerly JellyHop) - core logic (ported from the Jellyfin TV Bridge PowerShell scripts).
// Written in C# 5 so it also builds with the csc.exe that ships with Windows.
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace JellyScale
{
    static class AppInfo
    {
        public const string Name = "JellyScale";
        public const string Version = "0.4.0";
        public const string Publisher = "Demolisher9";
        public const int TvPort = 8090;
        public const int ServerPort = 8096;
        public const string FirewallRule = "JellyScale";
        public const string OperationMutex = @"Global\JellyScale-Operation";
        public const string ShowMessage = "JellyScale.Show.v1";
        public const string ExitMessage = "JellyScale.Exit.v1";
    }

    /// <summary>Errors that are safe to show the user as-is.</summary>
    class BridgeException : Exception
    {
        public BridgeException(string message) : base(message) { }
    }

    /// <summary>The Jellyfin server didn't answer (nothing was changed). Worth retrying while Tailscale connects.</summary>
    class ServerUnreachableException : BridgeException
    {
        public ServerUnreachableException(string message) : base(message) { }
    }

    static class Paths
    {
        public static string Root { get; private set; }
        public static string Exe { get; private set; }

        public static void Init()
        {
            Exe = System.Reflection.Assembly.GetEntryAssembly().Location;
            Root = Path.GetDirectoryName(Exe);
        }

        public static string Caddy { get { return Path.Combine(Root, "caddy.exe"); } }
        public static string Caddyfile { get { return Path.Combine(Root, "Caddyfile"); } }
        public static string Settings { get { return Path.Combine(Root, "settings.json"); } }
        public static string PowerState { get { return Path.Combine(Root, "power-state.json"); } }
        public static string CaddyLog { get { return Path.Combine(Root, "caddy.log"); } }
        public static string AppLog { get { return Path.Combine(Root, "jellyscale.log"); } }
        public static string NetworkChanges { get { return Path.Combine(Root, "network-changes.json"); } }
    }

    static class Log
    {
        static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    var file = new FileInfo(Paths.AppLog);
                    if (file.Exists && file.Length > 512 * 1024)
                    {
                        File.Copy(file.FullName, file.FullName + ".old", true);
                        file.Delete();
                    }
                    File.AppendAllText(Paths.AppLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    /// <summary>Cross-process lock so two operations never change power settings at once.</summary>
    sealed class OperationLock : IDisposable
    {
        Mutex mutex;

        public static OperationLock Acquire(int waitMs)
        {
            var m = new Mutex(false, AppInfo.OperationMutex);
            bool got;
            try { got = m.WaitOne(waitMs); }
            catch (AbandonedMutexException) { got = true; }
            if (!got)
            {
                m.Dispose();
                throw new BridgeException("Another JellyScale operation is running. Please wait a moment and try again.");
            }
            return new OperationLock { mutex = m };
        }

        public void Dispose()
        {
            if (mutex == null) return;
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
            mutex = null;
        }
    }

    static class Shell
    {
        public static string System32(string exe)
        {
            return Path.Combine(Environment.SystemDirectory, exe);
        }

        /// <summary>Runs a console program hidden and returns its exit code and combined output.</summary>
        public static int Run(string file, string args, out string output, int timeoutMs, Action<string> onLine)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var sb = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                DataReceivedEventHandler handler = (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (sb) sb.AppendLine(e.Data);
                    if (onLine != null && e.Data.Trim().Length > 0) onLine(e.Data);
                };
                p.OutputDataReceived += handler;
                p.ErrorDataReceived += handler;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new BridgeException(Path.GetFileName(file) + " did not finish in time.");
                }
                p.WaitForExit();
                lock (sb) output = sb.ToString();
                return p.ExitCode;
            }
        }

        public static int Run(string file, string args, out string output)
        {
            return Run(file, args, out output, 60000, null);
        }

        public static string FindOnPath(string exe)
        {
            var dirs = new List<string>();
            foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
            {
                string path = null;
                try { path = Environment.GetEnvironmentVariable("PATH", target); } catch { }
                if (path != null) dirs.AddRange(path.Split(';'));
            }
            foreach (var raw in dirs)
            {
                try
                {
                    var dir = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                    if (dir.Length == 0) continue;
                    var candidate = Path.Combine(dir, exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }
    }

    class AppSettings
    {
        public string ServerIP { get; set; }

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(Paths.Settings)) return new AppSettings();
                var s = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(Paths.Settings));
                return s ?? new AppSettings();
            }
            catch (Exception ex)
            {
                Log.Write("Could not read settings: " + ex.Message);
                return new AppSettings();
            }
        }

        public void Save()
        {
            File.WriteAllText(Paths.Settings, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
        }
    }

    /// <summary>High Performance sleep/hibernate handling. Behaviour is unchanged from Jellyfin TV Bridge 0.3.</summary>
    static class PowerPlan
    {
        const string High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        const string SleepGroup = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
        static readonly string[] SleepSettings = { "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", "9d7815a6-7ee4-497e-8888-515a05f02364" };

        public static bool HasBackup { get { return File.Exists(Paths.PowerState); } }

        static string Powercfg(string args)
        {
            string output;
            int code = Shell.Run(Shell.System32("powercfg.exe"), args, out output);
            if (code != 0) throw new BridgeException("Power setting failed: " + output.Trim());
            return output;
        }

        static void Save()
        {
            if (HasBackup) throw new BridgeException("A previous power backup is still pending. Turn JellyScale OFF first to restore it.");
            var rows = new List<Dictionary<string, object>>();
            foreach (var setting in SleepSettings)
            {
                string output;
                try { output = Powercfg("/query " + High + " " + SleepGroup + " " + setting); }
                catch (BridgeException)
                {
                    throw new BridgeException("The High Performance power plan isn't available on this PC, so JellyScale can't keep it awake. No settings were changed.");
                }
                // The final two hex indices are the current AC then DC values (after min/max/increment).
                var indices = Regex.Matches(output, "0x([0-9a-fA-F]{8})");
                if (indices.Count < 2) throw new BridgeException("Can't read the High Performance sleep settings. No settings were changed.");
                rows.Add(new Dictionary<string, object>
                {
                    { "Setting", setting },
                    { "AC", Convert.ToUInt32(indices[indices.Count - 2].Groups[1].Value, 16) },
                    { "DC", Convert.ToUInt32(indices[indices.Count - 1].Groups[1].Value, 16) }
                });
            }
            File.WriteAllText(Paths.PowerState, new JavaScriptSerializer().Serialize(rows), new UTF8Encoding(false));
        }

        public static void Enable()
        {
            Save();
            foreach (var setting in SleepSettings)
            {
                Powercfg("/setacvalueindex " + High + " " + SleepGroup + " " + setting + " 0");
                Powercfg("/setdcvalueindex " + High + " " + SleepGroup + " " + setting + " 0");
            }
            Powercfg("/setactive " + High);
        }

        public static void Restore()
        {
            if (HasBackup)
            {
                var rows = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(Paths.PowerState));
                if (rows == null) throw new BridgeException("Invalid power backup; it was kept for recovery.");
                foreach (var row in rows)
                {
                    var setting = Convert.ToString(row["Setting"]);
                    if (!SleepSettings.Contains(setting)) throw new BridgeException("Invalid power backup; it was kept for recovery.");
                    Powercfg("/setacvalueindex " + High + " " + SleepGroup + " " + setting + " " + Convert.ToUInt32(row["AC"]));
                    Powercfg("/setdcvalueindex " + High + " " + SleepGroup + " " + setting + " " + Convert.ToUInt32(row["DC"]));
                }
            }
            // Intentional product behaviour: OFF always selects Balanced, not the previous plan.
            Powercfg("/setactive SCHEME_BALANCED");
            if (HasBackup) File.Delete(Paths.PowerState);
        }
    }

    enum BridgeState { Off, On, Interrupted }

    static class Bridge
    {
        public static List<int> CaddyProcessIds()
        {
            var ids = new List<int>();
            string target = Path.GetFullPath(Paths.Caddy);
            foreach (var p in Process.GetProcessesByName("caddy"))
            {
                using (p)
                {
                    var path = Native.ProcessPath(p.Id);
                    if (path != null && string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase)) ids.Add(p.Id);
                }
            }
            return ids;
        }

        public static BridgeState GetState()
        {
            if (CaddyProcessIds().Count > 0) return BridgeState.On;
            if (PowerPlan.HasBackup) return BridgeState.Interrupted;
            return BridgeState.Off;
        }

        /// <summary>Stops only the Caddy that lives in the JellyScale folder, then restores power settings.</summary>
        public static void Stop()
        {
            foreach (var id in CaddyProcessIds())
            {
                try
                {
                    using (var p = Process.GetProcessById(id))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch (ArgumentException) { } // already gone
            }
            PowerPlan.Restore();
            Log.Write("Bridge OFF");
        }

        static void WriteCaddyfile(string server)
        {
            // Admin API and config autosave are disabled so this copy can't clash with another Caddy.
            var text = "{\n    admin off\n    persist_config off\n}\n:" + AppInfo.TvPort + " {\n    reverse_proxy http://" + server + ":" + AppInfo.ServerPort + "\n}\n";
            File.WriteAllText(Paths.Caddyfile, text, Encoding.ASCII);
        }

        public static void Start(string server, Action<string> progress)
        {
            if (!File.Exists(Paths.Caddy)) throw new BridgeException("Caddy is missing from the JellyScale folder. Run JellyScale Setup again to repair it.");
            progress("Checking your Jellyfin server…");
            if (!Net.CanConnect(server, AppInfo.ServerPort, 6000))
                throw new ServerUnreachableException("Can't reach your Jellyfin server at " + server + ":" + AppInfo.ServerPort + ". Check that Tailscale is connected and the server is running. No settings were changed.");
            if (Net.IsPortListening(AppInfo.TvPort))
                throw new BridgeException("Port " + AppInfo.TvPort + " is already in use by another program. No settings were changed.");
            WriteCaddyfile(server);
            try
            {
                progress("Keeping this PC awake…");
                PowerPlan.Enable();
                progress("Starting the bridge…");
                int pid = Native.StartHidden(Paths.Caddy, "run --config \"" + Paths.Caddyfile + "\"", Paths.Root, Paths.CaddyLog);
                bool listening = false, exited = false;
                for (int i = 0; i < 40 && !listening; i++)
                {
                    Thread.Sleep(200);
                    exited = !ProcessAlive(pid);
                    if (exited) break;
                    listening = Native.ListenerPids(AppInfo.TvPort).Contains(pid);
                }
                if (exited) throw new BridgeException("The bridge stopped right after starting." + LogTail());
                if (!listening) throw new BridgeException("The bridge didn't open port " + AppInfo.TvPort + "." + LogTail());
                Log.Write("Bridge ON -> " + server);
            }
            catch (Exception ex)
            {
                string original = ex.Message;
                Log.Write("Turn ON failed: " + original);
                // Only undo what was actually changed: if the backup was never written, nothing was touched.
                try { if (PowerPlan.HasBackup || CaddyProcessIds().Count > 0) Stop(); }
                catch (Exception ex2)
                {
                    throw new BridgeException(original + "\n\nRestoring your power settings also failed: " + ex2.Message + " The backup was kept; turn JellyScale OFF to retry.");
                }
                if (ex is BridgeException) throw;
                throw new BridgeException(original);
            }
        }

        static bool ProcessAlive(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; }
            catch { return false; }
        }

        static string LogTail()
        {
            try
            {
                using (var fs = new FileStream(Paths.CaddyLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                {
                    var lines = sr.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                    if (lines.Count == 0) return "";
                    return "\n\nCaddy said:\n" + string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 4)));
                }
            }
            catch { return ""; }
        }
    }

    enum NetworkProfile { Private, NotPrivate, Unknown }

    static class Net
    {
        public static bool CanConnect(string host, int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var ar = client.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    client.EndConnect(ar);
                    return client.Connected;
                }
            }
            catch { return false; }
        }

        public static bool IsPortListening(int port)
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
        }

        static bool IsPrivateLan(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
        }

        /// <summary>This PC's home-network IPv4 (has a default gateway, not Tailscale, private range).</summary>
        public static string LanIPv4()
        {
            int index;
            return LanIPv4(out index);
        }

        /// <summary>Same, plus the IPv4 interface index of that network adapter (-1 if none).</summary>
        public static string LanIPv4(out int interfaceIndex)
        {
            interfaceIndex = -1;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if ((ni.Name + " " + ni.Description).IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var props = ni.GetIPProperties();
                    bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    if (!hasGateway) continue;
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivateLan(ua.Address))
                        {
                            try { interfaceIndex = props.GetIPv4Properties().Index; } catch { }
                            return ua.Address.ToString();
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("LAN lookup failed: " + ex.Message); }
            return null;
        }

        /// <summary>Same test as before: at least one connected, non-Tailscale network must be Private.</summary>
        public static NetworkProfile HomeNetworkProfile()
        {
            try
            {
                bool any = false;
                using (var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT InterfaceAlias, NetworkCategory, IPv4Connectivity FROM MSFT_NetConnectionProfile"))
                {
                    foreach (ManagementObject o in searcher.Get())
                    {
                        using (o)
                        {
                            var alias = Convert.ToString(o["InterfaceAlias"]) ?? "";
                            if (alias.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            if (Convert.ToInt32(o["IPv4Connectivity"]) == 0) continue; // Disconnected
                            any = true;
                            if (Convert.ToInt32(o["NetworkCategory"]) == 1) return NetworkProfile.Private;
                        }
                    }
                }
                return any ? NetworkProfile.NotPrivate : NetworkProfile.Unknown;
            }
            catch (Exception ex)
            {
                Log.Write("Network profile check failed: " + ex.Message);
                return NetworkProfile.Unknown;
            }
        }

        /// <summary>Validates and normalises a Tailscale IPv4 (100.64.0.0/10). Returns null and a reason if invalid.</summary>
        public static string NormalizeTailscaleIP(string input, out string error)
        {
            error = null;
            var s = (input ?? "").Trim();
            if (s.Length == 0) { error = "Enter your server's Tailscale IPv4 address."; return null; }
            var m = Regex.Match(s, @"^100\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$");
            if (!m.Success) { error = "Enter a Tailscale IPv4 address like 100.101.102.103, without http:// or a port."; return null; }
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value), c = int.Parse(m.Groups[3].Value);
            if (a < 64 || a > 127 || b > 255 || c > 255) { error = "Tailscale IPv4 addresses are between 100.64.0.0 and 100.127.255.255."; return null; }
            return "100." + a + "." + b + "." + c;
        }

        public static bool TailscaleInstalled()
        {
            if (Shell.FindOnPath("tailscale.exe") != null) return true;
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return File.Exists(Path.Combine(pf, @"Tailscale\tailscale.exe"));
        }
    }

    /// <summary>One-time work done by the installer (runs elevated): Caddy, firewall rule, import from JellyHop or Jellyfin TV Bridge.</summary>
    static class Installer
    {
        public static void Setup(string importDir, Action<string> say)
        {
            if (File.Exists(@"C:\Caddy\Jellyfin-TV-Toggle.ps1"))
                throw new BridgeException("The old bridge is still installed in C:\\Caddy. Turn it OFF and remove that version before installing JellyScale. Setup leaves it untouched.");

            if (importDir != null && Directory.Exists(importDir)) ImportLegacy(importDir, say);

            if (!File.Exists(Paths.Caddy))
            {
                string source = FindCaddy();
                string temp = null;
                try
                {
                    if (source == null)
                    {
                        temp = Path.Combine(Path.GetTempPath(), "JellyScale-" + Guid.NewGuid().ToString("N"));
                        source = DownloadCaddy(temp, say);
                    }
                    else say("Using Caddy from " + source);
                    File.Copy(source, Paths.Caddy, true);
                }
                finally
                {
                    if (temp != null) try { Directory.Delete(temp, true); } catch { }
                }
            }
            else say("Caddy is already present.");

            string output;
            if (Shell.Run(Paths.Caddy, "version", out output) != 0)
            {
                try { File.Delete(Paths.Caddy); } catch { }
                throw new BridgeException("The Caddy program could not run: " + output.Trim());
            }
            say("Caddy " + output.Trim().Split(' ')[0]);

            say("Adding the firewall rule (TCP " + AppInfo.TvPort + ", home network only)...");
            RemoveFirewallRule();
            int code = Shell.Run(Shell.System32("netsh.exe"),
                "advfirewall firewall add rule name=\"" + AppInfo.FirewallRule + "\" dir=in action=allow enable=yes protocol=TCP localport=" + AppInfo.TvPort +
                " program=\"" + Paths.Caddy + "\" remoteip=localsubnet profile=private description=\"Lets TVs on your home network reach JellyScale.\"", out output);
            if (code != 0) throw new BridgeException("Could not add the firewall rule: " + output.Trim());
            Log.Write("Setup complete");
        }

        public static void RemoveFirewallRule()
        {
            string output;
            Shell.Run(Shell.System32("netsh.exe"), "advfirewall firewall delete rule name=\"" + AppInfo.FirewallRule + "\"", out output);
        }

        static void ImportLegacy(string dir, Action<string> say)
        {
            var legacySettings = Path.Combine(dir, "settings.json");
            if (File.Exists(legacySettings) && !File.Exists(Paths.Settings))
            {
                try
                {
                    var old = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(legacySettings));
                    string error;
                    var ip = old == null ? null : Net.NormalizeTailscaleIP(old.ServerIP, out error);
                    if (ip != null)
                    {
                        new AppSettings { ServerIP = ip }.Save();
                        say("Kept your server address from the previous version: " + ip);
                    }
                }
                catch (Exception ex) { say("Could not import the old settings: " + ex.Message); }
            }
            var legacyCaddy = Path.Combine(dir, "caddy.exe");
            if (File.Exists(legacyCaddy) && !File.Exists(Paths.Caddy))
            {
                File.Copy(legacyCaddy, Paths.Caddy);
                say("Reused Caddy from the previous version.");
            }
        }

        static string FindCaddy()
        {
            var onPath = Shell.FindOnPath("caddy.exe");
            if (onPath != null && !string.Equals(Path.GetFullPath(onPath), Path.GetFullPath(Paths.Caddy), StringComparison.OrdinalIgnoreCase)) return onPath;
            try
            {
                var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages");
                if (Directory.Exists(winget))
                {
                    foreach (var dir in Directory.GetDirectories(winget, "CaddyServer.Caddy*"))
                    {
                        var hit = Directory.GetFiles(dir, "caddy.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (hit != null) return hit;
                    }
                }
            }
            catch { }
            return null;
        }

        static string DownloadCaddy(string temp, Action<string> say)
        {
            var winget = Shell.FindOnPath("winget.exe");
            if (winget == null)
            {
                var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
                if (File.Exists(alias)) winget = alias;
            }
            if (winget == null) throw new BridgeException("Caddy isn't on this PC and WinGet (Microsoft App Installer) isn't available to download it. Install App Installer from the Microsoft Store, then run setup again.");
            Directory.CreateDirectory(temp);
            say("Downloading Caddy with WinGet...");
            // Download an app-owned copy; never install a shared WinGet package that uninstall might remove.
            string output;
            int code = Shell.Run(winget, "download --id CaddyServer.Caddy --exact --download-directory \"" + temp + "\" --accept-package-agreements --accept-source-agreements --disable-interactivity",
                out output, 15 * 60 * 1000, null);
            if (code != 0) throw new BridgeException("Caddy download failed (WinGet exit code " + code + "). Check your internet connection and try again.");
            var zip = Directory.GetFiles(temp, "*.zip", SearchOption.AllDirectories).FirstOrDefault();
            if (zip == null) throw new BridgeException("Expected a Caddy ZIP download but none arrived. Nothing was installed.");
            var extracted = Path.Combine(temp, "extracted");
            ZipFile.ExtractToDirectory(zip, extracted);
            var exe = Directory.GetFiles(extracted, "caddy.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe == null) throw new BridgeException("The Caddy download didn't contain caddy.exe.");
            say("Caddy downloaded.");
            return exe;
        }
    }

    /// <summary>
    /// "Start with Windows": a Task Scheduler task that runs "JellyScale.exe --autostart" when this user signs in.
    /// A task is used instead of the Run key because JellyScale runs as administrator: Windows skips elevated
    /// Run-key programs, while a task with highest privileges starts elevated without a UAC prompt.
    /// </summary>
    static class AutoStart
    {
        const string TaskName = AppInfo.Name;

        static string Schtasks { get { return Shell.System32("schtasks.exe"); } }

        public static bool IsEnabled()
        {
            try
            {
                string output;
                return Shell.Run(Schtasks, "/Query /TN \"" + TaskName + "\"", out output, 20000, null) == 0;
            }
            catch (Exception ex)
            {
                Log.Write("Start with Windows check failed: " + ex.Message);
                return false;
            }
        }

        public static void Enable()
        {
            var user = WindowsIdentity.GetCurrent();
            string sid = SecurityElement.Escape(user.User.Value);
            // No time limit (the default kills tasks after 72 hours), runs on battery, and normal priority (4) so
            // Caddy, which inherits it, isn't throttled. The short delay gives Tailscale a head start.
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo>\r\n" +
                "    <Author>" + SecurityElement.Escape(AppInfo.Publisher) + "</Author>\r\n" +
                "    <Description>Starts JellyScale in the notification area when you sign in and turns the TV bridge on. Turn this off in JellyScale's menu (Start with Windows).</Description>\r\n" +
                "  </RegistrationInfo>\r\n" +
                "  <Triggers>\r\n" +
                "    <LogonTrigger>\r\n" +
                "      <Enabled>true</Enabled>\r\n" +
                "      <UserId>" + sid + "</UserId>\r\n" +
                "      <Delay>PT10S</Delay>\r\n" +
                "    </LogonTrigger>\r\n" +
                "  </Triggers>\r\n" +
                "  <Principals>\r\n" +
                "    <Principal id=\"Author\">\r\n" +
                "      <UserId>" + sid + "</UserId>\r\n" +
                "      <LogonType>InteractiveToken</LogonType>\r\n" +
                "      <RunLevel>HighestAvailable</RunLevel>\r\n" +
                "    </Principal>\r\n" +
                "  </Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
                "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
                "    <IdleSettings>\r\n" +
                "      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n" +
                "      <RestartOnIdle>false</RestartOnIdle>\r\n" +
                "    </IdleSettings>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <Hidden>false</Hidden>\r\n" +
                "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
                "    <WakeToRun>false</WakeToRun>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>4</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\">\r\n" +
                "    <Exec>\r\n" +
                "      <Command>\"" + SecurityElement.Escape(Paths.Exe) + "\"</Command>\r\n" +
                "      <Arguments>--autostart</Arguments>\r\n" +
                "      <WorkingDirectory>" + SecurityElement.Escape(Paths.Root) + "</WorkingDirectory>\r\n" +
                "    </Exec>\r\n" +
                "  </Actions>\r\n" +
                "</Task>\r\n";
            string temp = Path.Combine(Path.GetTempPath(), "JellyScale-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(temp, xml, Encoding.Unicode);
                string output;
                if (Shell.Run(Schtasks, "/Create /TN \"" + TaskName + "\" /XML \"" + temp + "\" /F", out output) != 0)
                    throw new BridgeException("Windows couldn't add JellyScale to startup: " + output.Trim());
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
            Log.Write("Start with Windows: on, for " + user.Name);
        }

        public static void Disable()
        {
            if (!IsEnabled()) return;
            string output;
            if (Shell.Run(Schtasks, "/Delete /TN \"" + TaskName + "\" /F", out output) != 0)
                throw new BridgeException("Windows couldn't remove JellyScale from startup: " + output.Trim());
            Log.Write("Start with Windows: off");
        }
    }

    /// <summary>
    /// "Make Private": switches the home network (the one with the TV address) from Public to Private, and remembers
    /// what it changed so uninstall can set it back. Uses the same WMI class as the Private check, with Windows'
    /// Network List Manager as a fallback (which also reaches networks that aren't connected right now).
    /// </summary>
    static class HomeNetwork
    {
        const uint Public = 0, Private = 1, DomainAuthenticated = 2;

        public class Change
        {
            public string Name { get; set; }
            public string InterfaceAlias { get; set; }
            public int Previous { get; set; }
        }

        /// <summary>Calls `use` with the connected, non-Tailscale network profile that carries this PC's LAN
        /// address (or the only candidate), or with null if there's no clear home network.</summary>
        static T WithHomeProfile<T>(Func<ManagementObject, T> use)
        {
            int lanIndex;
            Net.LanIPv4(out lanIndex);
            using (var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT * FROM MSFT_NetConnectionProfile"))
            using (var results = searcher.Get())
            {
                var candidates = new List<ManagementObject>();
                foreach (ManagementObject o in results)
                {
                    var alias = Convert.ToString(o["InterfaceAlias"]) ?? "";
                    if (alias.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (Convert.ToInt32(o["IPv4Connectivity"]) == 0) continue; // Disconnected
                    candidates.Add(o);
                }
                var pick = candidates.FirstOrDefault(o => Convert.ToInt64(o["InterfaceIndex"]) == lanIndex);
                if (pick == null && candidates.Count == 1) pick = candidates[0];
                return use(pick);
            }
        }

        /// <summary>Calls `use` with the connected profile called `name`, or null if it isn't connected.</summary>
        static T WithProfileNamed<T>(string name, Func<ManagementObject, T> use)
        {
            using (var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT * FROM MSFT_NetConnectionProfile"))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject o in results)
                    if (string.Equals(Convert.ToString(o["Name"]), name, StringComparison.Ordinal)) return use(o);
                return use(null);
            }
        }

        /// <summary>The home network's name (as shown in Windows Settings), or null if it isn't clear which one it is.</summary>
        public static string Name()
        {
            return WithHomeProfile(p => p == null ? null : Convert.ToString(p["Name"]));
        }

        public static string MakePrivate()
        {
            return WithHomeProfile(p =>
            {
                if (p == null) throw new BridgeException("JellyScale couldn't tell which network is your home network. Use Open network settings to set it to Private.");
                string name = Convert.ToString(p["Name"]) ?? "", alias = Convert.ToString(p["InterfaceAlias"]) ?? "";
                uint category = Convert.ToUInt32(p["NetworkCategory"]);
                if (category == Private) return name;
                if (category == DomainAuthenticated) throw new BridgeException("\"" + name + "\" is a work (domain) network, so JellyScale can't change it.");
                SetCategory(p, name, Private);
                Remember(new Change { Name = name, InterfaceAlias = alias, Previous = (int)category });
                Log.Write("Set network \"" + name + "\" (" + alias + ") to Private");
                return name;
            });
        }

        /// <summary>Uninstall: networks JellyScale made Private go back to Public, unless someone changed them since.</summary>
        public static void RestoreForUninstall(Action<string> say)
        {
            var changes = LoadChanges();
            foreach (var c in changes)
            {
                try
                {
                    uint? now = WithProfileNamed(c.Name, p => p == null ? (uint?)null : Convert.ToUInt32(p["NetworkCategory"]));
                    if (now == null) now = NetworkListCategory(c.Name);
                    if (now == null) { say("Network \"" + c.Name + "\" wasn't found, so there was nothing to set back."); continue; }
                    if (now != Private) { say("Network \"" + c.Name + "\" isn't Private any more; left as it is."); continue; }
                    WithProfileNamed(c.Name, p => { SetCategory(p, c.Name, (uint)c.Previous); return true; });
                    say("Set network \"" + c.Name + "\" back to Public, as it was before JellyScale changed it.");
                    Log.Write("Set network \"" + c.Name + "\" back to Public");
                }
                catch (Exception ex)
                {
                    say("Couldn't set network \"" + c.Name + "\" back to Public (" + ex.Message + "). Change it in Settings > Network & internet if you want to.");
                }
            }
            try { File.Delete(Paths.NetworkChanges); } catch { }
        }

        static void SetCategory(ManagementObject wmi, string name, uint category)
        {
            if (wmi != null)
            {
                try
                {
                    wmi["NetworkCategory"] = category;
                    wmi.Put(new PutOptions { Type = PutType.UpdateOnly });
                    uint? check = WithProfileNamed(name, p => p == null ? (uint?)null : Convert.ToUInt32(p["NetworkCategory"]));
                    if (check == category) return;
                    Log.Write("WMI accepted the network change but it didn't apply; trying Network List Manager.");
                }
                catch (Exception ex) { Log.Write("WMI couldn't change the network type, trying Network List Manager: " + ex.Message); }
            }
            if (!NetworkListSetCategory(name, category))
                throw new BridgeException("Windows didn't let JellyScale change \"" + name + "\". Use Open network settings instead.");
        }

        // ---- Network List Manager (COM, late bound through IDispatch so a mismatch fails cleanly)

        static readonly Guid NetworkListManagerClsid = new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
        const int EnumAllNetworks = 3; // NLM_ENUM_NETWORK_ALL

        static object Call(object com, string method, params object[] args)
        {
            return com.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, com, args);
        }

        /// <summary>Runs `use` on the Network List Manager network called `name`; returns false if not found or on failure.</summary>
        static bool WithNetworkListNetwork(string name, Action<object> use)
        {
            object nlm = null;
            try
            {
                nlm = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManagerClsid));
                foreach (object network in (IEnumerable)Call(nlm, "GetNetworks", EnumAllNetworks))
                {
                    if (!string.Equals(Convert.ToString(Call(network, "GetName")), name, StringComparison.Ordinal)) continue;
                    use(network);
                    return true;
                }
            }
            catch (Exception ex) { Log.Write("Network List Manager failed: " + (ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException.Message : ex.Message)); }
            finally { if (nlm != null) try { Marshal.ReleaseComObject(nlm); } catch { } }
            return false;
        }

        static uint? NetworkListCategory(string name)
        {
            uint? result = null;
            WithNetworkListNetwork(name, n => result = Convert.ToUInt32(Call(n, "GetCategory")));
            return result;
        }

        static bool NetworkListSetCategory(string name, uint category)
        {
            bool ok = false;
            WithNetworkListNetwork(name, n =>
            {
                Call(n, "SetCategory", (int)category);
                ok = Convert.ToUInt32(Call(n, "GetCategory")) == category;
            });
            return ok;
        }

        // ---- What JellyScale changed (kept in the app folder; removed with it)

        static List<Change> LoadChanges()
        {
            try
            {
                if (!File.Exists(Paths.NetworkChanges)) return new List<Change>();
                return new JavaScriptSerializer().Deserialize<List<Change>>(File.ReadAllText(Paths.NetworkChanges)) ?? new List<Change>();
            }
            catch (Exception ex)
            {
                Log.Write("Could not read network changes: " + ex.Message);
                return new List<Change>();
            }
        }

        static void Remember(Change change)
        {
            var changes = LoadChanges();
            if (changes.Any(c => c.Name == change.Name)) return; // keep what it was before the first change
            changes.Add(change);
            File.WriteAllText(Paths.NetworkChanges, new JavaScriptSerializer().Serialize(changes), new UTF8Encoding(false));
        }
    }

    static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        public static string ProcessPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, uint reserved);

        /// <summary>PIDs listening on a TCP port (IPv4 and IPv6, since Caddy binds dual-stack).</summary>
        public static HashSet<int> ListenerPids(int port)
        {
            var pids = new HashSet<int>();
            foreach (int af in new[] { 2 /* AF_INET */, 23 /* AF_INET6 */ })
            {
                int rowSize = af == 2 ? 24 : 56, portOffset = af == 2 ? 8 : 20, pidOffset = af == 2 ? 20 : 52;
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    size += 4096;
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        uint rc = GetExtendedTcpTable(buf, ref size, false, af, 3, 0);
                        if (rc == 122 /* ERROR_INSUFFICIENT_BUFFER */) continue;
                        if (rc != 0) break;
                        int count = Marshal.ReadInt32(buf);
                        for (int i = 0; i < count; i++)
                        {
                            IntPtr row = new IntPtr(buf.ToInt64() + 4 + (long)i * rowSize);
                            int raw = Marshal.ReadInt32(row, portOffset);
                            int p = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                            if (p == port) pids.Add(Marshal.ReadInt32(row, pidOffset));
                        }
                        break;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            return pids;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(string name, uint access, uint share, ref SECURITY_ATTRIBUTES sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        /// <summary>Starts a console program with no window and its output written to a log file. The program keeps running after JellyScale exits.</summary>
        public static int StartHidden(string exe, string args, string cwd, string logPath)
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), bInheritHandle = true };
            IntPtr invalid = new IntPtr(-1);
            IntPtr log = CreateFile(logPath, 0x40000000 /* GENERIC_WRITE */, 7, ref sa, 2 /* CREATE_ALWAYS */, 0x80, IntPtr.Zero);
            if (log == invalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Can't create " + logPath);
            IntPtr nul = CreateFile("NUL", 0x80000000 /* GENERIC_READ */, 3, ref sa, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            try
            {
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                si.dwFlags = 0x100 /* STARTF_USESTDHANDLES */ | 0x1 /* STARTF_USESHOWWINDOW */;
                si.wShowWindow = 0;
                si.hStdInput = nul == invalid ? IntPtr.Zero : nul;
                si.hStdOutput = log;
                si.hStdError = log;
                PROCESS_INFORMATION pi;
                var cmd = new StringBuilder("\"" + exe + "\" " + args);
                if (!CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, true, 0x08000000 /* CREATE_NO_WINDOW */, IntPtr.Zero, cwd, ref si, out pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Can't start " + Path.GetFileName(exe));
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
            finally
            {
                CloseHandle(log);
                if (nul != invalid) CloseHandle(nul);
            }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Dark title bar on Windows 10 (1809+) and a matching caption colour on Windows 11.</summary>
        public static void StyleTitleBar(IntPtr hwnd, System.Drawing.Color caption, System.Drawing.Color text)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                int c = caption.R | (caption.G << 8) | (caption.B << 16);
                DwmSetWindowAttribute(hwnd, 35, ref c, 4);
                int t = text.R | (text.G << 8) | (text.B << 16);
                DwmSetWindowAttribute(hwnd, 36, ref t, 4);
            }
            catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        public static void Broadcast(string message)
        {
            try { PostMessage(new IntPtr(0xFFFF), RegisterWindowMessage(message), IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);

        public static void SetCueBanner(IntPtr textBox, string text)
        {
            try { SendMessage(textBox, 0x1501 /* EM_SETCUEBANNER */, new IntPtr(1), text); } catch { }
        }
    }
}
