using System;
using System.Collections.Generic;
using System.Threading;

namespace JellyScale
{
    enum CheckStatus { Pending, Running, Ok, Warn, Fail }

    /// <summary>Reports one row of the server check list: index, status, optional detail.</summary>
    delegate void CheckReport(int index, CheckStatus status, string detail);

    class TurnOnResult
    {
        public string LanIP;
        public bool NetworkNotPrivate;
    }

    /// <summary>Everything the window needs. Real implementation talks to Windows; demo is for screenshots/UI work.</summary>
    interface IBackend
    {
        string ServerIP { get; }
        BridgeState GetState();
        string LanIP();
        /// <summary>Runs the four checks. Returns the normalised IP when it may be saved (warnings allowed), otherwise null.</summary>
        string CheckServer(string input, CheckReport report);
        void SaveServer(string ip);
        TurnOnResult TurnOn(Action<string> progress);
        void TurnOff(Action<string> progress);
        bool StartsWithWindows();
        void SetStartWithWindows(bool on);
        /// <summary>Name of the home network (as Windows shows it), or null if it isn't clear which one it is.</summary>
        string HomeNetworkName();
        /// <summary>Sets the home network to Private and returns its name. Uninstall sets it back.</summary>
        string MakeNetworkPrivate();
    }

    static class Checks
    {
        public static readonly string[] Labels =
        {
            "Valid Tailscale address",
            "Tailscale is installed on this PC",
            "Jellyfin server answers on port 8096",
            "Home network is set to Private"
        };
    }

    class RealBackend : IBackend
    {
        string serverIP;

        public RealBackend()
        {
            serverIP = AppSettings.Load().ServerIP;
            string error;
            if (serverIP != null) serverIP = Net.NormalizeTailscaleIP(serverIP, out error);
        }

        public string ServerIP { get { return serverIP; } }

        public BridgeState GetState() { return Bridge.GetState(); }

        public string LanIP() { return Net.LanIPv4(); }

        public string CheckServer(string input, CheckReport report)
        {
            report(0, CheckStatus.Running, null);
            string error;
            var ip = Net.NormalizeTailscaleIP(input, out error);
            if (ip == null) { report(0, CheckStatus.Fail, error); return null; }
            report(0, CheckStatus.Ok, null);

            report(1, CheckStatus.Running, null);
            if (!Net.TailscaleInstalled()) { report(1, CheckStatus.Fail, "Install Tailscale on this PC and sign in, then check again."); return null; }
            report(1, CheckStatus.Ok, null);

            report(2, CheckStatus.Running, null);
            if (!Net.CanConnect(ip, AppInfo.ServerPort, 8000))
            {
                report(2, CheckStatus.Fail, "Can't reach " + ip + " on port " + AppInfo.ServerPort + ". Check the address, that Tailscale is connected and shares the server with this PC, and that Jellyfin is running.");
                return null;
            }
            report(2, CheckStatus.Ok, null);

            report(3, CheckStatus.Running, null);
            var profile = Net.HomeNetworkProfile();
            if (profile == NetworkProfile.Private) report(3, CheckStatus.Ok, null);
            else if (profile == NetworkProfile.NotPrivate) report(3, CheckStatus.Warn, "Your home network is set to Public, so the TV can't connect yet. JellyScale can make it Private for you.");
            else report(3, CheckStatus.Warn, "Couldn't confirm your home network is Private. If the TV can't connect, make it Private.");
            return ip;
        }

        public void SaveServer(string ip)
        {
            using (OperationLock.Acquire(2000))
            {
                if (Bridge.GetState() != BridgeState.Off) throw new BridgeException("Turn JellyScale OFF before changing the server.");
                new AppSettings { ServerIP = ip }.Save();
                serverIP = ip;
                Log.Write("Server set to " + ip);
            }
        }

        public TurnOnResult TurnOn(Action<string> progress)
        {
            using (OperationLock.Acquire(2000))
            {
                if (serverIP == null) throw new BridgeException("Connect your server first.");
                if (Bridge.GetState() != BridgeState.Off) throw new BridgeException("JellyScale is already on.");
                Bridge.Start(serverIP, progress);
                return new TurnOnResult
                {
                    LanIP = Net.LanIPv4(),
                    NetworkNotPrivate = Net.HomeNetworkProfile() == NetworkProfile.NotPrivate
                };
            }
        }

        public void TurnOff(Action<string> progress)
        {
            using (OperationLock.Acquire(2000))
            {
                progress("Restoring your sleep settings…");
                Bridge.Stop();
            }
        }

        public bool StartsWithWindows() { return AutoStart.IsEnabled(); }

        public void SetStartWithWindows(bool on)
        {
            if (on) AutoStart.Enable(); else AutoStart.Disable();
        }

        public string HomeNetworkName() { return HomeNetwork.Name(); }

        public string MakeNetworkPrivate() { return HomeNetwork.MakePrivate(); }
    }

    /// <summary>Fake backend used with --demo to preview every screen without touching the PC.</summary>
    class DemoBackend : IBackend
    {
        string server;
        BridgeState state = BridgeState.Off;
        bool failOn, publicNet, startsWithWindows;
        int unreachableTries;

        public DemoBackend(string mode)
        {
            if (mode != "--demo") server = "100.101.102.103";
            if (mode == "--demo-on") state = BridgeState.On;
            if (mode == "--demo-interrupted") state = BridgeState.Interrupted;
            failOn = mode == "--demo-error";
            publicNet = mode == "--demo-public" || mode == "--demo-autostart";
            // --demo-autostart: the server is unreachable for the first try, like right after sign-in.
            if (mode == "--demo-autostart") { unreachableTries = 1; startsWithWindows = true; }
        }

        public string ServerIP { get { return server; } }
        public BridgeState GetState() { return state; }
        public string LanIP() { return "192.168.1.42"; }

        public string CheckServer(string input, CheckReport report)
        {
            report(0, CheckStatus.Running, null); Thread.Sleep(250);
            string error;
            var ip = Net.NormalizeTailscaleIP(input, out error);
            if (ip == null) { report(0, CheckStatus.Fail, error); return null; }
            report(0, CheckStatus.Ok, null);
            report(1, CheckStatus.Running, null); Thread.Sleep(450); report(1, CheckStatus.Ok, null);
            report(2, CheckStatus.Running, null); Thread.Sleep(900);
            if (ip.EndsWith(".1")) { report(2, CheckStatus.Fail, "Can't reach " + ip + " on port 8096. Check the address, that Tailscale is connected and shares the server with this PC, and that Jellyfin is running."); return null; }
            report(2, CheckStatus.Ok, null);
            report(3, CheckStatus.Running, null); Thread.Sleep(350);
            if (publicNet) report(3, CheckStatus.Warn, "Your home network is set to Public, so the TV can't connect yet. JellyScale can make it Private for you.");
            else report(3, CheckStatus.Ok, null);
            return ip;
        }

        public void SaveServer(string ip) { server = ip; }

        public TurnOnResult TurnOn(Action<string> progress)
        {
            progress("Checking your Jellyfin server…"); Thread.Sleep(700);
            if (unreachableTries > 0) { unreachableTries--; throw new ServerUnreachableException("Can't reach your Jellyfin server at " + server + ":8096."); }
            if (failOn) throw new BridgeException("Can't reach your Jellyfin server at " + server + ":8096. Check that Tailscale is connected and the server is running. No settings were changed.");
            progress("Keeping this PC awake…"); Thread.Sleep(600);
            progress("Starting the bridge…"); Thread.Sleep(800);
            state = BridgeState.On;
            return new TurnOnResult { LanIP = LanIP(), NetworkNotPrivate = publicNet };
        }

        public void TurnOff(Action<string> progress)
        {
            progress("Restoring your sleep settings…"); Thread.Sleep(900);
            state = BridgeState.Off;
        }

        public bool StartsWithWindows() { Thread.Sleep(150); return startsWithWindows; }
        public void SetStartWithWindows(bool on) { Thread.Sleep(400); startsWithWindows = on; }
        public string HomeNetworkName() { Thread.Sleep(200); return "Home Wi-Fi"; }
        public string MakeNetworkPrivate() { Thread.Sleep(600); publicNet = false; return "Home Wi-Fi"; }
    }
}
