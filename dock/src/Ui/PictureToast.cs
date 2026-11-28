using System;
using System.IO;
using Launcher.Core;
using Launcher.Native;

namespace Launcher.Ui
{
    /// <summary>
    /// Top-right Fumé capsule toast shown when cycling Picture modes: muted "Picture", bold mode name,
    /// and indicator dots for the active position in the user's preferred cycle list. Built lazily on first use,
    /// skips focus, and passes all input events through so the running app stays undisturbed.
    /// </summary>
    internal sealed class PictureToast
    {
        private const string Font = "SamsungOneUI 500";
        private const string FontBold = "SamsungOneUI 600";
        private const int FontSize = 28;
        private const int NotificationLevelTop = 40, WindowLayer = 900;

        private readonly string _assets;
        private readonly Action<string> _log;
        private readonly int _screenW, _screenH;
        private readonly Efl.TaskCb _tick, _holdEnd;

        private IntPtr _win, _rim, _body, _label, _value;
        private readonly IntPtr[] _dots = new IntPtr[PictureToastLayout.MaxDots];
        private IntPtr _animator, _holdTimer;
        private bool _built, _failed, _shown;
        private double _shownAt, _until;
        private int _winX, _winY, _edgeOffset;
        private int _dotCount, _activeDot = -1;
        private int _probeStarted;
        private volatile int _probedOffset;

        public PictureToast(string assets, Action<string> log, int screenW, int screenH)
        {
            _assets = assets;
            _log = log;
            _screenW = screenW;
            _screenH = screenH;
            _tick = Tick;
            _holdEnd = OnHoldEnd;
        }

        public bool IsOnScreen { get { return _shown; } }

        public void Show(string label, string value, int activeDot, int totalDots)
        {
            if (!Build()) return;
            int offset = _probedOffset;
            if (offset != _edgeOffset)
            {
                _edgeOffset = offset;
                Efl.evas_object_move(_win, _winX + offset, _winY);
            }
            Update(label ?? "Picture", value ?? "", activeDot, totalDots);
            double now = Efl.ecore_time_get();
            _until = now + PictureToastLayout.HoldSeconds;
            if (!_shown)
            {
                _shown = true;
                _shownAt = now;
                Apply(VolumeFade.Instant ? 1.0 : 0.0);
                Efl.evas_object_show(_win);
            }
            else if (now - _shownAt < PictureToastLayout.FadeInSeconds + 0.01) { /* still fading in */ }
            else _shownAt = now - PictureToastLayout.FadeInSeconds;
            Efl.elm_win_raise(_win);
            CancelHold();
            if (_animator == IntPtr.Zero) _animator = Efl.ecore_animator_add(_tick, IntPtr.Zero);
        }

        private void StartScreenProbe()
        {
            if (System.Threading.Interlocked.Exchange(ref _probeStarted, 1) != 0) return;
            var t = new System.Threading.Thread(() =>
            {
                _probedOffset = VolumeScreenProbe.HorizontalOffset(VolumeEdge.Right, _screenW, _log);
            }) { IsBackground = true, Name = "picture-screen" };
            t.Start();
        }

        private void CancelHold()
        {
            if (_holdTimer != IntPtr.Zero) { Efl.ecore_timer_del(_holdTimer); _holdTimer = IntPtr.Zero; }
        }

        private byte OnHoldEnd(IntPtr data)
        {
            _holdTimer = IntPtr.Zero;
            try { if (_shown && _animator == IntPtr.Zero) _animator = Efl.ecore_animator_add(_tick, IntPtr.Zero); }
            catch (Exception ex) { _log("picture toast hold: " + ex.Message); }
            return 0;
        }

        private bool Build()
        {
            if (_built) return true;
            if (_failed) return false;
            try
            {
                var initLayout = PictureToastLayout.Compute(_screenW, _screenH, 80, 120, 0);
                _winX = initLayout.WinX;
                _winY = initLayout.WinY;

                _win = Efl.elm_win_add(IntPtr.Zero, "vetro-picture-toast", Efl.ElmWinNotification);
                if (_win == IntPtr.Zero) { _failed = true; _log("picture toast: elm_win_add failed"); return false; }
                Efl.elm_win_title_set(_win, "vetro-picture-toast");
                Efl.elm_win_alpha_set(_win, true);
                Efl.elm_win_borderless_set(_win, true);
                Efl.elm_win_prop_focus_skip_set(_win, true);
                Efl.efl_util_set_notification_window_level(_win, NotificationLevelTop);
                Efl.elm_win_layer_set(_win, WindowLayer);
                Efl.elm_win_aux_hint_add(_win, "wm.policy.win.user.geometry", "1");
                Efl.elm_win_aux_hint_add(_win, "wm.comp.win.effect.enable", "0");

                StartScreenProbe();
                _edgeOffset = _probedOffset;
                Efl.evas_object_move(_win, _winX + _edgeOffset, _winY);
                Efl.evas_object_resize(_win, initLayout.WinW, initLayout.WinH);
                Efl.evas_object_pass_events_set(_win, true);

                IntPtr evas = Efl.evas_object_evas_get(_win);
                _rim = Slice(evas, "pill_rim", Design.RimSlice, (PictureToastLayout.Radius + Design.ShellBorderW) / (double)Design.RimSlice);
                _body = Slice(evas, "pill_bg", Design.SheetSlice, PictureToastLayout.Radius / (double)Design.SheetSlice);

                _label = Efl.evas_object_text_add(evas);
                Efl.evas_object_text_font_set(_label, Font, FontSize);
                Efl.evas_object_pass_events_set(_label, true);
                Efl.evas_object_show(_label);

                _value = Efl.evas_object_text_add(evas);
                Efl.evas_object_text_font_set(_value, FontBold, FontSize);
                Efl.evas_object_pass_events_set(_value, true);
                Efl.evas_object_show(_value);

                for (int i = 0; i < _dots.Length; i++)
                {
                    IntPtr dot = Efl.evas_object_image_add(evas);
                    Efl.evas_object_image_file_set(dot, Path.Combine(_assets, "pill_cap.png"), null);
                    if (Efl.evas_object_image_load_error_get(dot) != Efl.EvasLoadErrorNone) _log("picture toast: asset missing pill_cap");
                    Efl.evas_object_image_filled_set(dot, true);
                    Efl.evas_object_image_smooth_scale_set(dot, true);
                    Efl.evas_object_resize(dot, PictureToastLayout.DotSize, PictureToastLayout.DotSize);
                    Efl.evas_object_pass_events_set(dot, true);
                    _dots[i] = dot;
                }

                _built = true;
                _log("picture toast: ready");
                return true;
            }
            catch (Exception ex) { _failed = true; _log("picture toast: " + ex.Message); return false; }
        }

        private IntPtr Slice(IntPtr evas, string name, int border, double scale)
        {
            IntPtr img = Efl.evas_object_image_add(evas);
            Efl.evas_object_image_file_set(img, Path.Combine(_assets, name + ".png"), null);
            if (Efl.evas_object_image_load_error_get(img) != Efl.EvasLoadErrorNone) _log("picture toast: asset missing " + name);
            Efl.evas_object_image_filled_set(img, true);
            Efl.evas_object_image_border_set(img, border, border, border, border);
            if (scale != 1.0) Efl.evas_object_image_border_scale_set(img, scale);
            Efl.evas_object_image_border_center_fill_set(img, 1);
            Efl.evas_object_image_smooth_scale_set(img, true);
            Efl.evas_object_pass_events_set(img, true);
            Efl.evas_object_show(img);
            return img;
        }

        private void Update(string label, string value, int activeDot, int totalDots)
        {
            Efl.evas_object_text_text_set(_label, label);
            int labelW = Efl.evas_object_text_horiz_advance_get(_label);
            int labelH = Efl.evas_object_text_vert_advance_get(_label);

            Efl.evas_object_text_ellipsis_set(_value, -1.0);
            Efl.evas_object_text_text_set(_value, value);
            int valueW = Efl.evas_object_text_horiz_advance_get(_value);
            int valueH = Efl.evas_object_text_vert_advance_get(_value);

            var p = PictureToastLayout.Compute(_screenW, _screenH, labelW, valueW, totalDots);
            _dotCount = p.DotCount;
            _activeDot = activeDot >= 0 && activeDot < _dotCount ? activeDot : -1;

            Efl.evas_object_move(_rim, p.RimX, p.RimY);
            Efl.evas_object_resize(_rim, p.RimW, p.RimH);
            Efl.evas_object_move(_body, p.BodyX, p.BodyY);
            Efl.evas_object_resize(_body, p.BodyW, p.BodyH);

            int valueTop = p.TextCenterY - valueH / 2;
            int labelAsc = (int)Math.Round(labelH * 0.775);
            int valueAsc = (int)Math.Round(valueH * 0.775);
            int labelTop = valueTop + valueAsc - labelAsc;

            Efl.evas_object_move(_label, p.LabelX, labelTop);
            if (valueW > p.ValueMaxW)
            {
                Efl.evas_object_text_ellipsis_set(_value, 1.0);
                Efl.evas_object_resize(_value, p.ValueMaxW, valueH);
            }
            else
            {
                Efl.evas_object_resize(_value, valueW + 4, valueH);
            }
            Efl.evas_object_move(_value, p.ValueX, valueTop);

            int halfDot = PictureToastLayout.DotSize / 2;
            for (int i = 0; i < _dotCount; i++)
                Efl.evas_object_move(_dots[i], p.DotCenterX[i] - halfDot, p.DotCenterY - halfDot);
        }

        private void Apply(double a)
        {
            Tint(_rim, 0xFFFFFF, Design.ShellBorderAlpha * a);
            Tint(_body, Design.ShellColor, Design.VolumeAlpha * a);
            Tint(_label, 0xFFFFFF, 0.70 * a);
            Tint(_value, 0xFFFFFF, 0.96 * a);
            for (int i = 0; i < _dots.Length; i++)
            {
                if (i < _dotCount)
                {
                    if (i == _activeDot) Tint(_dots[i], Design.LightColor, a);
                    else Tint(_dots[i], 0xFFFFFF, 0.28 * a);
                    Efl.evas_object_show(_dots[i]);
                }
                else Efl.evas_object_hide(_dots[i]);
            }
        }

        private static void Tint(IntPtr obj, int rgb, double alpha)
        {
            int a = (int)Math.Round(Math.Max(0, Math.Min(1, alpha)) * 255);
            Efl.evas_object_color_set(obj, ((rgb >> 16) & 255) * a / 255, ((rgb >> 8) & 255) * a / 255, (rgb & 255) * a / 255, a);
        }

        private byte Tick(IntPtr data)
        {
            try
            {
                double now = Efl.ecore_time_get();
                double a = PictureToastLayout.Alpha(now, _shownAt, _until);
                if (a <= 0)
                {
                    Efl.evas_object_hide(_win);
                    _shown = false;
                    _animator = IntPtr.Zero;
                    CancelHold();
                    return 0;
                }
                Apply(a);
                if (!PictureToastLayout.Holding(a, now, _until)) return 1;
                _animator = IntPtr.Zero;
                CancelHold();
                _holdTimer = Efl.ecore_timer_add(Math.Max(0.0, _until - now), _holdEnd, IntPtr.Zero);
                return 0;
            }
            catch (Exception ex)
            {
                _log("picture toast tick: " + ex.Message);
                _animator = IntPtr.Zero;
                _shown = false;
                CancelHold();
                if (_win != IntPtr.Zero) Efl.evas_object_hide(_win);
                return 0;
            }
        }
    }
}
