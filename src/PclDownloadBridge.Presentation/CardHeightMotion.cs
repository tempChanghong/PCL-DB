using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace PclDownloadBridge.Presentation
{
    // Measurement and content retirement stay with the original VB card.
    internal sealed class CardHeightMotion
    {
        private readonly FrameworkElement _owner;
        private readonly Func<double, double, AnimationTimeline> _animation;
        private readonly Action _settle;
        private AnimationClock _transition;
        private double _target = double.NaN;

        internal CardHeightMotion(FrameworkElement owner, Func<double, double, AnimationTimeline> animation, Action settle)
        {
            _owner = owner;
            _animation = animation;
            _settle = settle;
        }

        internal AnimationClock Transition => _transition;
        internal double Target => _target;

        internal void Start(double origin, double target)
        {
            Stop();
            if (origin <= 0 || Math.Abs(target - origin) < 1)
            {
                _settle();
                return;
            }
            _owner.ClipToBounds = true;
            var clock = (AnimationClock)_animation(origin, target).CreateClock(true);
            _target = target;
            _transition = clock;
            clock.Completed += (sender, args) =>
            {
                if (!ReferenceEquals(_transition, clock)) return;
                Stop();
                _settle();
            };
            _owner.Height = origin;
            _owner.ApplyAnimationClock(FrameworkElement.HeightProperty, clock);
        }

        internal void Stop()
        {
            var previous = _transition;
            _transition = null;
            _target = double.NaN;
            if (previous is null) return;
            previous.Controller.Remove();
            _owner.ApplyAnimationClock(FrameworkElement.HeightProperty, null);
        }
    }
}
