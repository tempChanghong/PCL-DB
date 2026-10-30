using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PclDownloadBridge.Presentation
{
    // PCL MyListItem.SetChecked: preserve the original marker and title ownership.
    // App-owned dependency properties and animation types enter through fixed factories.
    internal sealed class ListSelectionMotion
    {
        private readonly FrameworkElement _owner;
        private readonly DependencyProperty _foregroundProperty;
        private readonly Border _marker;
        private readonly Func<string, SolidColorBrush> _brush;
        private readonly Func<double, double, AnimationTimeline> _heightAnimation;
        private ClockGroup _clock;
        private SolidColorBrush _ink;
        private bool _hasState, _selected;
        private double _targetHeight;
        private Color _targetColor;

        internal ListSelectionMotion(FrameworkElement owner, DependencyProperty foregroundProperty, Border marker,
            Func<string, SolidColorBrush> brush, Func<double, double, AnimationTimeline> heightAnimation)
        {
            _owner = owner;
            _foregroundProperty = foregroundProperty;
            _marker = marker;
            _brush = brush;
            _heightAnimation = heightAnimation;
        }

        internal ClockGroup Transition => _clock;

        private string InkKey(bool selected) => selected ? (_owner.Height < 40 ? "ColorBrush3" : "ColorBrush2") : "ColorBrush1";

        internal void RefreshPalette()
        {
            _targetColor = _brush(InkKey(_selected)).Color;
            if (_clock is null || _ink is null) return;
            _ink.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
            _ink.Color = _targetColor;
        }

        internal void Update(bool selected, bool animate)
        {
            double height = selected ? Math.Max(0, _owner.ActualHeight - 12) : 0;
            var color = _brush(InkKey(selected)).Color;
            if (animate && _hasState && selected == _selected && height == _targetHeight && color == _targetColor) return;
            if (!animate)
            {
                Snap(selected);
                return;
            }
            // Stable Auto/Stretch geometry follows layout without an empty selection.
            if (_hasState && selected == _selected && _clock is null)
            {
                Snap(selected);
                return;
            }
            _hasState = true;
            _selected = selected;
            _targetHeight = height;
            _targetColor = color;
            double start = _marker.ActualHeight, opacity = _marker.Opacity;
            // The original property getter casts to Brush before its TryCast to solid.
            var previous = (Brush)_owner.GetValue(_foregroundProperty) as SolidColorBrush;
            var oldColor = previous is null ? _brush("ColorBrush1").Color : previous.Color;
            Detach();
            _marker.Height = start;
            _marker.Opacity = opacity;
            _marker.Margin = new Thickness(-1, 0, 0, 0);
            _marker.VerticalAlignment = VerticalAlignment.Center;
            _ink = new SolidColorBrush(oldColor);
            _owner.SetValue(_foregroundProperty, _ink);
            var plan = new ParallelTimeline();
            if (selected)
            {
                plan.Children.Add(_heightAnimation(start, height));
                plan.Children.Add(new DoubleAnimation(opacity, 1, TimeSpan.FromMilliseconds(30)));
            }
            else
            {
                plan.Children.Add(new DoubleAnimation(start, 0, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = new PowerEase { Power = 2, EasingMode = EasingMode.EaseIn }
                });
                plan.Children.Add(new DoubleAnimation(opacity, 0, TimeSpan.FromMilliseconds(70)) { BeginTime = TimeSpan.FromMilliseconds(40) });
            }
            plan.Children.Add(new ColorAnimation(oldColor, color, TimeSpan.FromMilliseconds(selected ? 200 : 120)));
            var clock = (ClockGroup)plan.CreateClock(true);
            _clock = clock;
            clock.Completed += (sender, args) =>
            {
                if (ReferenceEquals(_clock, clock)) Snap(selected);
            };
            _marker.ApplyAnimationClock(FrameworkElement.HeightProperty, (AnimationClock)clock.Children[0]);
            _marker.ApplyAnimationClock(UIElement.OpacityProperty, (AnimationClock)clock.Children[1]);
            _ink.ApplyAnimationClock(SolidColorBrush.ColorProperty, (AnimationClock)clock.Children[2]);
        }

        private void Snap(bool selected)
        {
            Detach();
            _hasState = true;
            _selected = selected;
            _targetHeight = selected ? Math.Max(0, _owner.ActualHeight - 12) : 0;
            _targetColor = _brush(InkKey(selected)).Color;
            _marker.Height = selected ? double.NaN : 0;
            _marker.Margin = new Thickness(-1, selected ? 6 : 0, 0, selected ? 6 : 0);
            _marker.VerticalAlignment = selected ? VerticalAlignment.Stretch : VerticalAlignment.Center;
            _marker.Opacity = selected ? 1 : 0;
            _marker.SetResourceReference(Border.BackgroundProperty, "ColorBrush3");
            _owner.SetResourceReference(_foregroundProperty, InkKey(selected));
        }

        private void Detach()
        {
            var old = _clock;
            _clock = null;
            _marker.ApplyAnimationClock(FrameworkElement.HeightProperty, null);
            _marker.ApplyAnimationClock(UIElement.OpacityProperty, null);
            _ink?.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
            old?.Controller.Remove();
        }
    }
}
