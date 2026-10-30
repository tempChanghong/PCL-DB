using System;

namespace PclDownloadBridge.Presentation
{
    // PCL 5ef81e4 ModAnimation / PCL-CE 915ac297 easing formulas.
    // Keep the bridge's existing operation order and IEEE floating-point behavior.
    internal static class MotionCurves
    {
        internal static double Fluent(double progress, double power)
        {
            return 1 - Math.Pow(1 - Math.Max(0, Math.Min(1, progress)), power);
        }

        internal static double Back(double progress, double power = 3)
        {
            var p = Math.Max(0, Math.Min(1, progress));
            return 1 - Math.Pow(1 - p, 3 - power * 0.5) * Math.Cos(1.5 * Math.PI * p);
        }

        internal static double PageY(double milliseconds)
        {
            // Parallel AaTranslateY(5,250,fluent) + AaTranslateY(11,350,back).
            return -16 + 5 * Fluent(milliseconds / 250, 3) + 11 * Back(milliseconds / 350);
        }
    }
}
