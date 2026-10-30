using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace PclDownloadBridge.Presentation
{
    // Fixed PCL 5ef81e4 MyPageRight.TriggerEnter/ExitAnimation and CE 915ac297.
    // These factories preserve the existing plans; VB retains clock ownership,
    // binding restoration, input gates, stagger calculation and custom Y types.
    internal static class PageMotion
    {
        internal static DoubleAnimationUsingKeyFrames CardOpacity(bool incoming, double start, double normal, int delay)
        {
            var animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(TimeSpan.FromMilliseconds(checked(delay + (incoming ? 100 : 70))))
            };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            if (delay > 0)
                animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
            var end = new EasingDoubleKeyFrame(incoming ? normal : 0,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(checked(delay + (incoming ? 100 : 70)))));
            if (incoming) end.EasingFunction = new PowerEase { Power = 2, EasingMode = EasingMode.EaseOut };
            animation.KeyFrames.Add(end);
            return animation;
        }

        internal static DoubleAnimation ExitY(double origin, int delay)
        {
            return new DoubleAnimation(origin, origin - 6, TimeSpan.FromMilliseconds(70))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay)
            };
        }

        internal static DoubleAnimationUsingKeyFrames SurfaceReveal(double opacity, int delay)
        {
            var animation = new DoubleAnimationUsingKeyFrames();
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(opacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
            return animation;
        }

        internal static DoubleAnimationUsingKeyFrames SurfaceOpacity(double opacity, int delay)
        {
            var animation = new DoubleAnimationUsingKeyFrames();
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(opacity,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(checked(delay + 100))),
                new PowerEase { Power = 2, EasingMode = EasingMode.EaseOut }));
            return animation;
        }

        internal static DoubleAnimation SurfaceExitOpacity(int delay)
        {
            return new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(70))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay)
            };
        }

        internal static DoubleAnimation ScrollBarExit()
        {
            return new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new PowerEase { Power = 3, EasingMode = EasingMode.EaseIn }
            };
        }

        internal static DoubleAnimation ScrollBarEntry(int delay)
        {
            return new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(350))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new PowerEase { Power = 3, EasingMode = EasingMode.EaseOut }
            };
        }

        internal static double CardOffset(double origin, double milliseconds)
        {
            return -origin / 16 * MotionCurves.PageY(milliseconds);
        }
    }
}
