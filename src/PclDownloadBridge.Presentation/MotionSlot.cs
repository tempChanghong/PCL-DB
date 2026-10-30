using System.Windows;
using System.Windows.Media.Animation;

namespace PclDownloadBridge.Presentation
{
    // PCL / PCL-CE ModAnimation: retire the previous owner before replacing it.
    // The VB facade retains its type and entry points; this is the sole clock state.
    internal sealed class MotionSlot
    {
        private readonly DependencyObject _target;
        private readonly DependencyProperty _property;

        internal AnimationClock Clock { get; set; }

        internal MotionSlot(DependencyObject target, DependencyProperty property)
        {
            _target = target;
            _property = property;
        }

        internal void Animate(AnimationTimeline animation, object finalValue, bool pauseForChecks = false)
        {
            var origin = _target.GetValue(_property);
            SetValue(origin);
            var current = (AnimationClock)animation.CreateClock(true);
            if (pauseForChecks) current.Controller.Pause();
            Clock = current;
            current.Completed += (sender, args) =>
            {
                if (ReferenceEquals(Clock, current)) SetValue(finalValue);
            };
            Apply(current);
        }

        internal void SetValue(object value)
        {
            var previous = Clock;
            Clock = null;
            Apply(null);
            previous?.Controller.Remove();
            // These privately owned properties require the real base value.
            _target.SetValue(_property, value);
        }

        private void Apply(AnimationClock clock)
        {
            if (_target is Animatable)
                ((Animatable)_target).ApplyAnimationClock(_property, clock);
            else
                ((UIElement)_target).ApplyAnimationClock(_property, clock);
        }
    }
}
