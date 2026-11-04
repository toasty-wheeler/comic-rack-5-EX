using System.Drawing;
using cYo.Common.Drawing;
using cYo.Common.Windows.Forms;

namespace cYo.Projects.ComicRack.Engine.Drawing
{
	public class ThumbIconRenderer : ViewItemRenderer
	{
		public int TextHeight
		{
			get;
			set;
		}

		// Hover/selection growth, 0 (normal) to 1 (grown); negative turns it off. Covers are drawn a
		// bit smaller than their space and grow into all of it (border included), anchored at the
		// bottom, so a grown cover never paints outside its item.
		public float Grow
		{
			get;
			set;
		} = -1f;

		public const float NormalScale = 0.94f;

		private Rectangle GetGrownBounds(Rectangle area)
		{
			if (Grow < 0f)
			{
				return area;
			}
			Rectangle full = Rectangle.FromLTRB(area.Left - base.Border.Width, area.Top - base.Border.Height, area.Right + base.Border.Width, area.Bottom);
			int w = (int)(area.Width * NormalScale);
			int h = (int)(area.Height * NormalScale);
			Rectangle normal = new Rectangle(area.Left + (area.Width - w) / 2, area.Bottom - h, w, h);
			float g = Grow > 1f ? 1f : Grow;
			return Rectangle.FromLTRB(Lerp(normal.Left, full.Left, g), Lerp(normal.Top, full.Top, g), Lerp(normal.Right, full.Right, g), Lerp(normal.Bottom, full.Bottom, g));
		}

		private static int Lerp(int a, int b, float t)
		{
			return a + (int)((b - a) * t + 0.5f);
		}

		public ThumbIconRenderer(Image image, ThumbnailDrawingOptions flags)
		{
			TextHeight = 45;
			base.Image = image;
			base.Options = flags;
		}

		public Rectangle Draw(Graphics graphics, Rectangle bounds)
		{
			Rectangle rectangle = bounds;
			rectangle.Inflate(-base.Border.Width, -base.Border.Height);
			Rectangle rectangle2 = rectangle;
			rectangle2.Height -= TextHeight;
			Point location = new Point(rectangle2.Left, rectangle2.Top);
			Rectangle rectangle3 = DrawThumbnail(graphics, GetGrownBounds(new Rectangle(location, rectangle2.Size)));
			if (base.TextLines.Count == 0)
			{
				return rectangle3;
			}
			Rectangle rectangle4 = new Rectangle(rectangle.X, rectangle2.Bottom, rectangle.Width, TextHeight);
			Rectangle rect = rectangle4;
			rect.Inflate(-2, -2);
			if (base.Selected || base.Hot || base.Focused)
			{
				Rectangle rc = SimpleTextRenderer.MeasureText(graphics, base.TextLines, rect);
				rc.Inflate(2, 2);
				rc.Intersect(rectangle4);
				graphics.DrawStyledRectangle(rc, base.SelectionAlphaState, base.SelectionBackColor);
			}
			using (graphics.Fast())
			{
				return Rectangle.Union(rectangle3, SimpleTextRenderer.DrawText(graphics, base.TextLines, rect));
			}
		}
	}
}
