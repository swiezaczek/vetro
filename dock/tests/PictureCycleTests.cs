using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Launcher.Core;

namespace Launcher.Tests
{
    internal static class PictureCycleTests
    {
        private static string GroupReply(params int[] activeIds)
        {
            var active = new HashSet<int>(activeIds);
            var sb = new StringBuilder("   array [\n");
            for (int i = 0; i < PictureModes.Candidates.Length; i++)
                sb.Append("      int32 ").Append(active.Contains(PictureModes.Candidates[i]) ? 1 : 0).Append('\n');
            sb.Append("   ]\n   int32 0\n");
            return sb.ToString();
        }

        private static ChoiceState SampleState(int activeId, params int[] offeredIds)
        {
            var sb = new StringBuilder();
            var offered = new HashSet<int>(offeredIds);
            foreach (int id in PictureModes.Candidates)
                sb.Append("i ").Append(offered.Contains(id) ? 1 : 0).Append('\n');
            return PictureModes.Build("ii " + activeId + " 0", sb.ToString());
        }

        public static void Run()
        {
            T.Run("commands: picnext is parsed from FIFO stream", () =>
            {
                var b = new CommandBuffer();
                var bytes = Encoding.ASCII.GetBytes("picnext\n PICNEXT \npicprev\n");
                var r = b.Feed(bytes, bytes.Length);
                T.Eq(r.Count, 2, "two picnext commands parsed, unknown dropped");
                T.Eq(r[0], Command.PicNext, "lowercase picnext");
                T.Eq(r[1], Command.PicNext, "trimmed uppercase PICNEXT");
            });

            T.Run("picture modes: CheckPictureModeByGroup format, parsing, and busctl fallback", () =>
            {
                string groupCmd = PictureModes.CheckGroupCommand();
                T.True(groupCmd.Contains("dbus-send --system --reply-timeout=3000 --print-reply=literal")
                    && groupCmd.Contains("org.tizen.tv.avoc.AvOutputControl.CheckPictureModeByGroup")
                    && groupCmd.Contains("array:int32:65536,")
                    && groupCmd.Contains(" int32:22"), "builds dbus-send CheckPictureModeByGroup command");

                string combined = PictureModes.GetAndCheckGroupCommand();
                T.True(combined.Contains("GetPictureMode i 1")
                    && combined.Contains("__GROUP__")
                    && combined.Contains("CheckPictureModeByGroup"), "combines mode read and group check");

                string parsed = PictureModes.ParseGroupReply(GroupReply(65536, 65538, 655360));
                T.True(parsed != null, "accepts 22-element reply ending in int32 0");
                ChoiceState st = PictureModes.Build("ii 65538 0", parsed);
                T.Eq(st.Rows.Count, 3, "builds 3 offered rows from group reply");
                T.True(st.Rows[1].Value == 65538 && st.Rows[1].Active, "marks Movie active");

                T.Eq(PictureModes.ParseGroupReply(null), null, "null rejected");
                T.Eq(PictureModes.ParseGroupReply(""), null, "empty rejected");
                T.Eq(PictureModes.ParseGroupReply("   array [\n      int32 1\n   ]\n   int32 -1\n"), null, "truncated -1 reply rejected");
                T.Eq(PictureModes.ParseGroupReply("   array [\n      int32 1\n   ]\n   int32 0\n"), null, "partial array count rejected");
                T.Eq(PictureModes.ParseGroupReply(GroupReply(65536).Replace("int32 0\n", "int32 1\n")), null, "non-zero trailing rc rejected");
                T.Eq(PictureModes.ParseGroupReply(GroupReply(65536).Replace("   int32 0\n", "")), null, "missing trailing int32 0 rejected");

                // ReadState uses group reply directly when valid
                int fallbackCalls = 0;
                ChoiceState fast = PictureModes.ReadState(cmd =>
                {
                    if (cmd.Contains("__GROUP__")) return "ii 65538 0\n__GROUP__\n" + GroupReply(65536, 65538, 655360);
                    fallbackCalls++;
                    return "";
                });
                T.Eq(fallbackCalls, 0, "valid group reply skips busctl fallback");
                T.Eq(fast.Rows.Count, 3, "fast read builds all rows");

                // ReadState falls back to CheckCommand when group reply is truncated
                ChoiceState fb = PictureModes.ReadState(cmd =>
                {
                    if (cmd.Contains("__GROUP__")) return "ii 655360 0\n__GROUP__\n   array [\n      int32 1\n   ]\n   int32 -1\n";
                    fallbackCalls++;
                    var sb = new StringBuilder();
                    foreach (int id in PictureModes.Candidates) sb.Append("i ").Append(id == 65538 || id == 655360 ? 1 : 0).Append('\n');
                    return sb.ToString();
                });
                T.Eq(fallbackCalls, 1, "truncated group reply triggers busctl fallback");
                T.Eq(fb.Rows.Count, 2, "fallback builds rows");
                T.True(fb.Rows[1].Value == 655360 && fb.Rows[1].Active, "fallback preserves active mode");
            });

            T.Run("picture cycle: PickNext cycles preferred modes, handles locked and unavailable", () =>
            {
                ChoiceState st = SampleState(65538, 65536, 65537, 65538, 655360); // Dynamic, Standard, Movie(active), Filmmaker Mode
                var prefs = new[] { "movie", "Filmmaker Mode", "MOVIE", "Standard", "  " };

                PictureCycleResult r1 = PictureModes.PickNext(st, prefs);
                T.Eq(r1.Kind, PictureCycleKind.Switch, "switches from Movie to Filmmaker Mode");
                T.Eq(r1.TargetId, 655360, "target id is Filmmaker Mode");
                T.Eq(r1.TargetLabel, "Filmmaker Mode", "target label");
                T.Eq(r1.ActiveIndex, 1, "index 1 in deduplicated list");
                T.Eq(r1.TotalCount, 3, "3 deduplicated matches");

                // Wrap around from last preferred mode (Standard) to first (Movie)
                ChoiceState atEnd = SampleState(65537, 65536, 65537, 65538, 655360);
                PictureCycleResult rWrap = PictureModes.PickNext(atEnd, prefs);
                T.Eq(rWrap.Kind, PictureCycleKind.Switch, "wraps to start");
                T.Eq(rWrap.TargetId, 65538, "wraps to Movie");
                T.Eq(rWrap.ActiveIndex, 0, "index 0 on wrap");

                // Active mode not in preferred list -> starts at index 0
                ChoiceState otherActive = SampleState(65536, 65536, 65537, 65538, 655360); // Dynamic active
                PictureCycleResult rStart = PictureModes.PickNext(otherActive, prefs);
                T.Eq(rStart.Kind, PictureCycleKind.Switch, "starts at index 0 when active is outside preferred list");
                T.Eq(rStart.TargetId, 65538, "picks first preferred mode");
                T.Eq(rStart.ActiveIndex, 0, "active index 0");

                // Single offered preferred mode still resolves cleanly
                PictureCycleResult rSingle = PictureModes.PickNext(st, new[] { "Filmmaker Mode", "Eco" });
                T.Eq(rSingle.Kind, PictureCycleKind.Switch, "single match still switches/confirms");
                T.Eq(rSingle.ActiveIndex, 0, "single dot index 0");
                T.Eq(rSingle.TotalCount, 1, "single dot count 1");

                // No matched modes -> Unavailable
                PictureCycleResult rNone = PictureModes.PickNext(st, new[] { "Eco", "Natural" });
                T.Eq(rNone.Kind, PictureCycleKind.Unavailable, "no matches returns Unavailable");
                T.Eq(rNone.Note, "Not available", "unavailable note");

                // Locked single-row state (e.g. Game mode)
                ChoiceState locked = PictureModes.Build("ii 65547 0", "i 1\n");
                PictureCycleResult rLocked = PictureModes.PickNext(locked, prefs);
                T.Eq(rLocked.Kind, PictureCycleKind.Locked, "Game mode returns Locked");
                T.Eq(rLocked.TargetLabel, "Game", "locked label preserved");
                T.Eq(rLocked.Note, "Locked", "locked note");

                // Null or unavailable state
                PictureCycleResult rNull = PictureModes.PickNext(null, prefs);
                T.Eq(rNull.Kind, PictureCycleKind.Unavailable, "null state is Unavailable");
                T.Eq(rNull.Note, "Not available", "null state note");
            });

            T.Run("picture cycle: ReadModesFile reads trimmed lines and falls back cleanly", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "vetro-pic-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    string missing = Path.Combine(dir, "missing.txt");
                    List<string> defMissing = PictureModes.ReadModesFile(missing);
                    T.Eq(string.Join("|", defMissing.ToArray()), "Movie|Filmmaker Mode", "missing file fallback");
                    T.Eq(string.Join("|", PictureModes.ReadModesFile(null).ToArray()), "Movie|Filmmaker Mode", "null path fallback");

                    string empty = Path.Combine(dir, "empty.txt");
                    File.WriteAllText(empty, "\n   \r\n\t\n");
                    List<string> defEmpty = PictureModes.ReadModesFile(empty);
                    T.Eq(string.Join("|", defEmpty.ToArray()), "Movie|Filmmaker Mode", "blank file fallback");

                    string custom = Path.Combine(dir, "modes.txt");
                    File.WriteAllText(custom, "  Standard \r\n\nMovie\n  Filmmaker Mode  \n");
                    List<string> got = PictureModes.ReadModesFile(custom);
                    T.Eq(string.Join("|", got.ToArray()), "Standard|Movie|Filmmaker Mode", "reads trimmed non-empty lines");
                }
                finally { Directory.Delete(dir, true); }
            });

            T.Run("picture toast layout: top-right Fum\u00e9 capsule geometry, clamps, dots, and fade", () =>
            {
                var p = PictureToastLayout.Compute(1920, 1080, 90, 180, 3);
                T.Eq(p.WinW, PictureToastLayout.MaxW + 2 * PictureToastLayout.Pad, "fixed window width");
                T.Eq(p.WinH, PictureToastLayout.Height + 2 * PictureToastLayout.Pad, "fixed window height");
                T.Eq(p.WinX, 1920 - PictureToastLayout.Margin - PictureToastLayout.MaxW - PictureToastLayout.Pad, "top-right WinX");
                T.Eq(p.WinY, PictureToastLayout.Margin - PictureToastLayout.Pad, "top-right WinY");
                T.Eq(p.WinX + p.BodyX + p.BodyW, 1920 - PictureToastLayout.Margin, "body right edge keeps 96 px safe margin");
                T.Eq(p.WinY + p.BodyY, PictureToastLayout.Margin, "body top edge keeps 96 px safe margin");
                T.Eq(p.RimX, p.BodyX - 2, "rim 2 px left");
                T.Eq(p.RimY, p.BodyY - 2, "rim 2 px above");
                T.Eq(p.RimW, p.BodyW + 4, "rim 4 px wider");
                T.Eq(p.RimH, p.BodyH + 4, "rim 4 px taller");
                T.True(p.RimX >= 0 && p.RimY >= 0 && p.RimX + p.RimW <= p.WinW && p.RimY + p.RimH <= p.WinH, "rim fits inside window");
                T.Eq(p.DotCount, 3, "3 dots");
                T.Eq(p.DotCenterX.Length, 3, "3 dot centers");
                T.Eq(p.DotCenterX[1] - p.DotCenterX[0], PictureToastLayout.DotSize + PictureToastLayout.DotGap, "dot spacing");
                T.Eq(p.DotCenterY, p.TextCenterY, "dots aligned to text center Y");
                T.True(p.LabelX == p.BodyX + PictureToastLayout.PadX && p.ValueX > p.LabelX, "label then value");

                // MinW and MaxW clamping
                var tiny = PictureToastLayout.Compute(1920, 1080, 20, 30, 0);
                T.Eq(tiny.BodyW, PictureToastLayout.MinW, "clamps up to MinW");
                T.Eq(tiny.DotCount, 0, "0 dots when dotCount is 0");

                var huge = PictureToastLayout.Compute(1920, 1080, 100, 900, 15);
                T.Eq(huge.BodyW, PictureToastLayout.MaxW, "clamps down to MaxW");
                T.Eq(huge.DotCount, PictureToastLayout.MaxDots, "clamps dotCount to MaxDots=10");
                T.True(huge.ValueX + huge.ValueMaxW <= huge.DotCenterX[0] - PictureToastLayout.DotSize / 2 - PictureToastLayout.DotsGap, "oversized value caps before dots");

                // Fade timing and --no-motion
                bool prevInstant = VolumeFade.Instant;
                try
                {
                    VolumeFade.Instant = false;
                    T.Eq(PictureToastLayout.Alpha(10.0, 10.0, 11.8), 0.0, "fade starts at 0");
                    T.True(Math.Abs(PictureToastLayout.Alpha(10.07, 10.0, 11.8) - 0.5) < 1e-6, "halfway through 0.14s fade-in");
                    T.Eq(PictureToastLayout.Alpha(10.5, 10.0, 11.8), 1.0, "full during hold");
                    T.True(PictureToastLayout.Holding(1.0, 10.5, 12.5), "holding while full before until");
                    T.True(!PictureToastLayout.Holding(1.0, 12.51, 12.5), "not holding after until");
                    T.True(Math.Abs(PictureToastLayout.Alpha(12.61, 10.0, 12.5) - 0.5) < 1e-6, "halfway through 0.22s fade-out");
                    T.True(PictureToastLayout.Alpha(12.73, 10.0, 12.5) <= 0.0, "gone after fade-out");

                    VolumeFade.Instant = true;
                    T.Eq(PictureToastLayout.Alpha(10.0, 10.0, 12.5), 1.0, "no-motion immediately full");
                    T.Eq(PictureToastLayout.Alpha(12.51, 10.0, 12.5), 0.0, "no-motion immediately gone after until");
                }
                finally { VolumeFade.Instant = prevInstant; }
            });
        }
    }
}
