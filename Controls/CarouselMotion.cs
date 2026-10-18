using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerShot.Controls;

/// <summary>Continuous, bounded motion shared by the horizontal carousel and vertical drum.</summary>
internal sealed class CarouselMotion
{
    private readonly FrameworkElement _owner;
    private readonly Action _changed;
    private readonly Action<int> _select;
    private readonly Func<int> _maximum;
    private readonly Func<double> _itemPixels;
    private readonly bool _vertical;
    private TimeSpan? _lastFrame;
    private double _target, _velocity;
    private bool _listening;
    private bool _pressed, _dragging, _coasting, _selecting, _dirty;
    private double _pressCoordinate, _pressPosition, _lastPosition;
    private long _lastPointer;

    public double Position { get; private set; }
    public bool IsMoving => _listening || _pressed;

    public CarouselMotion(FrameworkElement owner, Action changed, bool vertical,
        Func<double> itemPixels, Func<int> maximum, Action<int> select)
    {
        _owner = owner;
        _changed = changed;
        _vertical = vertical;
        _itemPixels = itemPixels;
        _maximum = maximum;
        _select = select;
        owner.Unloaded += (_, _) => Reset(_target);
        owner.Loaded += (_, _) => { _changed(); if (Math.Abs(Position - _target) > .001) Start(); };
    }

    public void Reset(double position)
    {
        Stop();
        _pressed = _dragging = _coasting = false;
        if (_owner.IsMouseCaptured) _owner.ReleaseMouseCapture();
        Position = _target = position;
        _velocity = 0;
    }

    public void MoveTo(int index)
    {
        if (_selecting) return;
        _pressed = _dragging = _coasting = false;
        if (_owner.IsMouseCaptured) _owner.ReleaseMouseCapture();
        _target = index;
        if (!_owner.IsLoaded) { Reset(index); _changed(); return; }
        Start();
    }

    public void Press(MouseButtonEventArgs e)
    {
        if (_maximum() < 1) return;
        _owner.Focus();
        Stop();
        _pressed = true;
        _dragging = _coasting = false;
        _velocity = 0;
        _target = Math.Round(Position);
        PublishSelection();
        _pressPosition = _lastPosition = Position;
        _pressCoordinate = Coordinate(e.GetPosition(_owner));
        _lastPointer = Stopwatch.GetTimestamp();
        if (!_owner.CaptureMouse()) { _pressed = false; Start(); }
        e.Handled = true;
    }

    public void Drag(MouseEventArgs e)
    {
        if (!_pressed) return;
        double distance = Coordinate(e.GetPosition(_owner)) - _pressCoordinate;
        double threshold = _vertical ? SystemParameters.MinimumVerticalDragDistance : SystemParameters.MinimumHorizontalDragDistance;
        if (!_dragging && Math.Abs(distance) < threshold) return;
        _dragging = true;
        Position = Math.Clamp(_pressPosition - distance / Math.Max(24, _itemPixels()), 0, _maximum());
        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(_lastPointer, now).TotalSeconds;
        if (elapsed > .001)
        {
            double sample = Math.Clamp((Position - _lastPosition) / elapsed, -24, 24);
            double blend = 1 - Math.Exp(-elapsed * 25);
            _velocity += (sample - _velocity) * blend;
        }
        _lastPosition = Position;
        _lastPointer = now;
        _dirty = true;
        Start();
        e.Handled = true;
    }

    public bool Release(MouseButtonEventArgs e)
    {
        if (!_pressed) return false;
        bool dragged = _dragging;
        // A held pointer has no throw, even if its last movement was fast.
        if (Stopwatch.GetElapsedTime(_lastPointer).TotalMilliseconds > 100) _velocity = 0;
        _pressed = _dragging = false;
        _owner.ReleaseMouseCapture();
        _coasting = dragged && Math.Abs(_velocity) > .5;
        _target = Math.Round(Position);
        Start();
        return dragged;
    }

    public void LostCapture()
    {
        if (!_pressed) return;
        _pressed = _dragging = _coasting = false;
        _velocity = 0;
        _target = Math.Round(Position);
        Start();
    }

    public void Interrupt()
    {
        if (!_coasting && !_pressed) return;
        _pressed = _dragging = _coasting = false;
        if (_owner.IsMouseCaptured) _owner.ReleaseMouseCapture();
        _velocity = 0;
        _target = Math.Round(Position);
        PublishSelection();
        Start();
    }

    private double Coordinate(Point point) => _vertical ? point.Y : point.X;

    private void PublishSelection()
    {
        _selecting = true;
        try { _select((int)Math.Round(Position)); }
        finally { _selecting = false; }
    }

    private void Start()
    {
        if (_listening) return;
        _listening = true;
        _lastFrame = null;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!_listening) return;
        _listening = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        // A missed frame should delay the trip, rather than skip a stack of pictures.
        double dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 1.0 / 30) : 1.0 / 60;
        _lastFrame = now;
        if (_pressed)
        {
            if (_dirty) { _dirty = false; _changed(); }
            return;
        }
        if (_coasting)
        {
            double friction = Math.Exp(-4.5 * dt);
            double nextPosition = Position + _velocity * (1 - friction) / 4.5;
            Position = Math.Clamp(nextPosition, 0, _maximum());
            _velocity *= friction;
            if (nextPosition != Position || Math.Abs(_velocity) < .5)
            {
                _coasting = false;
                _target = Math.Round(Position);
                if (nextPosition != Position) _velocity = 0;
            }
            _changed();
            return;
        }
        const double frequency = 12, maxSpeed = 24;
        double offset = Position - _target;
        double c = _velocity + frequency * offset;
        double decay = Math.Exp(-frequency * dt);
        double next = _target + (offset + c * dt) * decay;
        _velocity = Math.Clamp((_velocity - frequency * c * dt) * decay, -maxSpeed, maxSpeed);
        Position = Math.Clamp(Position + Math.Clamp(next - Position, -maxSpeed * dt, maxSpeed * dt), 0, _maximum());
        if (Math.Abs(Position - _target) < .001 && Math.Abs(_velocity) < .03)
        {
            Position = _target;
            _velocity = 0;
            Stop();
            PublishSelection();
        }
        _changed();
    }
}
