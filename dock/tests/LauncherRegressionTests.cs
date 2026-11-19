using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Launcher.Core;

namespace Launcher.Tests
{
    internal static class LauncherRegressionTests
    {
        private static string Q(string s) { return "'" + s.Replace("'", "'\"'\"'") + "'"; }
        private static void Shell(string command)
        {
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(command);
            using (var process = Process.Start(start))
            {
                if (!process.WaitForExit(6000)) { process.Kill(true); throw new Exception("shell test timed out"); }
                T.Eq(process.ExitCode, 0, "shell completed");
            }
        }

        public static void Run()
        {
            T.Run("raw navigation: immediate presses, delayed echoes, aliases, stale counts and hidden dock", () =>
            {
                var nav = new NavigationInput();
                T.Eq(nav.Press(106, false, 0), null, "hidden dock ignores raw navigation");
                T.True(!nav.IsEcho("Right", 0.14), "hidden press does not swallow a key later");
                T.Eq(nav.Press(106, true, 1), "Right", "raw press is delivered now");
                T.Eq(nav.Press(106, true, 1.05), "Right", "a second real press is not deduplicated");
                T.True(nav.IsEcho("Right", 1.14), "first echo discarded");
                T.True(nav.IsEcho("Right", 1.19), "second echo discarded");
                T.True(!nav.IsEcho("Right", 1.2), "next compositor-only press remains valid");
                nav.Press(28, true, 2); T.True(nav.IsEcho("Select", 2.14), "OK alias");
                nav.Press(1, true, 3); T.True(nav.IsEcho("Escape", 3.14), "Back alias");
                nav.Press(105, true, 4); T.True(!nav.IsEcho("Left", 5.1), "late key is a fresh press");
                nav.Press(105, true, 6); nav.Press(105, true, 7.1);
                T.True(nav.IsEcho("Left", 7.2), "current echo discarded");
                T.True(!nav.IsEcho("Left", 7.3), "expired unmatched counts cannot swallow new presses");
                T.Eq(nav.Press(63, true, 8), null, "Home has its own debounce");
            });

            T.Run("reduce motion: instant visibility, pages, focus and stable icon geometry", () =>
            {
                bool instant = Controller.Instant, grow = DockLayout.Grow, glow = DockLayout.GlowFromIcon;
                try
                {
                    Controller.Instant = true; DockLayout.Grow = false; DockLayout.GlowFromIcon = false;
                    var layout = new DockLayout(20, 1920, 1080);
                    var ctl = new Controller(layout); ctl.Show(); int index;
                    T.Eq(ctl.Phase, Phase.Shown, "show has no fade or cascade"); T.Eq(ctl.Presence, 1f, "fully opaque immediately");
                    for (int i = 0; i < ctl.Count; i++) { T.Eq(ctl.ItemPresence(i), 1f, "no stagger"); T.Eq(ctl.ItemSlide(i), 0, "no entrance slide"); }
                    T.True(!ctl.Tick(0), "no animation frames needed while shown");
                    var panel = new Controller(layout, PageKind.Panel); panel.AdoptVisibility(ctl, .55f);
                    T.Eq(panel.Phase, Phase.Shown, "panel is immediate"); T.Eq(panel.Presence, 1f, "no page pop");
                    ctl.AdoptVisibility(panel, .55f);
                    ctl.Hide(); T.Eq(ctl.Phase, Phase.Hidden, "no hide transition"); T.Eq(ctl.Presence, 0f, "hidden immediately");
                    T.Eq(ctl.Slide, 0, "no exit slide"); T.True(!ctl.Tick(0), "no frames needed after hiding"); ctl.Show();
                    ctl.Key("Right", out index);
                    T.Eq(ctl.FocusPos, 1f, "focus changes without waiting for a frame");
                    var selected = layout.IconBox(1, 1, ctl.Scroll, 0);
                    var unselected = layout.IconBox(1, 0, ctl.Scroll, 0);
                    T.Eq(selected.W, unselected.W, "no zoom"); T.Eq(selected.Y, unselected.Y, "no lift");
                    for (int i = 0; i < 15; i++) ctl.Key("Right", out index);
                    T.Eq(ctl.Scroll, layout.ScrollFor(ctl.FocusTarget), "scroll follows the instant focus");
                    Controller.Instant = false;
                    ctl.Key("Left", out index);
                    T.True(ctl.FocusPos != ctl.FocusTarget, "normal motion remains available");
                }
                finally { Controller.Instant = instant; DockLayout.Grow = grow; DockLayout.GlowFromIcon = glow; }
            });

            T.Run("multi-app Home guard: every target guarded, cancellation stops the entire chain", () =>
            {
                string[] ids = HomeGuard.Parse("com.samsung.tv.csfs,com.samsung.tv.home.sidebar,com.samsung.tv.home.media");
                T.Eq(ids.Length, 3, "three valid targets");
                T.Eq(HomeGuard.Parse("com.samsung.tv.csfs;touch /tmp/x"), null, "shell metacharacters refused");
                T.Eq(HomeGuard.Parse("com.samsung.tv.csfs,,other"), null, "empty target refused");
                foreach (string id in ids) T.True(HomeGuard.ShouldCancel(ids, id), "target cancels guard");
                T.True(HomeGuard.ShouldCancel(ids, "com.samsung.tv.coba.newhome"), "CoBA launcher cancels guard");
                T.True(!HomeGuard.ShouldCancel(ids, "org.tizen.browser"), "unrelated app does not cancel guard");
                string dir = Path.Combine(Path.GetTempPath(), "vetro-guard-'" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string gen = Path.Combine(dir, "generation"), log = Path.Combine(dir, "calls"), tool = Path.Combine(dir, "launcher");
                    File.WriteAllText(tool, "#!/bin/sh\necho \"$2\" >> " + Q(log) + "\n");
                    Shell("chmod +x " + Q(tool)); File.WriteAllText(gen, "1");
                    string command = HomeGuard.Command(ids, gen, 1).Replace("/usr/bin/app_launcher", Q(tool));
                    Shell(command + " wait");
                    string[] calls = File.ReadAllLines(log); T.Eq(calls.Length, 15, "all three targets in all five rounds");
                    for (int i = 0; i < calls.Length; i++) T.Eq(calls[i], ids[i % 3], "target order");
                    File.Delete(log); File.WriteAllText(gen, "2"); Shell(command + " wait");
                    T.True(!File.Exists(log), "retired generation issues no kills");
                    File.WriteAllText(gen, "1");
                    File.WriteAllText(tool, "#!/bin/sh\necho \"$2\" >> " + Q(log) + "\necho 2 > " + Q(gen) + "\n");
                    Shell(command + " wait");
                    T.Eq(File.ReadAllLines(log).Length, 1, "cancellation between targets stops remaining targets too");
                }
                finally { Directory.Delete(dir, true); }
            });

            T.Run("custom launcher mode: only a plain app id becomes the Home target", () =>
            {
                AppEntry e = HomeLaunch.Parse("org.example.projectivy");
                T.True(e != null, "plain id accepted");
                T.Eq(e.AppId, "org.example.projectivy", "launches the chosen app");
                T.True(!e.IsSystem, "an ordinary app, not a system control");
                T.Eq(HomeLaunch.Parse("app;reboot"), null, "shell metacharacters refused");
                T.Eq(HomeLaunch.Parse(""), null, "empty id refused");
                T.Eq(HomeLaunch.Parse(null), null, "missing id refused");
            });

            T.Run("CoBA system launch: fallback follows failed launcher, never a successful one", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "vetro-launch-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string tool = Path.Combine(dir, "launcher"), log = Path.Combine(dir, "calls");
                    string command = SystemLaunch.Command("com.samsung.tv.coba.newhome", "com.samsung.tv.csfs").Replace("/usr/bin/app_launcher", Q(tool));
                    File.WriteAllText(tool, "#!/bin/sh\necho \"$2\" >> " + Q(log) + "\n[ \"$2\" != com.samsung.tv.coba.newhome ]\n");
                    Shell("chmod +x " + Q(tool)); Shell(command + " wait");
                    T.Eq(string.Join(",", File.ReadAllLines(log)), "com.samsung.tv.coba.newhome,com.samsung.tv.csfs", "failed newhome tries csfs");
                    File.Delete(log); File.WriteAllText(tool, "#!/bin/sh\necho \"$2\" >> " + Q(log) + "\nexit 0\n");
                    Shell(command + " wait"); T.Eq(File.ReadAllLines(log).Length, 1, "success does not start two Homes");
                }
                finally { Directory.Delete(dir, true); }
            });

            T.Run("home key map and evdev filter: exclusive Home interception and clean release", () =>
            {
                var codes = HomeKeyMap.Parse("# xkb\nXF86Back 1\nXF86Home 63\nXF86Home 172\n", 63);
                T.Eq(codes.Count, 2, "XF86Home codes deduplicated");
                T.True(codes.Contains(63) && codes.Contains(172) && !codes.Contains(1), "only Home codes collected");
                T.True(HomeKeyMap.Parse(null, 63).Contains(63), "extraCode kept without layout file");

                var filter = new EvdevFilter(codes, 16);
                byte[] ev = new byte[16 * 6];
                Action<int, int, int, int> put = (slot, type, code, val) =>
                {
                    int o = slot * 16 + 8;
                    ev[o] = (byte)type; ev[o + 1] = (byte)(type >> 8);
                    ev[o + 2] = (byte)code; ev[o + 3] = (byte)(code >> 8);
                    ev[o + 4] = (byte)val; ev[o + 5] = (byte)(val >> 8); ev[o + 6] = (byte)(val >> 16); ev[o + 7] = (byte)(val >> 24);
                };
                put(0, 1, 63, 1);    // Home down -> HomePress (swallowed)
                put(1, 1, 63, 2);    // Home repeat -> Suppress
                put(2, 1, 63, 0);    // Home up -> Suppress
                put(3, 1, 106, 1);   // Right down -> ForwardAndNotify
                put(4, 0, 0, 0);     // EV_SYN -> Forward
                put(5, 1, 105, 1);   // Left down (left held) -> ForwardAndNotify
                int off = 0;
                EvdevEventAction act; ushort keyCode;
                T.True(filter.Next(ev, ev.Length, ref off, out act, out keyCode) && act == EvdevEventAction.HomePress && keyCode == 63, "Home press triggers dock");
                T.True(filter.Next(ev, ev.Length, ref off, out act, out keyCode) && act == EvdevEventAction.Suppress, "Home repeat suppressed");
                T.True(filter.Next(ev, ev.Length, ref off, out act, out keyCode) && act == EvdevEventAction.Suppress, "Home release suppressed");
                T.True(filter.Next(ev, ev.Length, ref off, out act, out keyCode) && act == EvdevEventAction.ForwardAndNotify && keyCode == 106, "Right forwarded and notified");
                T.True(filter.Next(ev, ev.Length, ref off, out act, out keyCode) && act == EvdevEventAction.Forward, "sync forwarded");
                T.True(filter.Next(ev, ev.Length, ref off, out act, out keyCode) && act == EvdevEventAction.ForwardAndNotify && keyCode == 105, "Left forwarded and tracked");
                T.True(!filter.Next(ev, ev.Length, ref off, out act, out keyCode), "stream exhausted");
                ushort[] held = filter.DrainPressedForwarded();
                T.Eq(held.Length, 2, "two forwarded keys still pressed, Home not among them");
                T.Eq(filter.DrainPressedForwarded().Length, 0, "drain clears pressed set");
            });

            T.Run("device capability bitmasks and forwarded Phys filtering", () =>
            {
                T.True(InputDevices.HasAnyKeyCapability(null), "missing sysfs tolerated");
                T.True(!InputDevices.HasAnyKeyCapability("0 0"), "all-zero dummy subdevice rejected");
                T.True(InputDevices.HasAnyKeyCapability("1 0"), "non-zero mask accepted");
                T.True(InputDevices.HasKeyBit("80000000 0", 63, 32), "32-bit kernel bit 63");
                T.True(!InputDevices.HasKeyBit("80000000 0", 62, 32), "32-bit kernel bit 62 unset");
                T.True(InputDevices.HasKeyBit("8000000000000000", 63, 64), "64-bit kernel bit 63");

                string text =
                    "I: Bus=0005 Vendor=04e8 Product=1234 Version=0100\nN: Name=\"Smart Control\"\nP: Phys=\nH: Handlers=kbd event4\n\n" +
                    "I: Bus=0006 Vendor=04e8 Product=1234 Version=0100\nN: Name=\"Smart Control\"\nP: Phys=vetro-dock-forwarded\nH: Handlers=kbd event21\n\n" +
                    "I: Bus=0006 Vendor=04e8 Product=1234 Version=0100\nN: Name=\"Smart Control\"\nP: Phys=vetro-forwarded-remote\nH: Handlers=kbd event20\n";
                var devs = InputDevices.RemoteDevices(text);
                T.Eq(devs.Count, 2, "dock's own uinput skipped, remapper uinput kept");
                T.Eq(devs[0].Node, "/dev/input/event4", "physical remote");
                T.True(!devs[0].IsVetroForwarded && devs[0].Bus == 5 && devs[0].Vendor == 0x04e8, "identity parsed");
                T.Eq(devs[1].Node, "/dev/input/event20", "remapper forwarded device");
                T.True(devs[1].IsVetroForwarded, "marked as remapper forwarded");
            });

            T.Run("foreground policy: kills unexpected Home, respects user Samsung Home choice", () =>
            {
                string[] ids = HomeGuard.Parse("com.samsung.tv.csfs,com.samsung.tv.home.sidebar,com.samsung.tv.home.media,com.samsung.tv.coba.newhome,com.samsung.tv.coba.home");
                T.True(HomeGuard.ShouldCancel(ids, "com.samsung.tv.coba.home"), "coba.home recognised");
                var p = new HomeForegroundPolicy();
                T.Eq(p.Evaluate(ids, "org.tizen.netflix-app", "", false, 10.0, -100.0, 1.5), ForegroundAction.None, "normal app ignored");
                T.Eq(p.Evaluate(ids, "com.samsung.tv.csfs", "", false, 10.0, -100.0, 1.5), ForegroundAction.KillAndShow, "unexpected fullscreen Home kills and shows dock");
                T.Eq(p.Evaluate(ids, "com.samsung.tv.coba.newhome", "", false, 10.5, 10.0, 1.5), ForegroundAction.KillOnly, "Home during hide echo kills without reopening");
                T.Eq(p.Evaluate(ids, "com.samsung.tv.csfs", "", true, 10.5, 10.0, 1.5), ForegroundAction.KillAndRaise, "Home over active dock kills and raises dock");
                T.Eq(p.Evaluate(ids, "org.tizen.tv-viewer", "com.samsung.tv.home.sidebar", false, 12.0, 5.0, 1.5), ForegroundAction.KillOnly, "partial sidebar killed without forcing dock open");

                p.AllowHome(20.0);
                T.Eq(p.Evaluate(ids, "org.tizen.netflix-app", "", false, 20.1, 19.8, 1.5), ForegroundAction.None, "stale previous app does not cancel intentional Home");
                T.Eq(p.Evaluate(ids, "com.samsung.tv.csfs", "", false, 20.3, 19.8, 1.5), ForegroundAction.None, "intentional Samsung Home stays open");
                T.Eq(p.Evaluate(ids, "org.tizen.netflix-app", "", false, 25.0, 19.8, 1.5), ForegroundAction.None, "opening another app re-arms guard");
                T.True(!p.Allowed, "guard re-armed after leaving Samsung Home");
                T.Eq(p.Evaluate(ids, "com.samsung.tv.csfs", "", false, 30.0, 19.8, 1.5), ForegroundAction.KillAndShow, "later exit back to Samsung Home is intercepted");

                p.AllowHome(40.0);
                p.OnHomePressed();
                T.True(!p.Allowed, "pressing Home re-arms guard");
            });

            T.Run("commands: grab and ungrab", () =>
            {
                var buf = new CommandBuffer();
                var cmds = buf.Feed(Encoding.ASCII.GetBytes("ungrab\ngrab\n"), 12);
                T.Eq(cmds.Count, 2, "two commands");
                T.Eq(cmds[0], Command.Ungrab, "ungrab");
                T.Eq(cmds[1], Command.Grab, "grab");
            });
        }
    }
}
