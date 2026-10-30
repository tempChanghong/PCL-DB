using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PclDownloadBridge.Presentation
{
    // PCL 5ef81e4 MyListItem.RefreshColor / CE 915ac297 MyListItem.
    // Matching state keeps the already-running clock rather than restarting it.
    internal sealed class ListHoverMotion
    {
        private readonly Border _background;
        private readonly ScaleTransform _backScale;
        private readonly ScaleTransform _wholeScale;
        private SolidColorBrush _fill;
        private ClockGroup _clock;
        private bool _hasState;
        private bool _active;
        private bool _pressed;
        private bool _sidebar;
        private Color _color;

        internal ListHoverMotion(Border background, ScaleTransform wholeScale)
        {
            _background = background;
            _backScale = (ScaleTransform)background.RenderTransform;
            _wholeScale = wholeScale;
        }

        internal ClockGroup Transition => _clock;

        internal void RefreshPalette(Color color)
        {
            _color = color;
            if (_fill is null) return;
            _fill.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
            _fill.Color = color;
        }

        internal void Update(bool active, bool pressed, bool sidebar, Color color, bool animate)
        {
            if (animate && _hasState && active == _active && pressed == _pressed && sidebar == _sidebar && color == _color) return;
            _hasState = true;
            _active = active;
            _pressed = pressed;
            _sidebar = sidebar;
            _color = color;
            var back = _backScale.ScaleX;
            var whole = _wholeScale.ScaleX;
            var opacity = _background.Opacity;
            var previous = ((SolidColorBrush)_background.Background).Color;
            Detach();
            var targetBack = active || sidebar ? 1.0 : 0.75;
            var targetWhole = pressed && !sidebar ? 0.98 : 1.0;
            var targetOpacity = active ? 1.0 : 0.0;
            if (!animate)
            {
                SetValues(targetBack, targetWhole, targetOpacity, color);
                return;
            }
            // Keep displayed bases until WPF activates the new clocks next tick.
            SetValues(back, whole, opacity, previous);
            var time = active ? 120 : 180;
            var plan = new ParallelTimeline();
            plan.Children.Add(Tween(opacity, targetOpacity, time, active ? 3 : 0));
            if (active || sidebar)
            {
                plan.Children.Add(Tween(back, targetBack, Convert.ToInt32(time * 1.6), 3));
            }
            else
            {
                // After=True waits for the preceding 540 ms whole-row return.
                var reset = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(541)) };
                reset.KeyFrames.Add(new LinearDoubleKeyFrame(back, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                reset.KeyFrames.Add(new EasingDoubleKeyFrame(0.996, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), Ease()));
                reset.KeyFrames.Add(new LinearDoubleKeyFrame(0.996, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(540))));
                reset.KeyFrames.Add(new LinearDoubleKeyFrame(0.75, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(541))));
                plan.Children.Add(reset);
            }
            plan.Children.Add(Tween(whole, targetWhole, active ? pressed ? 108 : 144 : 540, 3));
            plan.Children.Add(new ColorAnimation(previous, color, TimeSpan.FromMilliseconds(time)));
            var clock = (ClockGroup)plan.CreateClock(true);
            _clock = clock;
            clock.Completed += (sender, args) =>
            {
                if (!ReferenceEquals(_clock, clock)) return;
                Detach();
                SetValues(targetBack, targetWhole, targetOpacity, _color);
            };
            _background.ApplyAnimationClock(UIElement.OpacityProperty, (AnimationClock)clock.Children[0]);
            _backScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, (AnimationClock)clock.Children[1]);
            _backScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, (AnimationClock)clock.Children[1]);
            _wholeScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, (AnimationClock)clock.Children[2]);
            _wholeScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, (AnimationClock)clock.Children[2]);
            _fill.ApplyAnimationClock(SolidColorBrush.ColorProperty, (AnimationClock)clock.Children[3]);
        }

        private static PowerEase Ease()
        {
            return new PowerEase { Power = 3, EasingMode = EasingMode.EaseOut };
        }

        private static DoubleAnimation Tween(double origin, double target, int milliseconds, int power)
        {
            return new DoubleAnimation(origin, target, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = power == 0 ? null : Ease() };
        }

        private void SetValues(double back, double whole, double opacity, Color color)
        {
            _backScale.ScaleX = back;
            _backScale.ScaleY = back;
            _wholeScale.ScaleX = whole;
            _wholeScale.ScaleY = whole;
            _background.Opacity = opacity;
            _fill = new SolidColorBrush(color);
            _background.Background = _fill;
        }

        private void Detach()
        {
            var old = _clock;
            _clock = null;
            _background.ApplyAnimationClock(UIElement.OpacityProperty, null);
            _backScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, null);
            _backScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, null);
            _wholeScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, null);
            _wholeScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, null);
            _fill?.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
            old?.Controller.Remove();
        }
    }
}
