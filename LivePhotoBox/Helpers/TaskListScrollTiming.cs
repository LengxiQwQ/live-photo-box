using LivePhotoBox.Controls;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;

namespace LivePhotoBox.Helpers;

// UI callback gaps are diagnostic signals, not GPU-presented FPS. Aggregate one
// line per gesture; never walk all rows or log on every scroll/render callback.
internal sealed class TaskListScrollTiming(string owner, ListView list, ScrollViewer viewer, Func<bool> isActive)
{
    private readonly DispatcherQueueTimer _quiet = list.DispatcherQueue.CreateTimer();
    private bool _attached, _tracking, _operationActive;
    private long _started, _lastView, _lastFrame, _fits, _fitTicks, _hits;
    private int _events, _frames, _over50;
    private double _maxGap;
    private double _lastOffset;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _lastOffset = viewer.VerticalOffset;
        _quiet.Interval = TimeSpan.FromMilliseconds(220);
        _quiet.IsRepeating = false;
        _quiet.Tick += OnQuiet;
        viewer.ViewChanged += OnViewChanged;
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        viewer.ViewChanged -= OnViewChanged;
        _quiet.Tick -= OnQuiet;
        Finish();
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        double offset = viewer.VerticalOffset;
        if (Math.Abs(offset - _lastOffset) < 0.1) return;
        _lastOffset = offset;
        long now = Stopwatch.GetTimestamp();
        if (!_tracking)
        {
            _tracking = true;
            _started = now;
            _lastFrame = now;
            _events = _frames = _over50 = 0;
            _maxGap = 0;
            _operationActive = isActive();
            _fits = AdaptiveFileName.FitCount;
            _fitTicks = AdaptiveFileName.FitTicks;
            _hits = AdaptiveFileName.FitHits;
            CompositionTarget.Rendering += OnRendering;
        }
        else if (Milliseconds(now - _lastView) > 150) _lastFrame = now;
        _lastView = now;
        _events++;
        _quiet.Stop();
        _quiet.Start();
    }

    private void OnRendering(object? sender, object args)
    {
        if (!_tracking) return;
        long now = Stopwatch.GetTimestamp();
        double gap = Milliseconds(now - _lastFrame);
        _maxGap = Math.Max(_maxGap, gap);
        _frames++;
        if (gap >= 50) _over50++;
        _lastFrame = now;
    }

    private void OnQuiet(DispatcherQueueTimer timer, object args)
    {
        if (Milliseconds(Stopwatch.GetTimestamp() - _lastView) < 200) { _quiet.Start(); return; }
        Finish();
    }

    private void Finish()
    {
        _quiet.Stop();
        if (!_tracking) return;
        _tracking = false;
        CompositionTarget.Rendering -= OnRendering;
        if (ApplicationShutdown.IsClosing) return;
        int panelChildren = list.ItemsPanelRoot is Panel panel ? panel.Children.Count : -1;
        LogService.Info($"Scroll timing: page={owner}, operationActive={_operationActive}, items={list.Items.Count}, panelChildren={panelChildren}, events={_events}, durationMs={Milliseconds(_lastView - _started):F1}, renderCallbacks={_frames}, maxRenderCallbackGapMs={_maxGap:F1}, callbackGapsOver50Ms={_over50}, filenameFits={AdaptiveFileName.FitCount - _fits}, filenameCacheHits={AdaptiveFileName.FitHits - _hits}, filenameFitMs={Milliseconds(AdaptiveFileName.FitTicks - _fitTicks):F1}", LogSource.UI);
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
