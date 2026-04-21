using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace RoundedTB
{
    public class Background
    {
        private struct ReloadChecker
        {
            public bool IsReload { get; set; }
        }

        // Just have a reference point for the Dispatcher
        private MainWindow _mw;
        public MainWindow mw
        {
            get
            {
                if (_mw == null)
                {
                    // Try to get the MainWindow when first accessed
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    if (mainWindow != null)
                    {
                        _mw = mainWindow;
                    }
                    else
                    {
                        // If MainWindow is null, search for the MainWindow among all windows
                        foreach (Window window in Application.Current.Windows)
                        {
                            if (window is MainWindow mainWnd)
                            {
                                _mw = mainWnd;
                                break;
                            }
                        }
                    }
                }
                return _mw;
            }
        }

        bool redrawOverride = false;
        int infrequentCount = 0;

#if DEBUG
        // Diagnostic state for autohide. Tracked per-taskbar by index so we
        // only log on transitions (not every 100ms tick). Debug-only so
        // Release/Canary builds don't spend cycles or disk on chatter.
        private readonly Dictionary<int, byte> _lastLoggedOpacity = new();
        private readonly Dictionary<int, bool> _lastLoggedLayeredMissing = new();
        private readonly Dictionary<int, bool> _lastLoggedGLWAFailed = new();
        private bool _loggedAutohideEntered = false;
#endif

        public Background()
        {
            // Reference lookup is now deferred until mw property is accessed
        }

        public void SetMainWindow(MainWindow mainWindow)
        {
            _mw = mainWindow;
        }


        // Main method for the BackgroundWorker - runs indefinitely
        public void DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker worker = sender as BackgroundWorker;
#if DEBUG
            Log.Information("Background worker started");
            int heartbeatCount = 0;
            DateTime lastHeartbeat = DateTime.UtcNow;
#endif
            while (true)
            {
#if DEBUG
                // Heartbeat every ~60s so if the worker hangs or dies we can see the
                // last timestamp. Debug-only so Release logs stay clean.
                heartbeatCount++;
                if (heartbeatCount >= 600)
                {
                    heartbeatCount = 0;
                    var now = DateTime.UtcNow;
                    Log.Debug("Background worker heartbeat (interval={Seconds:0.0}s)", (now - lastHeartbeat).TotalSeconds);
                    lastHeartbeat = now;
                }
#endif
                try
                {
                    if (worker.CancellationPending == true)
                    {
                        e.Cancel = true;
                        break;
                    }

                    // Primary loop for the running process
                    else
                    {
                        // Section for running less important things without requiring an additional thread
                        infrequentCount++;
                        if (infrequentCount == 10)
                        {
                            // Check to see if settings need to be shown
                            List<IntPtr> windowList = Interaction.GetTopLevelWindows();
                            foreach (IntPtr hwnd in windowList)
                            {
                                StringBuilder windowClass = new StringBuilder(1024);
                                StringBuilder windowTitle = new StringBuilder(1024);
                                try
                                {
                                    LocalPInvoke.GetClassName(hwnd, windowClass, 1024);
                                    LocalPInvoke.GetWindowText(hwnd, windowTitle, 1024);

                                    if (windowClass.ToString().Contains("HwndWrapper[RoundedTB.exe") && windowTitle.ToString() == "RoundedTB_SettingsRequest")
                                    {
                                        mw.Dispatcher.Invoke(() =>
                                        {
                                            if (mw.Visibility != Visibility.Visible)
                                            {
                                                mw.ShowMenuItem_Click(null, null);
                                            }
                                        });
                                        LocalPInvoke.SetWindowText(hwnd, "RoundedTB");
                                    }
                                }
                                catch (Exception) { }
                            }

                            ReloadChecker checker = new();
                            mw.taskbarDetails?.ForEach(taskbar =>
                            {
                                if (taskbar.AppListXaml.ReloadRequired)
                                {
                                    taskbar.AppListXaml.ReloadTaskbarFrameElement();
                                    checker.IsReload = true;
                                }
                            });
                            infrequentCount = 0;
                        }

                        // Check if the taskbar is centred, and if it is, directly update the settings; using an interim bool to avoid delaying because I'm lazy
                        bool isCentred = Taskbar.CheckIfCentred();
                        mw.activeSettings.IsCentred = isCentred;

                        // Work with static values to avoid some null reference exceptions
                        List<Types.Taskbar> taskbars = mw.taskbarDetails;
                        Types.Settings settings = mw.activeSettings;

                        // If the number of taskbars has changed, regenerate taskbar information
                        if (Taskbar.TaskbarCountOrHandleChanged(taskbars.Count, taskbars[0].TaskbarHwnd))
                        {
                            // Forcefully reset taskbars if the taskbar count or main taskbar handle has changed
                            taskbars.ForEach(t => t.Dispose());
                            taskbars = Taskbar.GenerateTaskbarInfo(mw.interaction.IsWindows11());
                            Debug.WriteLine("Regenerating taskbar info");
                        }

                        for (int current = 0; current < taskbars.Count; current++)
                        {
                            if (taskbars[current].TaskbarHwnd == IntPtr.Zero || taskbars[current].AppListHwnd == IntPtr.Zero)
                            {
                                taskbars.ForEach(t => t.Dispose());
                                taskbars = Taskbar.GenerateTaskbarInfo(mw.interaction.IsWindows11());
                                Debug.WriteLine("Regenerating taskbar info due to a missing handle");
                                break;
                            }
                            // Get the latest quick details of this taskbar
                            Types.Taskbar newTaskbar = Taskbar.GetQuickTaskbarRects(taskbars[current].TaskbarHwnd, taskbars[current].TrayHwnd, taskbars[current].AppListHwnd, taskbars[current].AppListXaml);


                            // If the taskbar's monitor has a maximised window, reset it so it's "filled"
                            if (Taskbar.TaskbarShouldBeFilled(taskbars[current].TaskbarHwnd, settings))
                            {
                                if (taskbars[current].Ignored == false)
                                {
                                    Taskbar.ResetTaskbar(taskbars[current], settings);
                                    taskbars[current].Ignored = true;
                                }
                                continue;
                            }

                            // Showhide tray on hover
                            if (settings.ShowSegmentsOnHover)
                            {
                                LocalPInvoke.RECT currentTrayRect = taskbars[current].TrayRect;
                                LocalPInvoke.RECT currentWidgetsRect = taskbars[current].TaskbarRect;
                                currentWidgetsRect.Right = Convert.ToInt32(currentWidgetsRect.Right - (currentWidgetsRect.Right - currentWidgetsRect.Left) + (168 * taskbars[current].ScaleFactor));

                                if (currentTrayRect.Left != 0)
                                {
                                    LocalPInvoke.GetCursorPos(out LocalPInvoke.POINT msPt);
                                    bool isHoveringOverTray = LocalPInvoke.PtInRect(ref currentTrayRect, msPt);
                                    bool isHoveringOverWidgets = LocalPInvoke.PtInRect(ref currentWidgetsRect, msPt);
                                    if (isHoveringOverTray && !settings.ShowTray)
                                    {
                                        settings.ShowTray = true;
                                        taskbars[current].Ignored = true;
                                    }
                                    else if (!isHoveringOverTray)
                                    {
                                        taskbars[current].Ignored = true;
                                        settings.ShowTray = false;
                                    }

                                    if (isHoveringOverWidgets && !settings.ShowWidgets)
                                    {
                                        settings.ShowWidgets = true;
                                        taskbars[current].Ignored = true;
                                    }
                                    else if (!isHoveringOverWidgets)
                                    {
                                        taskbars[current].Ignored = true;
                                        settings.ShowWidgets = false;
                                    }
                                }
                            }

                            if (settings.AutoHide > 0)
                            {
                                LocalPInvoke.RECT currentTaskbarRect = taskbars[current].TaskbarRect;
                                LocalPInvoke.GetCursorPos(out LocalPInvoke.POINT msPt);
                                bool isHoveringOverTaskbar;
                                if (taskbars[current].TaskbarHidden)
                                {
                                    currentTaskbarRect.Top = currentTaskbarRect.Bottom - 2;
                                    isHoveringOverTaskbar = LocalPInvoke.PtInRect(ref currentTaskbarRect, msPt);

                                }
                                else
                                {
                                    isHoveringOverTaskbar = LocalPInvoke.PtInRect(ref currentTaskbarRect, msPt);
                                }

                                // AutoHide=2 ("Show on desktop"): force-show when foreground is the
                                // desktop (Progman / WorkerW). Useful for ultrawide setups where
                                // windowed apps/games overlap the taskbar - hides while an app is
                                // focused, reveals automatically when the user returns to desktop.
                                bool forceShow = false;
                                if (settings.AutoHide == 2)
                                {
                                    IntPtr fg = LocalPInvoke.GetForegroundWindow();
                                    if (fg != IntPtr.Zero)
                                    {
                                        StringBuilder fgClass = new StringBuilder(64);
                                        LocalPInvoke.GetClassName(fg, fgClass, fgClass.Capacity);
                                        string c = fgClass.ToString();
                                        if (c == "Progman" || c == "WorkerW")
                                        {
                                            forceShow = true;
                                        }
                                    }
                                }

                                // Configurable hover dwell before revealing while hidden - avoids
                                // accidental reveals when the mouse grazes the bottom edge while
                                // gaming. forceShow (desktop focused) bypasses the delay, and a
                                // dwell of 0 reverts to the original instant-reveal behavior.
                                double revealHoverMs = settings.HoverRevealDelayMs;
                                bool hoverRevealReady = false;
                                if (isHoveringOverTaskbar)
                                {
                                    if (revealHoverMs <= 0)
                                    {
                                        hoverRevealReady = true;
                                    }
                                    else if (taskbars[current].HoverStartedAt == null)
                                    {
                                        taskbars[current].HoverStartedAt = DateTime.UtcNow;
                                    }
                                    else if ((DateTime.UtcNow - taskbars[current].HoverStartedAt.Value).TotalMilliseconds >= revealHoverMs)
                                    {
                                        hoverRevealReady = true;
                                    }
                                }
                                else
                                {
                                    taskbars[current].HoverStartedAt = null;
                                }

                                int animSpeed = 15;
                                byte taskbarOpacity = 0;
                                bool glwaOk = LocalPInvoke.GetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, out _, out taskbarOpacity, out _);

#if DEBUG
                                if (!_loggedAutohideEntered)
                                {
                                    Log.Debug("Autohide block active. AutoHide={Auto} taskbarCount={Count}",
                                        settings.AutoHide, taskbars.Count);
                                    _loggedAutohideEntered = true;
                                }
#endif

                                int exStyle = LocalPInvoke.GetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
                                bool layeredMissing = (exStyle & LocalPInvoke.WS_EX_LAYERED) != LocalPInvoke.WS_EX_LAYERED;

                                // Win11 Explorer occasionally strips WS_EX_LAYERED from the taskbar
                                // (observed after long uptime / DPI or composition changes). Once it's
                                // stripped, GetLayeredWindowAttributes returns false and opacity reads
                                // as 0 forever, so neither the hide (opacity==255) nor reveal
                                // (opacity==1) branch below can fire. Re-apply the style immediately
                                // and prime alpha to 255 to match the actually-visible state; the next
                                // tick picks up from a clean baseline.
                                if (layeredMissing)
                                {
                                    LocalPInvoke.SetWindowLong(
                                        taskbars[current].TaskbarHwnd,
                                        LocalPInvoke.GWL_EXSTYLE,
                                        exStyle | LocalPInvoke.WS_EX_LAYERED);
                                    LocalPInvoke.SetLayeredWindowAttributes(
                                        taskbars[current].TaskbarHwnd, 0, 255, LocalPInvoke.LWA_ALPHA);
                                    taskbars[current].TaskbarHidden = false;
#if DEBUG
                                    if (!_lastLoggedLayeredMissing.GetValueOrDefault(current))
                                    {
                                        Log.Warning(
                                            "Autohide: WS_EX_LAYERED was cleared on taskbar {Idx} (hwnd=0x{Hwnd:X}); re-applied and reset alpha to 255.",
                                            current, taskbars[current].TaskbarHwnd.ToInt64());
                                        _lastLoggedLayeredMissing[current] = true;
                                    }
#endif
                                    continue;
                                }
#if DEBUG
                                else if (_lastLoggedLayeredMissing.GetValueOrDefault(current))
                                {
                                    Log.Information("Autohide: WS_EX_LAYERED stable again on taskbar {Idx}", current);
                                    _lastLoggedLayeredMissing[current] = false;
                                }

                                if (!glwaOk && !_lastLoggedGLWAFailed.GetValueOrDefault(current))
                                {
                                    Log.Warning(
                                        "Autohide: GetLayeredWindowAttributes returned false on taskbar {Idx} (hwnd=0x{Hwnd:X}) even though WS_EX_LAYERED is set. Hide/reveal branches will be skipped this tick.",
                                        current, taskbars[current].TaskbarHwnd.ToInt64());
                                    _lastLoggedGLWAFailed[current] = true;
                                }
                                else if (glwaOk && _lastLoggedGLWAFailed.GetValueOrDefault(current))
                                {
                                    Log.Information("Autohide: GetLayeredWindowAttributes recovered on taskbar {Idx}, opacity={Opacity}", current, taskbarOpacity);
                                    _lastLoggedGLWAFailed[current] = false;
                                }

                                // Flag when opacity is a value the state machine can't act on. At 10Hz
                                // normal values are 0 (uninitialised), 1 (hidden), 255 (visible), and
                                // 63/127/191 only during the ~60ms fade animation. Anything else sitting
                                // for multiple ticks means something external reset the layered state.
                                byte lastOp = _lastLoggedOpacity.GetValueOrDefault(current, (byte)255);
                                bool opacityUnexpected = taskbarOpacity != 0 && taskbarOpacity != 1 && taskbarOpacity != 255
                                                         && taskbarOpacity != 63 && taskbarOpacity != 127 && taskbarOpacity != 191;
                                if (opacityUnexpected && taskbarOpacity != lastOp)
                                {
                                    Log.Warning("Autohide: unexpected opacity {Opacity} on taskbar {Idx}", taskbarOpacity, current);
                                }
                                _lastLoggedOpacity[current] = taskbarOpacity;
#endif

                                // Reveal gate depends on current state:
                                //  - Hidden: require the 1s hover OR force-show to reveal
                                //  - Visible: any hover OR force-show keeps it visible
                                // This prevents the taskbar from fading out mid-hover just because
                                // the 1s dwell threshold hasn't been met yet.
                                bool shouldBeVisible;
                                if (taskbarOpacity == 1)
                                {
                                    shouldBeVisible = hoverRevealReady || forceShow;
                                }
                                else
                                {
                                    shouldBeVisible = isHoveringOverTaskbar || forceShow;
                                }

                                if (shouldBeVisible && taskbarOpacity == 1)
                                {
                                    int style = LocalPInvoke.GetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
                                    if ((style & LocalPInvoke.WS_EX_TRANSPARENT) == LocalPInvoke.WS_EX_TRANSPARENT)
                                    {
                                        LocalPInvoke.SetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE, LocalPInvoke.GetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32() ^ LocalPInvoke.WS_EX_TRANSPARENT);
                                    }
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 63, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 127, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 191, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 255, LocalPInvoke.LWA_ALPHA);
                                    taskbars[current].Ignored = true;
                                    taskbars[current].TaskbarHidden = false;
                                    Debug.WriteLine("MouseOver TB");
#if DEBUG
                                    Log.Debug("Autohide reveal on taskbar {Idx} (forceShow={ForceShow}, hoverReady={Hover})", current, forceShow, hoverRevealReady);
#endif
                                }
                                else if (!shouldBeVisible && taskbarOpacity == 255)
                                {
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 191, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 127, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 63, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 1, LocalPInvoke.LWA_ALPHA);
                                    int style = LocalPInvoke.GetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
                                    if ((style & LocalPInvoke.WS_EX_TRANSPARENT) != LocalPInvoke.WS_EX_TRANSPARENT)
                                    {
                                        LocalPInvoke.SetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE, LocalPInvoke.GetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32() ^ LocalPInvoke.WS_EX_TRANSPARENT);
                                    }
                                    taskbars[current].Ignored = true;
                                    taskbars[current].TaskbarHidden = true;
                                    Debug.WriteLine("MouseOff TB");
#if DEBUG
                                    Log.Debug("Autohide hide on taskbar {Idx} (forceShow={ForceShow}, hover={Hover})", current, forceShow, isHoveringOverTaskbar);
#endif
                                }
                            }
                            else
                            {
                                int animSpeed = 15;
                                byte taskbarOpacity = 0;
                                LocalPInvoke.GetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, out _, out taskbarOpacity, out _);
                                if (taskbarOpacity < 255)
                                {
                                    int style = LocalPInvoke.GetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
                                    if ((style & LocalPInvoke.WS_EX_TRANSPARENT) == LocalPInvoke.WS_EX_TRANSPARENT)
                                    {
                                        LocalPInvoke.SetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE, LocalPInvoke.GetWindowLong(taskbars[current].TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32() ^ LocalPInvoke.WS_EX_TRANSPARENT);
                                    }
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 63, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 127, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 191, LocalPInvoke.LWA_ALPHA);
                                    System.Threading.Thread.Sleep(animSpeed);
                                    LocalPInvoke.SetLayeredWindowAttributes(taskbars[current].TaskbarHwnd, 0, 255, LocalPInvoke.LWA_ALPHA);
                                    taskbars[current].Ignored = true;
                                    taskbars[current].TaskbarHidden = false;
                                }
                            }


                            // If the taskbar's overall rect has changed, update it. If it's simple, just update. If it's dynamic, check it's a valid change, then update it.
                            if (Taskbar.TaskbarRefreshRequired(taskbars[current], newTaskbar, settings.IsDynamic) || taskbars[current].Ignored || redrawOverride)
                            {
                                Debug.WriteLine($"Refresh required on taskbar {current}");
                                taskbars[current].Ignored = false;
                                int isFullTest = newTaskbar.TrayRect.Left - newTaskbar.AppListRect.Right;
                                if (!settings.IsDynamic || (isFullTest <= taskbars[current].ScaleFactor * 25 && isFullTest > 0 && newTaskbar.TrayRect.Left != 0))
                                {
                                    // Add the rect changes to the temporary list of taskbars
                                    taskbars[current].TaskbarRect = newTaskbar.TaskbarRect;
                                    taskbars[current].AppListRect = newTaskbar.AppListRect;
                                    taskbars[current].TrayRect = newTaskbar.TrayRect;
                                    Taskbar.UpdateSimpleTaskbar(taskbars[current], settings);
                                }
                                else
                                {
                                    if (Taskbar.CheckDynamicUpdateIsValid(taskbars[current], newTaskbar))
                                    {
                                        // Add the rect changes to the temporary list of taskbars
                                        taskbars[current].TaskbarRect = newTaskbar.TaskbarRect;
                                        taskbars[current].AppListRect = newTaskbar.AppListRect;
                                        taskbars[current].TrayRect = newTaskbar.TrayRect;
                                        Taskbar.UpdateDynamicTaskbar(taskbars[current], settings);
                                    }
                                }
                            }
                        }
                        mw.taskbarDetails = taskbars;


                        System.Threading.Thread.Sleep(100);
                    }
                }
                catch (Exception ex)
                {
                    // Worker used to die silently on anything except TypeInitializationException.
                    // Log and continue so autohide keeps running; if the same error repeats every
                    // tick it will be obvious in the log.
                    Log.Error(ex, "Background worker iteration threw {Type}", ex.GetType().Name);
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
    }
}
