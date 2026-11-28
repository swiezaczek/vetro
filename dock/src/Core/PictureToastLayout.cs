using System;

namespace Launcher.Core
{
    /// <summary>
    /// Geometry and fade timing for the top-right Fumé picture mode toast. Pure arithmetic (no EFL),
    /// so it is unit-tested without a TV. The window is fixed-size at the top-right (<see cref="Margin"/>
    /// from top and right edges, plus <see cref="Pad"/> for the 2 px rim); the capsule body inside it
    /// right-aligns to fit the label, mode name, and up to <see cref="MaxDots"/> indicator dots.
    /// </summary>
    internal sealed class PictureToastLayout
    {
        public const int Margin = 96;
        public const int Pad = 4;
        public const int Height = 72;
        public const int Radius = 36;
        public const int PadX = 32;
        public const int LabelGap = 16;
        public const int DotsGap = 20;
        public const int DotSize = 10;
        public const int DotGap = 8;
        public const int MinW = 240;
        public const int MaxW = 760;
        public const int MaxDots = 10;
        public const double HoldSeconds = 2.5;
        public const double FadeInSeconds = 0.14;
        public const double FadeOutSeconds = 0.22;

        public int WinX, WinY, WinW, WinH;
        public int BodyX, BodyY, BodyW, BodyH;
        public int RimX, RimY, RimW, RimH;
        public int LabelX, ValueX, ValueMaxW, TextCenterY;
        public int DotCount, DotCenterY;
        public int[] DotCenterX;

        public static PictureToastLayout Compute(int screenW, int screenH, int labelW, int valueW, int dotCount)
        {
            int dots = Math.Max(0, Math.Min(MaxDots, dotCount));
            int lw = Math.Max(0, labelW);
            int vw = Math.Max(0, valueW);
            int dotsW = dots > 0 ? dots * DotSize + (dots - 1) * DotGap : 0;
            int gapLabel = lw > 0 && vw > 0 ? LabelGap : 0;
            int gapDots = dots > 0 ? DotsGap : 0;
            int innerW = lw + gapLabel + vw + gapDots + dotsW;
            int bodyW = Math.Max(MinW, Math.Min(MaxW, 2 * PadX + innerW));

            int winW = MaxW + 2 * Pad;
            int winH = Height + 2 * Pad;
            int winX = screenW - Margin - MaxW - Pad;
            int winY = Margin - Pad;

            int bodyX = winW - Pad - bodyW;
            int bodyY = Pad;
            int bodyH = Height;

            int labelX = bodyX + PadX;
            int valueX = labelX + (lw > 0 ? lw + (vw > 0 ? LabelGap : 0) : 0);
            int rightInner = bodyX + bodyW - PadX;
            int dotsStartX = rightInner - dotsW;
            int valueMaxW = Math.Max(1, (dots > 0 ? dotsStartX - DotsGap : rightInner) - valueX);
            int centerY = bodyY + bodyH / 2;

            var dotCenters = new int[dots];
            for (int i = 0; i < dots; i++)
                dotCenters[i] = dotsStartX + i * (DotSize + DotGap) + DotSize / 2;

            return new PictureToastLayout
            {
                WinX = winX,
                WinY = winY,
                WinW = winW,
                WinH = winH,
                BodyX = bodyX,
                BodyY = bodyY,
                BodyW = bodyW,
                BodyH = bodyH,
                RimX = bodyX - Design.ShellBorderW,
                RimY = bodyY - Design.ShellBorderW,
                RimW = bodyW + 2 * Design.ShellBorderW,
                RimH = bodyH + 2 * Design.ShellBorderW,
                LabelX = labelX,
                ValueX = valueX,
                ValueMaxW = valueMaxW,
                TextCenterY = centerY,
                DotCount = dots,
                DotCenterY = centerY,
                DotCenterX = dotCenters
            };
        }

        public static double Alpha(double now, double shownAt, double until)
        {
            if (VolumeFade.Instant) return now > until ? 0 : 1;
            double a = Math.Min(1.0, (now - shownAt) / FadeInSeconds);
            if (now > until) a = Math.Min(a, 1.0 - (now - until) / FadeOutSeconds);
            return a;
        }

        public static bool Holding(double alpha, double now, double until)
        {
            return alpha >= 1.0 && now <= until;
        }
    }
}
