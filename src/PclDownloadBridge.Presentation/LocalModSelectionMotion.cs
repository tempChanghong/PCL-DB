using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PclDownloadBridge.Presentation
{
    // PCL MyLocalModItem.Checked keeps marker/title ownership separate from hover.
    internal sealed class LocalModSelectionMotion
    {
        private readonly Border _marker;
        private readonly TextBlock _title;
        private readonly Func<string, SolidColorBrush> _brush;
        private readonly Func<double, double, AnimationTimeline> _heightAnimation;
        private readonly Func<Color, Color, int, AnimationTimeline> _colorAnimation;
        private ClockGroup _clock;
        private SolidColorBrush _ink;
        private bool _selected;
        private string _key;

        internal LocalModSelectionMotion(Border marker, TextBlock title, Func<string, SolidColorBrush> brush,
            Func<double, double, AnimationTimeline> heightAnimation, Func<Color, Color, int, AnimationTimeline> colorAnimation)
        {
            _marker = marker;
            _title = title;
            _brush = brush;
            _heightAnimation = heightAnimation;
            _colorAnimation = colorAnimation;
        }

        internal ClockGroup Transition => _clock;

        internal void RefreshPalette(string key)
        {
            _key = key;
            if (_ink is null)
            {
                _title.SetResourceReference(TextBlock.ForegroundProperty, key);
            }
            else
            {
                _ink.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
                _ink.Color = _brush(key).Color;
            }
        }

        internal void Update(bool selected, string key, bool animate, bool pause)
        {
            if (!animate)
            {
                Snap(selected, key);
                return;
            }
            if (selected == _selected)
            {
                // Metadata accent changes do not restart Checked geometry.
                // VB's binary string comparison treats Nothing and empty as equal.
                if (!string.Equals(key ?? "", _key ?? "", StringComparison.Ordinal)) RefreshPalette(key);
                return;
            }
            double height = _marker.Height, opacity = _marker.Opacity;
            var color = ((SolidColorBrush)_title.Foreground).Color;
            Detach();
            _selected = selected;
            _key = key;
            _marker.Height = height;
            _marker.Opacity = opacity;
            _ink = new SolidColorBrush(color);
            _title.Foreground = _ink;
            var plan = new ParallelTimeline();
            if (selected)
            {
                plan.Children.Add(_heightAnimation(height, 32));
                plan.Children.Add(new DoubleAnimation(opacity, 1, TimeSpan.FromMilliseconds(30)));
            }
            else
            {
                plan.Children.Add(new DoubleAnimation(height, 0, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = new PowerEase { Power = 2, EasingMode = EasingMode.EaseIn }
                });
                // Keep the sampled opacity through the first 40 ms, including reversal.
                var alpha = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(110) };
                alpha.KeyFrames.Add(new DiscreteDoubleKeyFrame(opacity, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                alpha.KeyFrames.Add(new DiscreteDoubleKeyFrame(opacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40))));
                alpha.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
                plan.Children.Add(alpha);
            }
            plan.Children.Add(_colorAnimation(color, _brush(key).Color, selected ? 200 : 120));
            var current = (ClockGroup)plan.CreateClock(true);
            if (pause) current.Controller.Pause();
            _clock = current;
            current.Completed += (sender, args) =>
            {
                if (ReferenceEquals(_clock, current)) Snap(_selected, _key);
            };
            _marker.ApplyAnimationClock(FrameworkElement.HeightProperty, (AnimationClock)current.Children[0]);
            _marker.ApplyAnimationClock(UIElement.OpacityProperty, (AnimationClock)current.Children[1]);
            _ink.ApplyAnimationClock(SolidColorBrush.ColorProperty, (AnimationClock)current.Children[2]);
        }

        private void Snap(bool selected, string key)
        {
            Detach();
            _selected = selected;
            _key = key;
            _marker.Height = selected ? 32.0 : 0.0;
            _marker.Opacity = selected ? 1.0 : 0.0;
            _title.SetResourceReference(TextBlock.ForegroundProperty, key);
        }

        private void Detach()
        {
            var previous = _clock;
            _clock = null;
            _marker.ApplyAnimationClock(FrameworkElement.HeightProperty, null);
            _marker.ApplyAnimationClock(UIElement.OpacityProperty, null);
            _ink?.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
            _ink = null;
            previous?.Controller.Remove();
        }
    }
}
