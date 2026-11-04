#define TRACE
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using cYo.Common.Drawing;
using cYo.Common.Mathematics;

namespace cYo.Common.Windows.Forms
{
	public class ScrollControl : Control
	{
		private readonly HScrollBar hScrollbar = new HScrollBar();

		private readonly VScrollBar vScrollBar = new VScrollBar();

		private readonly ThumbStickControl thumbStick = new ThumbStickControl();

		private readonly Timer scrollTimer = new Timer();

		private readonly Timer scrollEndTimer = new Timer();

		private bool hVisible;

		private bool vVisible;

		private ExtendedBorderStyle borderStyle = ExtendedBorderStyle.Flat;

		private AutoScrollMode autoScrollMode = AutoScrollMode.Drag;

		private bool enableStick = true;

		private int dragScrollRegion = 10;

		private int oldValue = -100000;

		private Size virtualSize;

		private bool blockOwnResize;

		private bool blockUpdateScrollbars;

		private Point panStart;

		private Point panStartScrollPosition;

		private Point scrollDelta;

		private const int ScrollEndDelay = 250;

		private int lastScrollOrResize;

		[DefaultValue(ExtendedBorderStyle.Flat)]
		public ExtendedBorderStyle BorderStyle
		{
			get
			{
				return borderStyle;
			}
			set
			{
				if (borderStyle != value)
				{
					borderStyle = value;
					UpdateScrollbars();
					Invalidate();
				}
			}
		}

		[DefaultValue(AutoScrollMode.Drag)]
		public AutoScrollMode AutoScrollMode
		{
			get
			{
				return autoScrollMode;
			}
			set
			{
				autoScrollMode = value;
			}
		}

		[DefaultValue(true)]
		public bool EnableStick
		{
			get
			{
				return enableStick;
			}
			set
			{
				if (enableStick != value)
				{
					enableStick = value;
				}
			}
		}

		[DefaultValue(null)]
		public Cursor PanCursor
		{
			get;
			set;
		}

		[DefaultValue(10)]
		public int DragScrollRegion
		{
			get
			{
				return dragScrollRegion;
			}
			set
			{
				dragScrollRegion = value;
			}
		}

		public override Rectangle DisplayRectangle
		{
			get
			{
				Rectangle result = BorderUtility.AdjustBorder(base.DisplayRectangle, borderStyle);
				result.Width -= (vVisible ? vScrollBar.Width : 0);
				result.Height -= (hVisible ? hScrollbar.Height : 0);
				if (result.Width < 0)
				{
					result.Width = 0;
				}
				if (result.Height < 0)
				{
					result.Height = 0;
				}
				return result;
			}
		}

		public virtual Rectangle ViewRectangle => DisplayRectangle;

		[Browsable(false)]
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		public int ScrollPositionX
		{
			get
			{
				return hScrollbar.Value;
			}
			set
			{
				hScrollbar.Value = value.Clamp(hScrollbar.Minimum, hScrollbar.Maximum - hScrollbar.LargeChange);
			}
		}

		[Browsable(false)]
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		public int ScrollPositionY
		{
			get
			{
				return vScrollBar.Value;
			}
			set
			{
				if (oldValue > value)
				{
					Trace.WriteLine("Scroll Underflow");
				}
				oldValue = value;
				vScrollBar.Value = value.Clamp(vScrollBar.Minimum, vScrollBar.Maximum - vScrollBar.LargeChange);
			}
		}

		[DefaultValue(typeof(Point), "0, 0")]
		public Point ScrollPosition
		{
			get
			{
				return new Point(ScrollPositionX, ScrollPositionY);
			}
			set
			{
				ScrollPositionX = value.X;
				ScrollPositionY = value.Y;
			}
		}

		[DefaultValue(typeof(Size), "0, 0")]
		public Size VirtualSize
		{
			get
			{
				return virtualSize;
			}
			set
			{
				if (!(virtualSize == value))
				{
					virtualSize = value;
					UpdateScrollbars();
				}
			}
		}

		[DefaultValue(16)]
		public virtual int LineHeight
		{
			get;
			set;
		} = 16;


		[DefaultValue(16)]
		public virtual int ColumnWidth
		{
			get;
			set;
		} = 16;


		[DefaultValue(false)]
		public bool ScrollResizeRefresh
		{
			get;
			set;
		}

		public bool InScrollOrResize
		{
			get;
			private set;
		}

		// The whole view is repainted while scrolling or resizing, so it may be drawn in a faster,
		// lower quality (and repainted properly afterwards). Not while scrolling moves pixels: the
		// moved parts were drawn at full quality and the repainted strips must match them.
		public bool InFastPaint
		{
			get;
			private set;
		}

		protected override bool ScaleChildren => false;

		public event EventHandler<AutoScrollEventArgs> AutoScrolling;

		public event EventHandler Scroll;

		public event EventHandler ViewResized;

		public ScrollControl()
		{
			SetStyle(ControlStyles.Selectable, value: true);
			SetStyle(ControlStyles.ResizeRedraw, value: true);
			scrollTimer.Interval = 20;
			scrollTimer.Tick += ScrollTimerTick;
			scrollTimer.Enabled = false;
			scrollEndTimer.Interval = 50;
			scrollEndTimer.Enabled = false;
			scrollEndTimer.Tick += delegate
			{
				if (Environment.TickCount - lastScrollOrResize < ScrollEndDelay)
				{
					return;
				}
				scrollEndTimer.Stop();
				InScrollOrResize = false;
				if (InFastPaint)
				{
					InFastPaint = false;
					Invalidate();
				}
				else if (fixedMarginsMoved)
				{
					Rectangle view = ViewRectangle;
					Padding fixedMargin = FixedContentsMargin;
					Invalidate(Rectangle.FromLTRB(view.Left, view.Top, view.Right, view.Top + fixedMargin.Top));
					Invalidate(Rectangle.FromLTRB(view.Left, view.Bottom - fixedMargin.Bottom, view.Right, view.Bottom));
				}
				fixedMarginsMoved = false;
				OnScrollOrResizeEnded();
			};
			hScrollbar.TabStop = (vScrollBar.TabStop = (thumbStick.TabStop = false));
			hScrollbar.Visible = (vScrollBar.Visible = (thumbStick.Visible = true));
			hVisible = (vVisible = true);
			thumbStick.Sensitivity = new SizeF(32f, 32f);
			thumbStick.Acceleration = 4f;
			hScrollbar.ValueChanged += ScrollValueChanged;
			vScrollBar.ValueChanged += ScrollValueChanged;
			hScrollbar.VisibleChanged += ScrollbarVisibilityChanged;
			vScrollBar.VisibleChanged += ScrollbarVisibilityChanged;
			thumbStick.Scroll += ThumbStickScroll;
			base.Controls.Add(hScrollbar);
			base.Controls.Add(vScrollBar);
			base.Controls.Add(thumbStick);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				hScrollbar.Dispose();
				vScrollBar.Dispose();
				thumbStick.Dispose();
				scrollTimer.Dispose();
				scrollEndTimer.Dispose();
				surface?.Dispose();
				surfaceContext?.Dispose();
				surfaceDirty.Dispose();
			}
			base.Dispose(disposing);
		}

		public Point Translate(Point pt, bool fromClient)
		{
			if (fromClient)
			{
				pt.Offset(ScrollPosition.X, ScrollPosition.Y);
				pt.Offset(-ViewRectangle.X, -ViewRectangle.Y);
			}
			else
			{
				pt.Offset(-ScrollPosition.X, -ScrollPosition.Y);
				pt.Offset(ViewRectangle.X, ViewRectangle.Y);
			}
			return pt;
		}

		public Rectangle Translate(Rectangle rc, bool fromClient)
		{
			rc.Location = Translate(rc.Location, fromClient);
			return rc;
		}

		protected virtual void OnAutoScrolling(AutoScrollEventArgs e)
		{
			this.AutoScrolling?.Invoke(this, e);
		}

		private void ScrollValueChanged(object sender, EventArgs e)
		{
			Point position = ScrollPosition;
			Point delta = new Point(lastScrollPosition.X - position.X, lastScrollPosition.Y - position.Y);
			lastScrollPosition = position;
			if (!ScrollContents(delta))
			{
				if (ScrollResizeRefresh)
				{
					InFastPaint = true;
				}
				Invalidate();
			}
			OnScroll();
		}

		private Point lastScrollPosition;

		private bool fixedMarginsMoved;

		// True when everything drawn in the view moves with the scroll position (no fixed overlays or
		// background image), so scrolling may move the pixels on screen instead of repainting them
		protected virtual bool CanScrollContents => false;

		// Bands along the view's edges with fixed decorations (e.g. an inner shadow): they are
		// repainted instead of moved
		protected virtual Padding FixedContentsMargin => Padding.Empty;

		[StructLayout(LayoutKind.Sequential)]
		private struct RECT
		{
			public int Left, Top, Right, Bottom;
		}

		[DllImport("user32.dll")]
		private static extern int ScrollWindowEx(IntPtr hWnd, int dx, int dy, ref RECT scroll, ref RECT clip, IntPtr updateRegion, IntPtr updateRect, uint flags);

		private const uint SW_INVALIDATE = 0x0002;

		// Vertical scrolling moves the view's pixels and only the uncovered strip is repainted,
		// which keeps big lists smooth; anything else repaints the whole view
		private bool ScrollContents(Point delta)
		{
			Rectangle view = ViewRectangle;
			Padding fixedMargin = FixedContentsMargin;
			Rectangle moving = Rectangle.FromLTRB(view.Left + fixedMargin.Left, view.Top + fixedMargin.Top, view.Right - fixedMargin.Right, view.Bottom - fixedMargin.Bottom);
			if (delta.X != 0 || delta.Y == 0 || !CanScrollContents || !base.IsHandleCreated || !base.Visible || moving.Width <= 0 || moving.Height <= 0 || Math.Abs(delta.Y) >= moving.Height / 2)
			{
				return false;
			}
			if (RetainedPaint)
			{
				if (!ScrollSurface(view, delta.Y))
				{
					return false;
				}
			}
			else
			{
				RECT rc = new RECT { Left = view.Left, Top = view.Top, Right = view.Right, Bottom = view.Bottom };
				// The pending update region is moved along with the pixels
				if (ScrollWindowEx(base.Handle, 0, delta.Y, ref rc, ref rc, IntPtr.Zero, IntPtr.Zero, SW_INVALIDATE) == 0)
				{
					return false;
				}
			}
			if (fixedMargin != Padding.Empty)
			{
				// The band at the edge the list moves out of is repainted with the uncovered strip
				// (one rectangle, so the paint stays small). The bands along the sides move along,
				// they look the same at any height. The band at the other edge carries the moved
				// pixels until scrolling stops and is repainted then.
				if (delta.Y < 0)
				{
					Invalidate(Rectangle.FromLTRB(view.Left, view.Bottom - fixedMargin.Bottom + delta.Y, view.Right, view.Bottom));
				}
				else
				{
					Invalidate(Rectangle.FromLTRB(view.Left, view.Top, view.Right, view.Top + fixedMargin.Top + delta.Y));
				}
				fixedMarginsMoved = true;
			}
			return true;
		}

		#region Retained surface

		// The control is painted into its own off-screen surface, which is copied to the window.
		// Scrolling moves the surface's pixels and paints the uncovered strip into it: nothing is
		// ever read back from the screen. (ScrollWindowEx reads the window's pixels, and once an
		// OpenGL view had been shown in the window those read back black, so the list flashed or
		// stayed black.) What needs painting into the surface is tracked through OnInvalidated;
		// a WM_PAINT for anything else (the window uncovered) only copies the surface again.
		protected bool RetainedPaint { get; set; }

		private BufferedGraphicsContext surfaceContext;

		private BufferedGraphics surface;

		private Size surfaceSize;

		private readonly object surfaceLock = new object();

		private Region surfaceDirty = new Region(Rectangle.Empty);

		private bool screenOnlyInvalidate;

		[StructLayout(LayoutKind.Sequential)]
		private struct PAINTSTRUCT
		{
			public IntPtr hdc;

			public bool fErase;

			public RECT rcPaint;

			public bool fRestore;

			public bool fIncUpdate;

			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
			public byte[] rgbReserved;
		}

		[DllImport("user32.dll")]
		private static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT paint);

		[DllImport("user32.dll")]
		private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT paint);

		[DllImport("user32.dll")]
		private static extern bool ScrollDC(IntPtr hDC, int dx, int dy, ref RECT scroll, ref RECT clip, IntPtr updateRegion, IntPtr updateRect);

		private const int WM_PAINT = 0x000F;

		private void MarkSurfaceDirty(Rectangle rc)
		{
			lock (surfaceLock)
			{
				surfaceDirty.Union(rc);
			}
		}

		protected override void OnInvalidated(InvalidateEventArgs e)
		{
			// (invalidations from other threads always need painting)
			if (RetainedPaint && (!screenOnlyInvalidate || base.InvokeRequired))
			{
				MarkSurfaceDirty(e.InvalidRect);
			}
			base.OnInvalidated(e);
		}

		protected override void OnVisibleChanged(EventArgs e)
		{
			// Whatever changed while hidden may not have been invalidated
			if (RetainedPaint)
			{
				MarkSurfaceDirty(ClientRectangle);
			}
			base.OnVisibleChanged(e);
		}

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == WM_PAINT && RetainedPaint && m.WParam == IntPtr.Zero)
			{
				PaintFromSurface();
				return;
			}
			base.WndProc(ref m);
		}

		private void PaintFromSurface()
		{
			PAINTSTRUCT ps = new PAINTSTRUCT();
			IntPtr hdc = BeginPaint(base.Handle, ref ps);
			try
			{
				Rectangle client = ClientRectangle;
				if (client.Width <= 0 || client.Height <= 0)
				{
					return;
				}
				if (surface == null || surfaceSize != client.Size)
				{
					surface?.Dispose();
					if (surfaceContext == null)
					{
						surfaceContext = new BufferedGraphicsContext();
					}
					surfaceContext.MaximumBuffer = new Size(Math.Max(client.Width, surfaceContext.MaximumBuffer.Width), Math.Max(client.Height, surfaceContext.MaximumBuffer.Height));
					surface = surfaceContext.Allocate(hdc, client);
					surfaceSize = client.Size;
					MarkSurfaceDirty(client);
				}
				Region dirty;
				lock (surfaceLock)
				{
					dirty = surfaceDirty;
					surfaceDirty = new Region(Rectangle.Empty);
				}
				using (dirty)
				{
					Graphics g = surface.Graphics;
					dirty.Intersect(client);
					if (!dirty.IsEmpty(g))
					{
						Rectangle bounds = Rectangle.Ceiling(dirty.GetBounds(g));
						GraphicsState state = g.Save();
						try
						{
							using (PaintEventArgs pe = new PaintEventArgs(g, bounds))
							{
								g.Clip = dirty;
								OnPaintBackground(pe);
								g.Restore(state);
								state = g.Save();
								g.Clip = dirty;
								OnPaint(pe);
							}
						}
						catch (Exception)
						{
							MarkSurfaceDirty(client);
						}
						finally
						{
							g.Restore(state);
						}
					}
				}
				surface.Render(hdc);
			}
			finally
			{
				EndPaint(base.Handle, ref ps);
			}
		}

		// Moves the surface's pixels inside view by dy; areas waiting to be painted move along
		private bool ScrollSurface(Rectangle view, int dy)
		{
			if (surface == null || surfaceSize != ClientSize)
			{
				return false;
			}
			Graphics g = surface.Graphics;
			IntPtr sdc = g.GetHdc();
			try
			{
				RECT rc = new RECT { Left = view.Left, Top = view.Top, Right = view.Right, Bottom = view.Bottom };
				ScrollDC(sdc, 0, dy, ref rc, ref rc, IntPtr.Zero, IntPtr.Zero);
			}
			finally
			{
				g.ReleaseHdc(sdc);
			}
			Rectangle uncovered = dy < 0 ? Rectangle.FromLTRB(view.Left, view.Bottom + dy, view.Right, view.Bottom) : Rectangle.FromLTRB(view.Left, view.Top, view.Right, view.Top + dy);
			lock (surfaceLock)
			{
				using (Region inView = surfaceDirty.Clone())
				{
					inView.Intersect(view);
					surfaceDirty.Exclude(view);
					inView.Translate(0, dy);
					inView.Intersect(view);
					surfaceDirty.Union(inView);
				}
				surfaceDirty.Union(uncovered);
			}
			// The window shows the moved surface on the next paint
			screenOnlyInvalidate = true;
			try
			{
				Invalidate(view);
			}
			finally
			{
				screenOnlyInvalidate = false;
			}
			return true;
		}

		#endregion

		private void ScrollbarVisibilityChanged(object sender, EventArgs e)
		{
			Invalidate();
		}

		private void ThumbStickScroll(object sender, EventArgs e)
		{
			if (vVisible)
			{
				vScrollBar.Value = (vScrollBar.Value + (int)(thumbStick.Movement.Y * (float)vScrollBar.LargeChange * 10f)).Clamp(vScrollBar.Minimum, vScrollBar.Maximum - vScrollBar.LargeChange);
			}
			if (hVisible)
			{
				hScrollbar.Value = (hScrollbar.Value + (int)(thumbStick.Movement.X * (float)hScrollbar.LargeChange * 10f)).Clamp(hScrollbar.Minimum, hScrollbar.Maximum - hScrollbar.LargeChange);
			}
		}

		// Restarting a WinForms timer destroys and recreates its window, so the timer keeps
		// running while scrolling and only checks how long ago the last step was
		private void MarkScrollOrResize()
		{
			InScrollOrResize = true;
			lastScrollOrResize = Environment.TickCount;
			if (!scrollEndTimer.Enabled)
			{
				scrollEndTimer.Start();
			}
		}

		protected virtual void OnScrollOrResizeEnded()
		{
		}

		protected virtual void OnScroll()
		{
			// the moved fixed bands are repainted when scrolling ends
			if (ScrollResizeRefresh || fixedMarginsMoved)
			{
				MarkScrollOrResize();
			}
			this.Scroll?.Invoke(this, EventArgs.Empty);
		}

		protected virtual void OnViewResized()
		{
			if (ScrollResizeRefresh)
			{
				InFastPaint = true;
				MarkScrollOrResize();
			}
			OnResize(EventArgs.Empty);
			this.ViewResized?.Invoke(this, EventArgs.Empty);
		}

		protected virtual bool OnPanHitTest(MouseButtons buttons, Point location)
		{
			return true;
		}

		private void UpdateScrollbars()
		{
			if (blockUpdateScrollbars)
			{
				return;
			}
			blockUpdateScrollbars = true;
			try
			{
				for (int i = 0; i < 2; i++)
				{
					Rectangle viewRectangle = ViewRectangle;
					Rectangle viewRectangle2 = ViewRectangle;
					if (virtualSize.Width <= viewRectangle2.Width)
					{
						hVisible = false;
						hScrollbar.Value = 0;
						hScrollbar.Maximum = int.MaxValue;
					}
					else
					{
						hVisible = true;
						hScrollbar.Minimum = 0;
						hScrollbar.Maximum = virtualSize.Width;
						hScrollbar.LargeChange = viewRectangle2.Width;
						hScrollbar.SmallChange = ColumnWidth;
						hScrollbar.Value = hScrollbar.Value.Clamp(hScrollbar.Minimum, hScrollbar.Maximum - hScrollbar.LargeChange);
					}
					if (virtualSize.Height <= viewRectangle2.Height)
					{
						vVisible = false;
						vScrollBar.Value = 0;
						vScrollBar.Maximum = int.MaxValue;
					}
					else
					{
						vVisible = true;
						vScrollBar.Minimum = 0;
						vScrollBar.Maximum = virtualSize.Height;
						vScrollBar.LargeChange = viewRectangle2.Height;
						vScrollBar.SmallChange = LineHeight;
						vScrollBar.Value = vScrollBar.Value.Clamp(vScrollBar.Minimum, vScrollBar.Maximum - vScrollBar.LargeChange);
					}
					if (viewRectangle == ViewRectangle)
					{
						break;
					}
					blockOwnResize = true;
					try
					{
						OnViewResized();
					}
					finally
					{
						blockOwnResize = false;
					}
				}
			}
			finally
			{
				blockUpdateScrollbars = false;
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			hScrollbar.Visible = hVisible;
			vScrollBar.Visible = vVisible;
			base.OnPaint(e);
			BorderUtility.DrawBorder(e.Graphics, base.DisplayRectangle, borderStyle);
			e.Graphics.IntersectClip(DisplayRectangle);
		}

		protected override void OnLayout(LayoutEventArgs levent)
		{
			base.OnLayout(levent);
			if (blockOwnResize)
			{
				return;
			}
			Rectangle rectangle = BorderUtility.AdjustBorder(base.DisplayRectangle, borderStyle);
			UpdateScrollbars();
			hScrollbar.SetBounds(rectangle.Left, rectangle.Bottom - hScrollbar.Height, rectangle.Width - (vVisible ? vScrollBar.Width : 0), SystemInformation.HorizontalScrollBarHeight);
			vScrollBar.SetBounds(rectangle.Right - vScrollBar.Width, rectangle.Top, SystemInformation.VerticalScrollBarWidth, rectangle.Height - (hVisible ? hScrollbar.Height : 0));
			thumbStick.Width = vScrollBar.Width;
			thumbStick.Height = hScrollbar.Height;
			if (enableStick && (vVisible || hVisible))
			{
				thumbStick.Visible = true;
				thumbStick.Location = new Point(rectangle.Right - thumbStick.Width, rectangle.Bottom - thumbStick.Height);
				if (!vVisible || !hVisible)
				{
					if (vVisible)
					{
						vScrollBar.Height -= thumbStick.Height;
					}
					if (hVisible)
					{
						hScrollbar.Width -= thumbStick.Width;
					}
				}
			}
			else
			{
				thumbStick.Visible = false;
			}
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			Focus();
			switch (autoScrollMode)
			{
			case AutoScrollMode.Pan:
				if (OnPanHitTest(e.Button, e.Location))
				{
					panStartScrollPosition = ScrollPosition;
					panStart = e.Location;
					if (PanCursor != null)
					{
						Cursor.Current = PanCursor;
					}
				}
				break;
			case AutoScrollMode.Drag:
				if (e.Button == MouseButtons.Left)
				{
					scrollDelta = GetDelta(DisplayRectangle, e.Location);
					scrollTimer.Start();
				}
				break;
			}
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			if (panStart.IsEmpty)
			{
				scrollDelta = GetDelta(DisplayRectangle, e.Location);
				return;
			}
			if (PanCursor != null)
			{
				Cursor.Current = PanCursor;
			}
			Point scrollPosition = panStartScrollPosition;
			scrollPosition.Offset(panStart);
			scrollPosition.Offset(-e.X, -e.Y);
			ScrollPosition = scrollPosition;
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			base.OnMouseUp(e);
			panStart = Point.Empty;
			scrollTimer.Stop();
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			base.OnMouseLeave(e);
			scrollTimer.Stop();
		}

		protected override void OnDragOver(DragEventArgs e)
		{
			base.OnDragOver(e);
			scrollDelta = GetDelta(ViewRectangle.Pad(dragScrollRegion), PointToClient(new Point(e.X, e.Y)));
			scrollTimer.Start();
		}

		protected override void OnDragLeave(EventArgs e)
		{
			base.OnDragLeave(e);
			scrollTimer.Stop();
		}

		private static Point GetDelta(Rectangle rc, Point pos)
		{
			Point empty = Point.Empty;
			if (pos.X < rc.Left)
			{
				empty.X = pos.X - rc.Left;
			}
			else if (pos.X > rc.Right)
			{
				empty.X = pos.X - rc.Right;
			}
			if (pos.Y < rc.Top)
			{
				empty.Y = pos.Y - rc.Top;
			}
			else if (pos.Y > rc.Bottom)
			{
				empty.Y = pos.Y - rc.Bottom;
			}
			return empty;
		}

		private void ScrollTimerTick(object sender, EventArgs e)
		{
			Point scrollPosition = ScrollPosition;
			Point delta = scrollDelta;
			int num = Math.Sign(delta.X);
			int num2 = Math.Sign(delta.Y);
			delta.X = Math.Abs(delta.X);
			delta.Y = Math.Abs(delta.Y);
			if (delta.X > 10)
			{
				delta.X = 10 + (delta.X - 10) / 10;
			}
			if (delta.Y > 10)
			{
				delta.Y = 10 + (delta.Y - 10) / 10;
			}
			delta.X *= num;
			delta.Y *= num2;
			AutoScrollEventArgs autoScrollEventArgs = new AutoScrollEventArgs
			{
				Delta = delta
			};
			OnAutoScrolling(autoScrollEventArgs);
			if (!autoScrollEventArgs.Cancel)
			{
				scrollPosition.Offset(autoScrollEventArgs.Delta);
				ScrollPosition = scrollPosition;
			}
		}
	}
}
