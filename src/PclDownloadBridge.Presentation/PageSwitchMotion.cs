using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PclDownloadBridge.Presentation
{
    // Fixed PCL 5ef81e4 PageDownloadInstall.EnterSelectPage/ExitSelectPage
    // and CE 915ac297. Logical selection, cards and settling remain in VB.
    internal sealed class PageSwitchMotion
    {
        private readonly FrameworkElement _versions;
        private readonly FrameworkElement _install;
        private readonly TranslateTransform _versionOffset;
        private readonly TranslateTransform _installOffset;
        private readonly Action _completed;
        private ClockGroup _transition;

        internal PageSwitchMotion(FrameworkElement versions, FrameworkElement install,
            TranslateTransform versionOffset, TranslateTransform installOffset, Action completed)
        {
            _versions = versions;
            _install = install;
            _versionOffset = versionOffset;
            _installOffset = installOffset;
            _completed = completed;
        }

        internal ClockGroup Transition => _transition;

        internal static ParallelTimeline TransitionPlan(double outOpacity, double outX,
            double inOpacity, double inX, bool entering)
        {
            var plan = new ParallelTimeline { Duration = new Duration(TimeSpan.FromMilliseconds(260)) };
            plan.Children.Add(new DoubleAnimation(outOpacity, 0, TimeSpan.FromMilliseconds(70)) { BeginTime = TimeSpan.FromMilliseconds(10) });
            plan.Children.Add(new DoubleAnimation(outX, entering ? -50.0 : 50.0, TimeSpan.FromMilliseconds(90)) { BeginTime = TimeSpan.FromMilliseconds(10) });
            plan.Children.Add(new DoubleAnimation(inOpacity, 1, TimeSpan.FromMilliseconds(70)) { BeginTime = TimeSpan.FromMilliseconds(100) });
            plan.Children.Add(new DoubleAnimation(inX, 0, TimeSpan.FromMilliseconds(160))
            {
                BeginTime = TimeSpan.FromMilliseconds(100),
                EasingFunction = new PowerEase { Power = 5, EasingMode = EasingMode.EaseOut }
            });
            return plan;
        }

        internal void Start(FrameworkElement outgoing, FrameworkElement incoming,
            TranslateTransform outOffset, TranslateTransform inOffset, bool entering)
        {
            var clock = (ClockGroup)TransitionPlan(outgoing.Opacity, outOffset.X,
                incoming.Opacity, inOffset.X, entering).CreateClock(true);
            _transition = clock;
            clock.CurrentTimeInvalidated += (sender, args) =>
            {
                if (ReferenceEquals(_transition, clock) && entering && clock.CurrentTime.HasValue &&
                    clock.CurrentTime.Value.TotalMilliseconds >= 100)
                    _versions.Visibility = Visibility.Collapsed;
            };
            clock.Completed += (sender, args) =>
            {
                if (!ReferenceEquals(_transition, clock)) return;
                Stop();
                _completed();
            };
            outgoing.ApplyAnimationClock(UIElement.OpacityProperty, (AnimationClock)clock.Children[0]);
            outOffset.ApplyAnimationClock(TranslateTransform.XProperty, (AnimationClock)clock.Children[1]);
            incoming.ApplyAnimationClock(UIElement.OpacityProperty, (AnimationClock)clock.Children[2]);
            inOffset.ApplyAnimationClock(TranslateTransform.XProperty, (AnimationClock)clock.Children[3]);
        }

        internal void Stop()
        {
            var previous = _transition;
            _transition = null;
            previous?.Controller.Remove();
            _versions.BeginAnimation(UIElement.OpacityProperty, null);
            _install.BeginAnimation(UIElement.OpacityProperty, null);
            _versionOffset.BeginAnimation(TranslateTransform.XProperty, null);
            _installOffset.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }
}
