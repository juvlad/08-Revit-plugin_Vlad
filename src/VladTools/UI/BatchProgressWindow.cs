using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VladTools.UI
{
    /// <summary>
    /// How far a batch run has got: which model of how many, how long it has been at it, what became of
    /// the last one — and "Stop after this model". Written for "Export IFC"; any batch that goes model by
    /// model can show it the same way.
    ///
    /// **It runs on the thread the command runs on, because the Revit API has only that one.** The window is
    /// shown modeless, and the command calls <see cref="Begin"/> and <see cref="Done"/> between models; each
    /// call lets WPF draw what changed and take a click (<see cref="Refresh"/>). While a model is being opened
    /// or exported the thread belongs to Revit, so the window stands still then — the hint in it says so — and
    /// a click on "Stop" is taken at the next model, which is exactly what the button promises.
    ///
    /// **Revit's own window is disabled for as long as this one is open**, the way any modal dialog disables
    /// its owner. Letting WPF draw means letting the thread's messages through, Revit's included, and a click
    /// on the ribbon in the middle of a batch must not start another command. <see cref="Dispose"/> enables it
    /// again — in a <c>using</c>, whatever happens to the run.
    ///
    /// The window knows nothing about the Revit API: its owner is a window handle.
    /// </summary>
    internal sealed class BatchProgressWindow : Window, IDisposable
    {
        private readonly IntPtr _owner;
        private readonly int _total;
        private readonly string _title;

        private readonly TextBlock _current;
        private readonly ProgressBar _bar;
        private readonly TextBlock _counts;
        private readonly TextBlock _times;
        private readonly TextBlock _last;
        private readonly Button _stopButton;
        private readonly DispatcherTimer _timer;

        private readonly Stopwatch _run = Stopwatch.StartNew();
        private readonly Stopwatch _item = new Stopwatch();

        private int _done;
        private int _failed;
        private bool _ownerDisabled;
        private bool _finished;

        /// <summary>"Stop after this model" was pressed (or the window's close button): take no further model.</summary>
        public bool StopRequested { get; private set; }

        private BatchProgressWindow(IntPtr owner, string title, int total)
        {
            _owner = owner;
            _title = title ?? string.Empty;
            _total = Math.Max(0, total);

            Title = _title;
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SnapsToDevicePixels = true;

            _current = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 2)
            };

            var hint = new TextBlock
            {
                Text = "Revit is busy with this model and this window stands still until it is done; it comes back to life " +
                       "between models.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 0, 0, 8)
            };

            _bar = new ProgressBar { Height = 18, Minimum = 0, Maximum = Math.Max(1, _total), Margin = new Thickness(0, 0, 0, 8) };

            _counts = new TextBlock { Margin = new Thickness(0, 0, 0, 2) };
            _times = new TextBlock { Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(0, 0, 0, 6) };
            _last = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };

            _stopButton = new Button
            {
                Content = "Stop after this model",
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "The model being worked on now is finished; the rest are not started.\n" +
                          "A model cannot be stopped halfway — Revit does not let go of it until it is done."
            };
            _stopButton.Click += (s, e) => RequestStop();

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(_current);
            panel.Children.Add(hint);
            panel.Children.Add(_bar);
            panel.Children.Add(_counts);
            panel.Children.Add(_times);
            panel.Children.Add(_last);
            panel.Children.Add(_stopButton);
            Content = panel;

            // Ticks only when the thread's messages are let through — between models, or whenever Revit lets
            // them through itself while it works. It touches nothing but this window's text.
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => UpdateTimes();

            UpdateCounts();
            UpdateTimes();
        }

        /// <summary>Shows the window over Revit's and disables Revit's until <see cref="Dispose"/>.</summary>
        public static BatchProgressWindow Start(IntPtr owner, string title, int total)
        {
            var window = new BatchProgressWindow(owner, title, total);

            if (owner != IntPtr.Zero)
                new WindowInteropHelper(window).Owner = owner;

            window.Show();

            if (owner != IntPtr.Zero && IsWindowEnabled(owner))
                window._ownerDisabled = !EnableWindow(owner, false);

            window._timer.Start();
            window.Refresh();
            return window;
        }

        /// <summary>The model at <paramref name="index"/> (from 0) is about to be worked on.</summary>
        public void Begin(int index, string name)
        {
            Title = _title + " — " + (index + 1) + " of " + _total;
            _current.Text = (index + 1) + " of " + _total + ":  " + (name ?? string.Empty);
            _item.Restart();
            UpdateTimes();
            Refresh();
        }

        /// <summary>The model is finished — <paramref name="success"/> or not — and <paramref name="line"/> says how.</summary>
        public void Done(bool success, string line)
        {
            _item.Stop();

            if (success)
                _done++;
            else
                _failed++;

            _last.Text = "Last: " + (line ?? string.Empty);
            _last.Foreground = success ? Brushes.DarkGreen : Brushes.Firebrick;

            UpdateCounts();
            UpdateTimes();
            Refresh();
        }

        /// <summary>Stops the clock, enables Revit's window again and closes this one.</summary>
        public void Dispose()
        {
            if (_finished)
                return;

            _finished = true;
            _timer.Stop();

            // Revit's window first: closing an owned window hands the focus to its owner, and a disabled
            // owner cannot take it.
            if (_ownerDisabled)
                EnableWindow(_owner, true);

            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // The close button means "stop", not "hide the progress and keep Revit locked behind nothing".
            if (!_finished)
            {
                e.Cancel = true;
                RequestStop();
            }

            base.OnClosing(e);
        }

        private void RequestStop()
        {
            if (StopRequested)
                return;

            StopRequested = true;
            _stopButton.IsEnabled = false;
            _stopButton.Content = "Stopping after this model…";
        }

        private void UpdateCounts()
        {
            _bar.Value = Math.Min(_bar.Maximum, _done + _failed);
            _counts.Text = "Done: " + _done + "    not done: " + _failed + "    left: " + Math.Max(0, _total - _done - _failed);
        }

        private void UpdateTimes()
        {
            _times.Text = (_item.IsRunning ? "This model: " + Clock(_item.Elapsed) + "    " : string.Empty) +
                          "The whole run: " + Clock(_run.Elapsed);
        }

        private static string Clock(TimeSpan time)
        {
            return time.TotalHours >= 1
                ? (int)time.TotalHours + ":" + time.Minutes.ToString("00") + ":" + time.Seconds.ToString("00")
                : time.Minutes + ":" + time.Seconds.ToString("00");
        }

        /// <summary>
        /// Lets WPF lay out and draw what changed, and take a click on "Stop": everything queued above
        /// background priority runs before the empty action does.
        /// </summary>
        private void Refresh()
        {
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnableWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool enable);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowEnabled(IntPtr window);
    }
}
