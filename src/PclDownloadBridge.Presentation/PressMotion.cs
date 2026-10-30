using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PclDownloadBridge.Presentation
{
    // The App facade supplies its original enum and WPF animation factory.
    internal sealed class PressMotion<TKind>
    {
        private readonly FrameworkElement _owner;
        private readonly ScaleTransform _scale;
        private readonly Action _cancelPress;
        private readonly Func<bool> _canAnimate;
        private readonly Func<bool> _pauseForChecks;
        private readonly Func<double, TKind, AnimationTimeline> _animation;
        private readonly Func<TKind, bool> _isDown;
        private AnimationClock _clock;

        internal PressMotion(FrameworkElement owner, ScaleTransform scale, Action cancelPress,
            Func<bool> canAnimate, Func<bool> pauseForChecks,
            Func<double, TKind, AnimationTimeline> animation, Func<TKind, bool> isDown)
        {
            _owner = owner;
            _scale = scale;
            _cancelPress = cancelPress;
            _canAnimate = canAnimate;
            _pauseForChecks = pauseForChecks;
            _animation = animation;
            _isDown = isDown;
            owner.Unloaded += (sender, args) => Cancel();
            owner.IsVisibleChanged += (sender, args) =>
            {
                if (!owner.IsVisible) Cancel();
            };
            owner.IsHitTestVisibleChanged += (sender, args) =>
            {
                if (!owner.IsHitTestVisible) Cancel();
            };
            owner.IsEnabledChanged += (sender, args) =>
            {
                if (!owner.IsEnabled) Cancel();
            };
        }

        internal AnimationClock Transition => _clock;

        internal void Start(TKind kind)
        {
            if (!_owner.IsLoaded)
            {
                ResetScale();
                return;
            }
            if (!_owner.IsVisible || !_owner.IsHitTestVisible || !_owner.IsEnabled)
            {
                Cancel();
                return;
            }
            if (_canAnimate != null && !_canAnimate())
            {
                ResetScale();
                return;
            }
            var origin = _scale.ScaleX;
            ResetScale();
            _scale.ScaleX = origin;
            _scale.ScaleY = origin;
            var clock = (AnimationClock)_animation(origin, kind).CreateClock(true);
            if (_pauseForChecks != null && _pauseForChecks()) clock.Controller.Pause();
            _clock = clock;
            if (!_isDown(kind))
            {
                clock.Completed += (sender, args) =>
                {
                    if (ReferenceEquals(_clock, clock)) ResetScale();
                };
            }
            _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, clock);
            _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, clock);
        }

        internal void Cancel()
        {
            _cancelPress();
            ResetScale();
        }

        private void ResetScale()
        {
            var old = _clock;
            _clock = null;
            _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, null);
            _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, null);
            old?.Controller.Remove();
            _scale.ScaleX = 1;
            _scale.ScaleY = 1;
        }
    }
}
