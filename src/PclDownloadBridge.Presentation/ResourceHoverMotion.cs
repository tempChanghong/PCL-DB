using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PclDownloadBridge.Presentation
{
    // PCL 5ef81e4 MyResourceItem / CE 915ac297 MyCompItem:
    // animate the backing plate only, retaining its distinct scale sequence.
    internal sealed class ResourceHoverMotion
    {
        private readonly Border _back;
        private readonly ScaleTransform _scale;
        private readonly SolidColorBrush _fill;
        private readonly Func<Color, Color, int, AnimationTimeline> _colorAnimation;
        private ClockGroup _clock;
        private bool _hasState;
        private bool _active;
        private bool _pressed;
        private Color _color;

        internal ResourceHoverMotion(Border back, Func<Color, Color, int, AnimationTimeline> colorAnimation)
        {
            _back = back;
            _scale = (ScaleTransform)back.RenderTransform;
            _fill = new SolidColorBrush(((SolidColorBrush)back.Background).Color);
            back.Background = _fill;
            _colorAnimation = colorAnimation;
        }

        internal ClockGroup Transition => _clock;

        internal void RefreshPalette(Color color)
        {
            _color = color;
            _fill.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
            _fill.Color = color;
        }

        internal void Update(bool active, bool pressed, Color color, bool animate, bool pauseForChecks)
        {
            if (animate && _hasState && active == _active && pressed == _pressed && color == _color) return;
            _hasState = true;
            _active = active;
            _pressed = pressed;
            _color = color;
            var back = _scale.ScaleX;
            var opacity = _back.Opacity;
            var previous = _fill.Color;
            Detach();
            var targetScale = active ? pressed ? 0.996 : 1.0 : 0.8;
            var targetOpacity = active ? 1.0 : 0.0;
            if (!animate)
            {
                SetValues(targetScale, targetOpacity, color);
                return;
            }
            SetValues(back, opacity, previous);
            var time = active ? 120 : 180;
            var plan = new ParallelTimeline();
            plan.Children.Add(new DoubleAnimation(opacity, targetOpacity, TimeSpan.FromMilliseconds(time)) { EasingFunction = active ? Ease() : null });
            if (active)
            {
                plan.Children.Add(new DoubleAnimation(back, targetScale, TimeSpan.FromMilliseconds(144)) { EasingFunction = Ease() });
            }
            else
            {
                // After=True resets by -0.196 in one ms after the 180 ms fade.
                var reset = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(181) };
                reset.KeyFrames.Add(new LinearDoubleKeyFrame(back, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                reset.KeyFrames.Add(new EasingDoubleKeyFrame(0.996, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), Ease()));
                reset.KeyFrames.Add(new LinearDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(181))));
                plan.Children.Add(reset);
            }
            // The VB factory preserves the original animation type and DP owner.
            plan.Children.Add(_colorAnimation(previous, color, time));
            var current = (ClockGroup)plan.CreateClock(true);
            if (pauseForChecks) current.Controller.Pause();
            _clock = current;
            current.Completed += (sender, args) =>
            {
                if (!ReferenceEquals(_clock, current)) return;
                Detach();
                SetValues(targetScale, targetOpacity, _color);
            };
            _back.ApplyAnimationClock(UIElement.OpacityProperty, (AnimationClock)current.Children[0]);
            _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, (AnimationClock)current.Children[1]);
            _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, (AnimationClock)current.Children[1]);
            _fill.ApplyAnimationClock(SolidColorBrush.ColorProperty, (AnimationClock)current.Children[2]);
        }

        private static IEasingFunction Ease()
        {
            return new PowerEase { Power = 3, EasingMode = EasingMode.EaseOut };
        }

        private void SetValues(double scale, double opacity, Color color)
        {
            _scale.ScaleX = scale;
            _scale.ScaleY = scale;
            _back.Opacity = opacity;
            _fill.Color = color;
        }

        private void Detach()
        {
            var previous = _clock;
            _clock = null;
            _back.ApplyAnimationClock(UIElement.OpacityProperty, null);
            _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, null);
            _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, null);
            _fill.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
            previous?.Controller.Remove();
        }
    }
}
