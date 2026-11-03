using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace LivePhotoBox.Controls;

/// <summary>
/// The standard TeachingTip appearance and dismissal, with an accessible peer
/// that works in portable processes without a package identity.
/// </summary>
public sealed class ErrorDetailTeachingTip : TeachingTip
{
    public ErrorDetailTeachingTip()
    {
        DefaultStyleKey = typeof(TeachingTip);
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
