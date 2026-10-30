using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace PclDownloadBridge.Presentation
{
    // Fixed PCL 5ef81e4 MyCard.StartHeightAnimation and CE 915ac297:
    // retain the established PCL-DB plans and the original App easing type.
    internal static class CardMotion
    {
        internal static DoubleAnimationUsingKeyFrames HeightTransition(double origin, double target,
            Func<double, double, double, IEasingFunction> initialSpeedEase)
        {
            var delta = target - origin;
            var distance = Math.Abs(delta);
            var animation = new DoubleAnimationUsingKeyFrames();
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(origin, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            if (distance <= 800)
            {
                animation.KeyFrames.Add(new EasingDoubleKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)),
                    new PowerEase { Power = 5, EasingMode = EasingMode.EaseOut }));
                animation.Duration = new Duration(TimeSpan.FromMilliseconds(150));
            }
            else
            {
                int easeLength, easeTime, initialSpeed;
                if (delta < 0)
                {
                    easeLength = 200;
                    easeTime = 150;
                    initialSpeed = checked((int)Math.Round((distance - easeLength) / 0.1));
                }
                else if (distance > 3000)
                {
                    initialSpeed = 5000;
                    easeLength = checked((int)Math.Round(distance - initialSpeed * 0.3));
                    easeTime = 400;
                }
                else
                {
                    easeLength = 150;
                    easeTime = 200;
                    initialSpeed = 4000;
                }
                // Match typed VB CInt's rounding, checked cast and overflow exception.
                var linearTime = checked((int)Math.Round((distance - easeLength) / initialSpeed * 1000));
                var middle = origin + (distance - easeLength) * Math.Sign(delta);
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(middle, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(linearTime))));
                animation.KeyFrames.Add(new EasingDoubleKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(checked(linearTime + easeTime))),
                    initialSpeedEase(initialSpeed, easeTime / 1000.0, easeLength)));
                animation.Duration = new Duration(TimeSpan.FromMilliseconds(checked(linearTime + easeTime)));
            }
            return animation;
        }

        internal static DoubleAnimation ArrowTransition(double origin, double target)
        {
            return new DoubleAnimation(origin, target, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new PowerEase { Power = 5, EasingMode = EasingMode.EaseOut }
            };
        }

        internal static double InitialSpeedAlpha(double initialSpeed, double seconds, double distance)
        {
            return Math.Max(0, initialSpeed * seconds / distance - 1);
        }

        // The original VB override clamps time and reads its Alpha DP first.
        internal static double InitialSpeedValue(double percent, double alpha)
        {
            if (alpha == 0) return percent;
            return (alpha + 1) * percent / (1 + alpha * percent);
        }
    }
}
