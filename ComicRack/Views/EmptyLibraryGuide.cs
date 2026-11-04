using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using cYo.Common.Windows.Forms.Theme;
using cYo.Common.Windows.Forms.Theme.Resources;

namespace cYo.Projects.ComicRack.Viewer.Views
{
	// Shown over the library while it has no books: an arrow points at the Downloads tab, with a
	// short how-to, and the user can pick the folder comics are read from and downloads saved to
	public class EmptyLibraryGuide : Control
	{
		private const int CardPadding = 24;

		private const int ArrowSpace = 110;

		private const string TitleText = "Your library is empty";

		private const string DownloadsHeading = "Download comics";

		private const string DownloadsText = "Open the Downloads tab (the arrow points to it), paste the link of a gallery, chapter or series page and click Download. "
			+ "Finished books are saved to your library folder and show up here.";

		private const string FolderHeading = "Use your own comics";

		private const string FolderText = "Choose the folder your comics are kept in. ComicRack reads the comics in it, adds new ones as they appear, "
			+ "and saves downloads there too.";

		private readonly Button btDownloads = new Button { Text = "Open Downloads", AutoSize = true };

		private readonly Button btFolder = new Button { Text = "Choose Library Folder…", AutoSize = true };

		private readonly LinkLabel lnkHide = new LinkLabel { Text = "Not now", AutoSize = true };

		private Font titleFont;

		private Font headingFont;

		private Rectangle card;

		private Rectangle titleBounds;

		private Rectangle downloadsBounds;

		private Rectangle folderBounds;

		private Rectangle pathBounds;

		private Rectangle lastTarget;

		// Screen bounds of the Downloads tab
		public Func<Rectangle> GetTargetBounds { get; set; }

		// The folder downloads are saved to, shown under "Use your own comics"
		public Func<string> GetLibraryFolder { get; set; }

		public event EventHandler OpenDownloads;

		public event EventHandler ChooseFolder;

		public event EventHandler HideGuide;

		public EmptyLibraryGuide()
		{
			SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, value: true);
			btDownloads.Click += (s, e) => OpenDownloads?.Invoke(this, EventArgs.Empty);
			btFolder.Click += (s, e) => ChooseFolder?.Invoke(this, EventArgs.Empty);
			lnkHide.LinkClicked += (s, e) => HideGuide?.Invoke(this, EventArgs.Empty);
			Controls.AddRange(new Control[] { btDownloads, btFolder, lnkHide });
			this.Theme();
			lnkHide.BackColor = Color.Transparent;
			if (IsDark)
			{
				lnkHide.LinkColor = lnkHide.VisitedLinkColor = Color.FromArgb(110, 170, 255);
				lnkHide.ActiveLinkColor = Color.FromArgb(160, 200, 255);
			}
		}

		private Color BackgroundColor => ThemeColors.ItemView.MainBack;

		private bool IsDark => BackgroundColor.GetBrightness() < 0.5f;

		private Color TextColor => IsDark ? Color.FromArgb(230, 230, 230) : Color.FromArgb(30, 30, 30);

		private Color DimTextColor => IsDark ? Color.FromArgb(170, 170, 170) : Color.FromArgb(90, 90, 90);

		private Color CardColor => IsDark ? Color.FromArgb(44, 44, 46) : Color.White;

		private Color CardBorderColor => IsDark ? Color.FromArgb(70, 70, 74) : Color.FromArgb(215, 215, 220);

		private Color AccentColor => IsDark ? Color.FromArgb(110, 170, 255) : SystemColors.Highlight;

		protected override void OnFontChanged(EventArgs e)
		{
			base.OnFontChanged(e);
			titleFont?.Dispose();
			headingFont?.Dispose();
			titleFont = null;
			headingFont = null;
			PerformLayout();
		}

		private Font TitleFont => titleFont ?? (titleFont = new Font(Font.FontFamily, Font.SizeInPoints * 1.7f, FontStyle.Bold, GraphicsUnit.Point));

		private Font HeadingFont => headingFont ?? (headingFont = new Font(Font.FontFamily, Font.SizeInPoints * 1.15f, FontStyle.Bold, GraphicsUnit.Point));

		private const TextFormatFlags WrapFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

		private const TextFormatFlags PathFlags = TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

		private static Rectangle Measure(string text, Font font, int width, TextFormatFlags flags)
		{
			Size size = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), flags);
			return new Rectangle(0, 0, width, size.Height);
		}

		private string PathText => "Library folder: " + (GetLibraryFolder?.Invoke() ?? string.Empty);

		protected override void OnLayout(LayoutEventArgs levent)
		{
			base.OnLayout(levent);
			int width = Math.Min(600, ClientSize.Width - 32);
			if (width < 200)
			{
				return;
			}
			int inner = width - 2 * CardPadding;
			titleBounds = Measure(TitleText, TitleFont, inner, WrapFlags);
			Rectangle h1 = Measure(DownloadsHeading, HeadingFont, inner, WrapFlags);
			downloadsBounds = Measure(DownloadsText, Font, inner, WrapFlags);
			Rectangle h2 = Measure(FolderHeading, HeadingFont, inner, WrapFlags);
			folderBounds = Measure(FolderText, Font, inner, WrapFlags);
			pathBounds = Measure("X", Font, inner, PathFlags);
			int height = CardPadding + titleBounds.Height + 16 + h1.Height + 4 + downloadsBounds.Height + 8 + btDownloads.Height
				+ 20 + h2.Height + 4 + folderBounds.Height + 6 + pathBounds.Height + 8 + btFolder.Height + 16 + lnkHide.Height + CardPadding;
			int top = Math.Max(ArrowSpace, Math.Min((ClientSize.Height - height) / 3, ArrowSpace + 60));
			card = new Rectangle((ClientSize.Width - width) / 2, top, width, height);
			int x = card.Left + CardPadding;
			int y = card.Top + CardPadding;
			titleBounds.Location = new Point(x, y);
			y = titleBounds.Bottom + 16;
			downloadsHeadingTop = y;
			y += h1.Height + 4;
			downloadsBounds.Location = new Point(x, y);
			y = downloadsBounds.Bottom + 8;
			btDownloads.Location = new Point(x, y);
			y = btDownloads.Bottom + 20;
			folderHeadingTop = y;
			y += h2.Height + 4;
			folderBounds.Location = new Point(x, y);
			y = folderBounds.Bottom + 6;
			pathBounds.Location = new Point(x, y);
			y = pathBounds.Bottom + 8;
			btFolder.Location = new Point(x, y);
			y = btFolder.Bottom + 16;
			lnkHide.Location = new Point(x, y);
		}

		private int downloadsHeadingTop;

		private int folderHeadingTop;

		// Repaints when the Downloads tab moved (tabs added or closed, window resized)
		public void UpdateTarget()
		{
			Rectangle target = GetTargetBounds?.Invoke() ?? Rectangle.Empty;
			if (target != lastTarget)
			{
				lastTarget = target;
				Invalidate();
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			g.Clear(BackgroundColor);
			if (card.IsEmpty)
			{
				return;
			}
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (GraphicsPath path = RoundedRectangle(card, 10))
			using (Brush brush = new SolidBrush(CardColor))
			using (Pen pen = new Pen(CardBorderColor))
			{
				g.FillPath(brush, path);
				g.DrawPath(pen, path);
			}
			int x = card.Left + CardPadding;
			TextRenderer.DrawText(g, TitleText, TitleFont, titleBounds, TextColor, WrapFlags);
			DrawHeading(g, DownloadsHeading, x, downloadsHeadingTop);
			TextRenderer.DrawText(g, DownloadsText, Font, downloadsBounds, DimTextColor, WrapFlags);
			DrawHeading(g, FolderHeading, x, folderHeadingTop);
			TextRenderer.DrawText(g, FolderText, Font, folderBounds, DimTextColor, WrapFlags);
			TextRenderer.DrawText(g, PathText, Font, pathBounds, TextColor, PathFlags);
			DrawArrow(g);
		}

		private void DrawHeading(Graphics g, string text, int x, int y)
		{
			TextRenderer.DrawText(g, text, HeadingFont, new Point(x, y), AccentColor, TextFormatFlags.NoPrefix);
		}

		// A curve from the card up to the Downloads tab, ending in an arrow head just below it
		private void DrawArrow(Graphics g)
		{
			Rectangle screen = GetTargetBounds?.Invoke() ?? Rectangle.Empty;
			if (screen.IsEmpty)
			{
				return;
			}
			Rectangle target = RectangleToClient(screen);
			PointF tip = new PointF(target.Left + target.Width / 2f, Math.Max(4, target.Bottom + 6));
			float startX = Math.Max(card.Left + 40, Math.Min(card.Right - 40, tip.X + 90));
			PointF start = new PointF(startX, card.Top - 8);
			float rise = start.Y - tip.Y;
			if (rise < 30)
			{
				return;
			}
			using (Pen pen = new Pen(AccentColor, 3f))
			using (AdjustableArrowCap cap = new AdjustableArrowCap(4.5f, 5f, isFilled: true))
			{
				pen.StartCap = LineCap.Round;
				pen.CustomEndCap = cap;
				g.DrawBezier(pen, start, new PointF(start.X, start.Y - rise * 0.55f), new PointF(tip.X, tip.Y + rise * 0.55f), tip);
			}
		}

		private static GraphicsPath RoundedRectangle(Rectangle r, int radius)
		{
			int d = radius * 2;
			GraphicsPath path = new GraphicsPath();
			path.AddArc(r.Left, r.Top, d, d, 180, 90);
			path.AddArc(r.Right - d - 1, r.Top, d, d, 270, 90);
			path.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
			path.AddArc(r.Left, r.Bottom - d - 1, d, d, 90, 90);
			path.CloseFigure();
			return path;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				titleFont?.Dispose();
				headingFont?.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
