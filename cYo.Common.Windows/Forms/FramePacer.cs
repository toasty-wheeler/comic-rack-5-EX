using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace cYo.Common.Windows.Forms
{
	// Raises Tick on the control's thread once per display refresh (paced by the desktop compositor),
	// so animations run at the monitor's refresh rate. WinForms timers tick at ~64 Hz at best and
	// are starved by painting.
	public class FramePacer : IDisposable
	{
		private readonly Control control;

		private readonly AutoResetEvent wake = new AutoResetEvent(false);

		private Thread thread;

		private volatile bool running;

		private volatile bool disposed;

		private int tickPending;

		private bool timerResolutionSet;

		public bool Enabled => running;

		public event EventHandler Tick;

		public FramePacer(Control control)
		{
			this.control = control;
		}

		[DllImport("dwmapi.dll")]
		private static extern int DwmFlush();

		[DllImport("winmm.dll")]
		private static extern int timeBeginPeriod(int period);

		[DllImport("winmm.dll")]
		private static extern int timeEndPeriod(int period);

		public void Start()
		{
			if (running || disposed)
			{
				return;
			}
			if (!timerResolutionSet)
			{
				// Only for the fallback sleeps, while animating
				timeBeginPeriod(1);
				timerResolutionSet = true;
			}
			running = true;
			if (thread == null)
			{
				thread = new Thread(Loop)
				{
					IsBackground = true,
					Name = "FramePacer",
					Priority = ThreadPriority.AboveNormal
				};
				thread.Start();
			}
			wake.Set();
		}

		public void Stop()
		{
			running = false;
			if (timerResolutionSet)
			{
				timeEndPeriod(1);
				timerResolutionSet = false;
			}
		}

		private void Loop()
		{
			while (!disposed)
			{
				if (!running)
				{
					wake.WaitOne();
					continue;
				}
				WaitForFrame();
				if (running && !disposed && Interlocked.Exchange(ref tickPending, 1) == 0)
				{
					try
					{
						control.BeginInvoke((Action)OnTick);
					}
					catch (InvalidOperationException)
					{
						tickPending = 0;
						running = false;
					}
				}
			}
		}

		private static void WaitForFrame()
		{
			long start = Stopwatch.GetTimestamp();
			int result;
			try
			{
				result = DwmFlush();
			}
			catch (DllNotFoundException)
			{
				result = -1;
			}
			// Without composition, or with nothing to present, DwmFlush returns at once
			if (result != 0 || Stopwatch.GetTimestamp() - start < Stopwatch.Frequency / 2000)
			{
				Thread.Sleep(4);
			}
		}

		private void OnTick()
		{
			tickPending = 0;
			if (running && !disposed)
			{
				Tick?.Invoke(this, EventArgs.Empty);
			}
		}

		public void Dispose()
		{
			Stop();
			disposed = true;
			wake.Set();
		}
	}
}
