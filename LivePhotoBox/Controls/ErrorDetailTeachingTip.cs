using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using LivePhotoBox.Services;
using LivePhotoBox.Models;
using Windows.Foundation;

namespace LivePhotoBox.Controls;

/// <summary>
/// The standard TeachingTip appearance and dismissal, with an accessible peer
/// that works in portable processes without a package identity.
/// </summary>
public sealed class ErrorDetailTeachingTip : TeachingTip
{
    private FrameworkElement? _requestedTarget;
    private object? _requestedData;
    private FrameworkElement? _observedTarget;
    private object? _targetData;
    private bool _closing;
    private ScrollViewer? _targetScrollViewer;
    private double _scrollX;
    private double _scrollY;
    private bool _requestedDiagnosticPlacement;
    private readonly DispatcherTimer _closeRetry = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private void Trace(string action) => LogService.Debug($"ResultTip {action}: open={IsOpen}, closing={_closing}, requested={(_requestedTarget as TextBlock)?.Text}, target={(Target as TextBlock)?.Text}", LogSource.UI);

    // TeachingTip cannot safely retarget its open popup while a previous close
    // animation is still pending. Serialize closing and opening on the same tip.
    public void ToggleAt(FrameworkElement target, bool diagnosticPlacement = false)
    {
        Trace("toggle " + (target as TextBlock)?.Text);
        if (ReferenceEquals(_requestedTarget, target)) { Dismiss(); return; }
        _requestedTarget = target;
        _requestedData = target.DataContext;
        _requestedDiagnosticPlacement = diagnosticPlacement;
        if (IsOpen) { RequestClose(); return; }
        if (!_closing) OpenRequestedTarget();
    }

    public void Dismiss()
    {
        Trace("dismiss");
        _requestedTarget = null;
        _requestedData = null;
        StopObservingTarget();
        if (IsOpen) RequestClose();
        else if (!_closing) Target = null;
    }

    private void RequestClose()
    {
        // WinUI may reject IsOpen=false during its opening animation. Retain
        // the close request until Closed confirms it, rather than retargeting
        // an opening popup or losing a scroll/collapse dismissal.
        _closing = true;
        _closeRetry.Start();
        IsOpen = false;
    }

    private void OpenRequestedTarget()
    {
        var target = _requestedTarget;
        if (target is null || target.XamlRoot is null || !ReferenceEquals(target.DataContext, _requestedData) ||
            target.ActualWidth <= 0 || target.ActualHeight <= 0)
        { Dismiss(); return; }
        StopObservingTarget();
        _observedTarget = target;
        _targetData = target.DataContext;
        target.LayoutUpdated += Target_LayoutUpdated;
        target.Unloaded += Target_Unloaded;
        for (DependencyObject? parent = target; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer)
            {
                _targetScrollViewer = viewer;
                _scrollX = viewer.HorizontalOffset;
                _scrollY = viewer.VerticalOffset;
                viewer.ViewChanged += Target_ViewChanged;
                break;
            }
        Target = target;
        ConfigurePlacement(target);
        Trace("open");
        IsOpen = true;
    }

    private void ConfigurePlacement(FrameworkElement target)
    {
        // With unconstrained placement WinUI honors the selected direction
        // instead of trying Right/Bottom. We constrain the content ourselves
        // to the actual XamlRoot and choose only Left or an above-left position.
        ShouldConstrainToRootBounds = false;
        PreferredPlacement = TeachingTipPlacementMode.Left;
        if (target.XamlRoot is not { Content: { } root } xamlRoot) return;
        var bounds = target.TransformToVisual(root).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        double leftWidth = Math.Max(80, Math.Min(520, bounds.Left - 48));
        MaxWidth = Math.Min(560, Math.Max(80, xamlRoot.Size.Width - 32));
        if (Content is not FrameworkElement content) return;
        if (content is ScrollViewer measuredViewer) measuredViewer.MaxHeight = 360;
        content.MaxWidth = Math.Min(520, leftWidth - 32);
        content.Measure(new Size(content.MaxWidth, Math.Max(40, xamlRoot.Size.Height - 100)));
        double requiredHeight = content.DesiredSize.Height + 80;
        double centerY = bounds.Top + bounds.Height / 2;
        if (_requestedDiagnosticPlacement &&
            (centerY + requiredHeight / 2 + 16 > xamlRoot.Size.Height || centerY - requiredHeight / 2 < 16))
        {
            PreferredPlacement = TeachingTipPlacementMode.TopLeft;
            if (content is ScrollViewer viewer) viewer.MaxHeight = Math.Max(40, Math.Min(360, bounds.Top - 96));
        }
        else if (content is ScrollViewer viewer)
        {
            if (!_requestedDiagnosticPlacement && centerY + requiredHeight / 2 + 16 > xamlRoot.Size.Height)
            {
                PreferredPlacement = TeachingTipPlacementMode.LeftTop;
                viewer.MaxHeight = Math.Max(40, Math.Min(360, bounds.Top - 80));
            }
            else viewer.MaxHeight = Math.Max(40, Math.Min(360, 2 * Math.Min(centerY - 16, xamlRoot.Size.Height - centerY - 16) - 80));
        }
    }

    private void Target_LayoutUpdated(object? sender, object e)
    {
        if (_observedTarget is not { } target) return;
        // IsLoaded can be transiently false during a virtualized row's layout
        // pass even while it still has a valid visual root. The actual Unloaded
        // event and data identity, rather than that transient flag, invalidate it.
        if (target.XamlRoot is null) { Dismiss(); return; }
        // Recycling or hiding a row invalidates the content's identity.
        if (target.Visibility != Visibility.Visible || !ReferenceEquals(target.DataContext, _targetData))
        {
            Trace($"anchor recycled: visibility={target.Visibility}, sameData={ReferenceEquals(target.DataContext, _targetData)}");
            Dismiss();
        }
    }

    private void Target_Unloaded(object sender, RoutedEventArgs e) => Dismiss();
    private void Target_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_targetScrollViewer is { } viewer &&
            (Math.Abs(viewer.HorizontalOffset - _scrollX) > 0.5 || Math.Abs(viewer.VerticalOffset - _scrollY) > 0.5))
        {
            Trace($"scroll: {_scrollX},{_scrollY} -> {viewer.HorizontalOffset},{viewer.VerticalOffset}");
            Dismiss();
        }
    }

    private void StopObservingTarget()
    {
        if (_observedTarget is { } target)
        {
            target.LayoutUpdated -= Target_LayoutUpdated;
            target.Unloaded -= Target_Unloaded;
        }
        _observedTarget = null;
        _targetData = null;
        if (_targetScrollViewer is { } viewer) viewer.ViewChanged -= Target_ViewChanged;
        _targetScrollViewer = null;
    }

    public ErrorDetailTeachingTip()
    {
        DefaultStyleKey = typeof(TeachingTip);
        _closeRetry.Tick += (_, _) =>
        {
            if (!_closing) { _closeRetry.Stop(); return; }
            if (IsOpen) IsOpen = false;
        };
        Closing += (_, _) => { Trace("closing"); _closing = true; StopObservingTarget(); };
        CloseButtonClick += (_, _) => _requestedTarget = null;
        Closed += (_, _) => { Trace("closed"); if (!IsOpen) CompleteClose(); };
        Unloaded += (_, _) => { Dismiss(); _closeRetry.Stop(); _closing = false; Target = null; };
        RegisterPropertyChangedCallback(IsOpenProperty, (_, _) =>
        {
            if (!IsOpen) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (IsOpen && FrameworkElementAutomationPeer.CreatePeerForElement(this) is ErrorDetailPeer peer)
                    peer.RaiseNotificationEvent(AutomationNotificationKind.Other,
                        AutomationNotificationProcessing.ImportantMostRecent, Title, "ErrorDetail");
            });
        });
    }

    private void CompleteClose()
    {
        _closeRetry.Stop();
        _closing = false;
        Target = null;
        DispatcherQueue.TryEnqueue(() => { if (!IsOpen && !_closing) OpenRequestedTarget(); });
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ErrorDetailPeer(this);

    // TeachingTip::OnPopupOpened queries Package.Current when its peer is a
    // TeachingTipAutomationPeer. In an unpackaged process that throws
    // 0x80073D54 before the control's own catch. A plain accessible pane avoids
    // that path; the real content/close button remain accessible in the popup.
    // Upstream: microsoft/microsoft-ui-xaml, controls/dev/TeachingTip/TeachingTip.cpp.
    private sealed class ErrorDetailPeer(ErrorDetailTeachingTip owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(TeachingTip);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetNameCore()
        {
            string name = AutomationProperties.GetName(owner);
            return string.IsNullOrEmpty(name) ? owner.Title : name;
        }

        protected override string GetHelpTextCore() => owner.Content is ScrollViewer { Content: TextBlock text }
            ? text.Text : base.GetHelpTextCore();
    }
}
