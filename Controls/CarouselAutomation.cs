using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using KillerShot.Services;

namespace KillerShot.Controls;

/// <summary>What the carousel peers need from Cover Flow.</summary>
internal interface ICaptureCarousel
{
    IReadOnlyList<CaptureItem> Items { get; }
    int SelectedIndex { get; set; }
    Func<CaptureItem, string>? Describe { get; }
    /// <summary>Opens the item, as Enter or a click on the current card does.</summary>
    void Activate(CaptureItem item);
}

/// <summary>A carousel as a list of captures with one selected, so screen readers and other
/// automation can enumerate captures, read and change the selection and open one.</summary>
internal sealed class CarouselAutomationPeer(FrameworkElement owner, string className)
    : FrameworkElementAutomationPeer(owner), ISelectionProvider
{
    private readonly ConditionalWeakTable<CaptureItem, CarouselItemAutomationPeer> _peers = new();

    internal ICaptureCarousel Carousel => (ICaptureCarousel)Owner;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
    protected override string GetClassNameCore() => className;
    protected override bool IsKeyboardFocusableCore() => true;

    public override object? GetPattern(PatternInterface pattern) =>
        pattern == PatternInterface.Selection ? this : base.GetPattern(pattern);

    internal CarouselItemAutomationPeer PeerFor(CaptureItem item) =>
        _peers.GetValue(item, i => new CarouselItemAutomationPeer(this, i));

    protected override List<AutomationPeer> GetChildrenCore() =>
        Carousel.Items.Select(i => (AutomationPeer)PeerFor(i)).ToList();

    /// <summary>Called when the selection moves, by keys, wheel, click or automation.</summary>
    internal void RaiseSelected()
    {
        var items = Carousel.Items;
        int index = Carousel.SelectedIndex;
        if (index < 0 || index >= items.Count || !ListenerExists(AutomationEvents.SelectionItemPatternOnElementSelected)) return;
        PeerFor(items[index]).RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
    }

    IRawElementProviderSimple[] ISelectionProvider.GetSelection()
    {
        var items = Carousel.Items;
        int index = Carousel.SelectedIndex;
        return index >= 0 && index < items.Count ? [ProviderFromPeer(PeerFor(items[index]))] : [];
    }
    bool ISelectionProvider.CanSelectMultiple => false;
    bool ISelectionProvider.IsSelectionRequired => true;

    internal Rect OwnerScreenBounds()
    {
        if (PresentationSource.FromVisual(Owner) == null) return Rect.Empty;
        var o = (FrameworkElement)Owner;
        return new Rect(o.PointToScreen(new Point()), o.PointToScreen(new Point(o.ActualWidth, o.ActualHeight)));
    }
}

/// <summary>One capture in a carousel. Only the current card is on screen; selecting or
/// scrolling to another brings it to the front.</summary>
internal sealed class CarouselItemAutomationPeer(CarouselAutomationPeer parent, CaptureItem item)
    : AutomationPeer, ISelectionItemProvider, IInvokeProvider, IScrollItemProvider
{
    private int Index => IndexIn(parent.Carousel.Items);
    private int IndexIn(IReadOnlyList<CaptureItem> items)
    {
        for (int i = 0; i < items.Count; i++) if (items[i].Id == item.Id) return i;
        return -1;
    }
    private bool Current => Index >= 0 && Index == parent.Carousel.SelectedIndex;

    protected override string GetNameCore()
    {
        var items = parent.Carousel.Items;
        string text = parent.Carousel.Describe?.Invoke(item) ?? item.Window ?? item.Id.ToString(CultureInfo.InvariantCulture);
        if (Index < 0) return text;   // a capture that has left the carousel has no position
        return text + ", " + (Index + 1).ToString(CultureInfo.CurrentCulture) + " / " + items.Count.ToString(CultureInfo.CurrentCulture);
    }
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
    protected override string GetClassNameCore() => nameof(CaptureItem);
    protected override string GetAutomationIdCore() => "capture-" + item.Id.ToString(CultureInfo.InvariantCulture);
    protected override string GetAcceleratorKeyCore() => "";
    protected override string GetAccessKeyCore() => "";
    protected override string GetHelpTextCore() => "";
    protected override string GetItemStatusCore() => "";
    protected override string GetItemTypeCore() => "";
    protected override AutomationPeer? GetLabeledByCore() => null;
    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
    protected override List<AutomationPeer>? GetChildrenCore() => null;
    protected override bool HasKeyboardFocusCore() => Current && ((FrameworkElement)parent.Owner).IsKeyboardFocused;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override bool IsEnabledCore() => true;
    protected override bool IsKeyboardFocusableCore() => true;
    protected override bool IsOffscreenCore() => !Current;
    protected override bool IsPasswordCore() => false;
    protected override bool IsRequiredForFormCore() => false;
    protected override Rect GetBoundingRectangleCore() => Current ? parent.OwnerScreenBounds() : Rect.Empty;
    protected override Point GetClickablePointCore()
    {
        var r = GetBoundingRectangleCore();
        return r.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }
    protected override void SetFocusCore()
    {
        ((FrameworkElement)parent.Owner).Focus();
        Select();
    }

    public override object? GetPattern(PatternInterface pattern) =>
        pattern is PatternInterface.SelectionItem or PatternInterface.Invoke or PatternInterface.ScrollItem ? this : null;

    private void Select()
    {
        if (Index >= 0) parent.Carousel.SelectedIndex = Index;
    }

    void ISelectionItemProvider.Select() => Select();
    void ISelectionItemProvider.AddToSelection() => Select();
    void ISelectionItemProvider.RemoveFromSelection() { }
    bool ISelectionItemProvider.IsSelected => Current;
    IRawElementProviderSimple ISelectionItemProvider.SelectionContainer => ProviderFromPeer(parent);
    void IScrollItemProvider.ScrollIntoView() => Select();
    void IInvokeProvider.Invoke()
    {
        if (Index < 0) return;
        Select();
        parent.Carousel.Activate(item);
    }
}
