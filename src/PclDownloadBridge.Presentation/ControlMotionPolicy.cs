using System;
using System.ComponentModel;
using System.Windows;

namespace PclDownloadBridge.Presentation
{
    // A control observes Windows animation preferences only while attached.
    // Keep notification and settle ordering identical to the original VB policy.
    internal sealed class ControlMotionPolicy
    {
        private readonly FrameworkElement _owner;
        private readonly Action _settle;
        private readonly PropertyChangedEventHandler _changed;
        private readonly bool _requiresInteraction;
        private bool _watching;
        private bool? _override;

        internal bool PauseForChecks { get; set; }

        internal ControlMotionPolicy(FrameworkElement owner, Action settle, bool requiresInteraction = true)
        {
            _owner = owner;
            _settle = settle;
            _requiresInteraction = requiresInteraction;
            _changed = (sender, args) =>
            {
                if (args.PropertyName != nameof(SystemParameters.ClientAreaAnimation) && !string.IsNullOrEmpty(args.PropertyName)) return;
                if (owner.Dispatcher.CheckAccess())
                {
                    if (_watching) settle();
                }
                else
                {
                    owner.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_watching) settle();
                    }));
                }
            };
            owner.Loaded += (sender, args) =>
            {
                if (!_watching) SystemParameters.StaticPropertyChanged += _changed;
                _watching = true;
                settle();
            };
            owner.Unloaded += (sender, args) =>
            {
                if (_watching) SystemParameters.StaticPropertyChanged -= _changed;
                _watching = false;
                settle();
            };
            owner.IsVisibleChanged += (sender, args) => settle();
            owner.IsEnabledChanged += (sender, args) => settle();
            owner.IsHitTestVisibleChanged += (sender, args) => settle();
            // A late-created policy subscribes immediately, but must not call
            // settle before its caller has assigned the owning policy field.
            if (owner.IsLoaded)
            {
                SystemParameters.StaticPropertyChanged += _changed;
                _watching = true;
            }
        }

        internal bool Interactive => _owner.IsEnabled && _owner.IsHitTestVisible && _owner.IsLoaded && _owner.IsVisible;

        internal bool CanAnimate => _watching && _owner.IsLoaded && _owner.IsVisible &&
            (!_requiresInteraction || Interactive) && _override.GetValueOrDefault(SystemParameters.ClientAreaAnimation);

        internal bool? EnabledForChecks
        {
            get => _override;
            set
            {
                _override = value;
                _settle();
            }
        }
    }
}
