using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerShot.Controls;

/// <summary>Preserves the header layout while exposing its menu action to automation.</summary>
internal sealed class CollectionHeader : StackPanel
{
    public CollectionHeader() => IsKeyboardFocusWithinChanged += (_, _) => InvalidateVisual();
    internal Action? OpenMenu { get; set; }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (IsKeyboardFocusWithin && ContextMenu != null && ActualWidth > 1 && ActualHeight > 1)
            drawing.DrawRectangle(null, new Pen(TryFindResource("SelectionAccent") as Brush ?? SystemColors.HighlightBrush, 1),
                new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1));
    }

    protected override AutomationPeer? OnCreateAutomationPeer() => ContextMenu == null
        ? base.OnCreateAutomationPeer() : new HeaderPeer(this);

    private sealed class HeaderPeer(CollectionHeader owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override string GetClassNameCore() => nameof(CollectionHeader);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Invoke ? this : base.GetPattern(pattern);
        public void Invoke()
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            if (!owner.IsLoaded || !owner.IsVisible) throw new ElementNotAvailableException();
            owner.Dispatcher.BeginInvoke(() => { if (owner.IsEnabled && owner.IsLoaded && owner.IsVisible) owner.OpenMenu?.Invoke(); });
        }
    }
}
