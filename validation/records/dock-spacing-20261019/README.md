# BF-L002 dock splitter spacing

October 19, 2026. KillerShot 1.0.0, unreleased. Bounded repair in Controls/DockHost.cs.

The public DockHost control was measured, arranged, and rendered without showing a window or sending desktop input. An 8px marker supplied through its public Rail property measures the layout independently of font metrics. The existing splitter template line was forced visible for inspection, as in the original review. This does not reproduce hover or dragging.

Before repair, expanded Left and Top had 11px versus 8px gaps. Right and Bottom had 10px versus 8px. All four collapsed states were 8px versus 8px. The failing regression exited 1. The confirmed cause was the centered line inside the separate 6px splitter track. Rail margin and body inset hypotheses were contradicted by the balanced collapsed states and fixed body gap.

The line now sits at the rail-facing edge. All eight states have 8px versus 8px gaps and the expanded splitter retains its 6px hit target. The unchanged regression passed with exit 0, including a rerun through the retained runner. Every collapsed before/after image has an identical SHA-256 hash.

Run from PowerShell with a new directory under scratch:

```powershell
./tests/DockSpacing/Test-DockSpacing.ps1 -WorkDirectory C:/Users/steve/scratch/dock-spacing-check
```

The retained .cs.txt avoids the application's default source glob. The runner compiles the current DockHost.cs directly in its scratch project.

The original theme probe also rendered 104 isolated control states against current Shot themes and RailButton markup. Dark Top expanded was visually inspected alongside the marker's Top before/after images. This supports the control composition only; the probe uses simplified pane contents and forces the splitter line visible. Full window behavior, native DPI, actual pointer/keyboard resizing, and drag behavior remain unverified.

The changed KillerShot app built in Debug with zero warnings and errors. Concurrent Controls.xaml and CapCenterMargin.cs work was preserved. The coordinator reviewed the source diff and representative render, and added the changelog entry before the local commit.
