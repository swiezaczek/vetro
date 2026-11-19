using System;
using System.Globalization;
using System.Text;

namespace Launcher.Core
{
    // The guard may close several Home windows, but choosing Home must retire every pending kill.
    internal static class HomeGuard
    {
        public static string[] Parse(string value)
        {
            if (value == null) return null;
            string[] ids = value.Split(',');
            return ids.Length > 0 && Array.TrueForAll(ids, Catalog.ValidAppId) ? ids : null;
        }

        public static bool ShouldCancel(string[] ids, string appId)
        {
            return ids != null && appId != null &&
                (Array.IndexOf(ids, appId) >= 0 || appId == "com.samsung.tv.coba.newhome" || appId == "com.samsung.tv.coba.home");
        }

        public static string Command(string[] ids, string file, int generation)
        {
            return BuildCommand(ids, file, generation, false);
        }

        /// <summary>Runs an immediate kill round at t=0 before the timed follow-up rounds (used when Home is already on screen).</summary>
        public static string ImmediateCommand(string[] ids, string file, int generation)
        {
            return BuildCommand(ids, file, generation, true);
        }

        private static string BuildCommand(string[] ids, string file, int generation, bool immediate)
        {
            if (ids == null || ids.Length == 0 || !Array.TrueForAll(ids, Catalog.ValidAppId))
                throw new ArgumentException("Invalid Home app ids");
            string quotedFile = "'" + file.Replace("'", "'\"'\"'") + "'";
            string gen = generation.ToString(CultureInfo.InvariantCulture);
            var command = new StringBuilder("(");
            if (immediate) AppendKills(command, ids, quotedFile, gen);
            foreach (string gap in new[] { "0.15", "0.3", "0.45", "0.7", "1.2" })
            {
                command.Append(" sleep ").Append(gap).Append(';');
                AppendKills(command, ids, quotedFile, gen);
            }
            return command.Append(" ) >/dev/null 2>&1 &").ToString();
        }

        private static void AppendKills(StringBuilder command, string[] ids, string quotedFile, string gen)
        {
            foreach (string id in ids)
                command.Append(" [ \"$(cat ").Append(quotedFile).Append(")\" = ").Append(gen)
                    .Append(" ] || exit 0; /usr/bin/app_launcher -k ").Append(id).Append(';');
        }
    }

    internal enum ForegroundAction { None, KillOnly, KillAndRaise, KillAndShow }

    /// <summary>
    /// Decides how to react when Tizen reports a foreground/partial app change, closing unexpected
    /// Samsung Home windows (app exit, wake from standby, SmartThings Home) while leaving intentional
    /// Samsung Home launches undisturbed until the user opens another app or presses Home again.
    /// </summary>
    internal sealed class HomeForegroundPolicy
    {
        private bool _allowed;
        private bool _sawAllowedHome;
        private double _allowedAt;

        public bool Allowed { get { return _allowed; } }

        public void AllowHome(double now)
        {
            _allowed = true;
            _sawAllowedHome = false;
            _allowedAt = now;
        }

        public void OnHomePressed()
        {
            _allowed = false;
            _sawAllowedHome = false;
        }

        public ForegroundAction Evaluate(string[] guardIds, string fullscreenAppId, string partialAppId, bool dockActive, double now, double lastHomeTime, double echoSeconds)
        {
            if (guardIds == null || guardIds.Length == 0) return ForegroundAction.None;
            bool fsHome = HomeGuard.ShouldCancel(guardIds, fullscreenAppId);
            bool partialHome = HomeGuard.ShouldCancel(guardIds, partialAppId);

            if (_allowed)
            {
                if (fsHome || partialHome)
                {
                    _sawAllowedHome = true;
                    return ForegroundAction.None;
                }
                if ((_sawAllowedHome && !string.IsNullOrEmpty(fullscreenAppId)) ||
                    (!_sawAllowedHome && now - _allowedAt > 10.0 && !string.IsNullOrEmpty(fullscreenAppId)))
                {
                    _allowed = false;
                    _sawAllowedHome = false;
                }
                return ForegroundAction.None;
            }

            if (fsHome)
            {
                if (dockActive) return ForegroundAction.KillAndRaise;
                return (now - lastHomeTime >= echoSeconds) ? ForegroundAction.KillAndShow : ForegroundAction.KillOnly;
            }
            if (partialHome)
            {
                return dockActive ? ForegroundAction.KillAndRaise : ForegroundAction.KillOnly;
            }
            return ForegroundAction.None;
        }
    }

    // --home-launch APPID (custom launcher mode): the Home key opens this app and the dock is never drawn.
    internal static class HomeLaunch
    {
        // The entry the Home key launches, or null when the id is not plain id characters (it goes on a command line).
        public static AppEntry Parse(string appId)
        {
            return Catalog.ValidAppId(appId) ? new AppEntry("custom", appId, appId, null) : null;
        }
    }
}
