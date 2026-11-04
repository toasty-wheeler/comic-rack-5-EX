using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Timers;
using cYo.Common.Collections;
using cYo.Common.ComponentModel;
using cYo.Common.Drawing;
using cYo.Common.Mathematics;
using cYo.Common.Runtime;
using cYo.Common.Threading;
using cYo.Common.Windows.Forms;
using cYo.Common.Windows.Forms.Theme.Resources;
using cYo.Projects.ComicRack.Engine.Drawing;
using cYo.Projects.ComicRack.Engine.IO;
using cYo.Projects.ComicRack.Viewer.Properties;

namespace cYo.Projects.ComicRack.Viewer.Controls
{
	public abstract class ThumbnailViewItem : ItemViewItem, IDisposable
	{
		private class ClickRegion
		{
			public Rectangle Bounds;

			public Action<Rectangle, Point> Click;
		}

		public const int RowColumnBorder = 2;

		public const int FadeInTime = 300;

		public const int FadeInSteps = 25;

		public const float FontHeightPercent = 0.07f;

		public const float MinFontSize = 0.8f;

		public const float MaxFontSize = 1f;

		public static readonly Bitmap DeletedStateImage = Resources.Deleted;

		private volatile float opacity = 1f;

		private Size border = new Size(4, 4);

		private volatile bool disposed;

		private static Timer animationTimer;

		private static readonly LinkedList<ThumbnailViewItem> animatedItems = new LinkedList<ThumbnailViewItem>();

		private static long animationTime;

		private bool validImage = true;

		private List<ClickRegion> clickRegions;

		public float Opacity
		{
			get
			{
				return opacity;
			}
			set
			{
				value = value.Clamp(0f, 1f);
				if (!opacity.CompareTo(value, 0.01f))
				{
					opacity = value;
					Update();
				}
			}
		}

		public Size Border
		{
			get
			{
				return border;
			}
			set
			{
				if (!(border == value))
				{
					border = value;
					Update(sizeChanged: true);
				}
			}
		}

		public abstract ThumbnailKey ThumbnailKey
		{
			get;
		}

		public bool IsDisposed => disposed;

		~ThumbnailViewItem()
		{
			Dispose(disposing: false);
		}

		public void RefreshImage()
		{
			OnRefreshImage();
			Update(sizeChanged: true);
		}

		protected IItemLock<ThumbnailImage> GetThumbnail(ThumbnailKey key, bool memoryOnly)
		{
			IItemLock<ThumbnailImage> image = Program.ImagePool.Thumbs.GetImage(key, memoryOnly);
			if (image == null)
			{
				Program.ImagePool.AddThumbToQueue(key, base.View, delegate
				{
					MakePageThumbnail(key);
				});
			}
			return image;
		}

		public IItemLock<ThumbnailImage> GetThumbnail(bool memoryOnly)
		{
			return GetThumbnail(ThumbnailKey, memoryOnly);
		}

		public IItemLock<ThumbnailImage> GetThumbnail(ItemDrawInformation drawInfo)
		{
			if (drawInfo != null && drawInfo.DisplayType == ItemViewMode.Detail)
			{
				if (drawInfo.Header == null)
				{
					return null;
				}
				ComicListField comicListField = drawInfo.Header.Tag as ComicListField;
				if (comicListField != null && comicListField.DisplayProperty != "Cover" && comicListField.DisplayProperty != "Thumbnail")
				{
					return null;
				}
			}
			return GetThumbnail(memoryOnly: true);
		}

		// While the list scrolls, a cover that isn't decoded at this size is decoded on the thumbnail
		// queue; until then the size already in memory is drawn scaled (or the placeholder if none)
		public Image GetThumbnailImage(IItemLock<ThumbnailImage> itemLock, int height)
		{
			ThumbnailImage thumbnail = itemLock?.Item;
			if (thumbnail == null)
			{
				return null;
			}
			ItemView view = base.View;
			if (view == null || !view.InScrollOrResize || !view.IsHandleCreated || thumbnail.IsDecoded(height))
			{
				return thumbnail.GetThumbnail(height);
			}
			ThumbnailKey key = ThumbnailKey;
			Program.ImagePool.AddThumbToQueue(key, view, delegate
			{
				DecodeInBackground(key, height);
			});
			Bitmap decoded = thumbnail.DecodedThumbnail;
			decodePending = decoded == null;
			return decoded;
		}

		// The cover exists and is only being decoded: no fade-in when it shows up
		private bool decodePending;

		private void DecodeInBackground(ThumbnailKey key, int height)
		{
			ItemView view = base.View;
			if (disposed || view == null)
			{
				return;
			}
			using (IItemLock<ThumbnailImage> itemLock = Program.ImagePool.Thumbs.GetImage(key, memoryOnly: true))
			{
				ThumbnailImage thumbnail = itemLock?.Item;
				if (thumbnail == null || thumbnail.IsDecoded(height))
				{
					return;
				}
				int decodeHeight = ThumbnailImage.GetDecodeHeight(height);
				Bitmap bitmap = thumbnail.DecodeThumbnail(decodeHeight);
				if (bitmap == null)
				{
					return;
				}
				// The bitmap the paint may be drawing is swapped on the UI thread
				try
				{
					view.BeginInvoke((Action)delegate
					{
						if (thumbnail.IsDisposed || thumbnail.IsDecoded(height))
						{
							bitmap.Dispose();
							return;
						}
						thumbnail.SetDecodedThumbnail(bitmap, decodeHeight);
						Update();
					});
				}
				catch (InvalidOperationException)
				{
					bitmap.Dispose();
				}
			}
		}

		public const int GrowTime = 120;

		private static readonly List<ThumbnailViewItem> growingItems = new List<ThumbnailViewItem>();

		private static System.Windows.Forms.Timer growTimer;

		private static readonly System.Diagnostics.Stopwatch growClock = System.Diagnostics.Stopwatch.StartNew();

		private static long lastGrowTick;

		private float grow;

		private bool growTarget;

		private bool growDrawn;

		// Hover/selection growth for the current paint (0 to 1). A change of target is animated;
		// the first paint starts at the target, so selected covers don't all grow when a list shows.
		protected float GetGrow(bool grown)
		{
			growTarget = grown;
			float target = grown ? 1f : 0f;
			if (!growDrawn)
			{
				growDrawn = true;
				grow = target;
			}
			else if (grow != target && !growingItems.Contains(this))
			{
				growingItems.Add(this);
				if (growTimer == null)
				{
					growTimer = new System.Windows.Forms.Timer { Interval = 10 };
					growTimer.Tick += GrowTimer_Tick;
				}
				if (!growTimer.Enabled)
				{
					lastGrowTick = growClock.ElapsedMilliseconds;
					growTimer.Start();
				}
			}
			return grow;
		}

		private static void GrowTimer_Tick(object sender, EventArgs e)
		{
			long now = growClock.ElapsedMilliseconds;
			float step = Math.Max(1, now - lastGrowTick) / (float)GrowTime;
			lastGrowTick = now;
			for (int i = growingItems.Count - 1; i >= 0; i--)
			{
				ThumbnailViewItem item = growingItems[i];
				if (item.disposed || item.View == null)
				{
					growingItems.RemoveAt(i);
					continue;
				}
				float target = item.growTarget ? 1f : 0f;
				item.grow = target > item.grow ? Math.Min(target, item.grow + step) : Math.Max(target, item.grow - step);
				if (item.grow == target)
				{
					growingItems.RemoveAt(i);
				}
				item.Update();
			}
			if (growingItems.Count == 0)
			{
				growTimer.Stop();
			}
		}

		private static void AddAnimation(ThumbnailViewItem item)
		{
			if (animationTimer == null)
			{
				animationTimer = new Timer(12.0)
				{
					AutoReset = true
				};
				animationTimer.Elapsed += animationTimer_Elapsed;
			}
			using (ItemMonitor.Lock(animatedItems))
			{
				if (!(item.Opacity >= 0.95f) && !animatedItems.Contains(item))
				{
					animatedItems.AddLast(item);
					if (!animationTimer.Enabled)
					{
						animationTime = 0L;
						animationTimer.Start();
					}
				}
			}
		}

		private static void animationTimer_Elapsed(object sender, ElapsedEventArgs e)
		{
			using (ItemMonitor.Lock(animatedItems))
			{
				long ticks = Machine.Ticks;
				long num = ((animationTime == 0L) ? 12 : (ticks - animationTime));
				animationTime = ticks;
				LinkedListNode<ThumbnailViewItem> linkedListNode = animatedItems.First;
				bool flag = false;
				while (linkedListNode != null)
				{
					LinkedListNode<ThumbnailViewItem> next = linkedListNode.Next;
					try
					{
						ThumbnailViewItem value = linkedListNode.Value;
						if (value.View == null || value.disposed)
						{
							animatedItems.Remove(linkedListNode);
							continue;
						}
						value.Opacity += (float)num / 300f;
						if (value.Opacity >= 1f)
						{
							animatedItems.Remove(linkedListNode);
						}
						else
						{
							flag = true;
						}
					}
					catch
					{
						animatedItems.Remove(linkedListNode);
					}
					finally
					{
						linkedListNode = next;
					}
				}
				if (!flag)
				{
					animationTimer.Stop();
				}
			}
		}

		public void Animate(Image image)
		{
			if (!Program.Settings.FadeInThumbnails)
			{
				return;
			}
			if (image == null)
			{
				if (!decodePending)
				{
					validImage = false;
				}
				decodePending = false;
				return;
			}
			decodePending = false;
			// No fade while the list scrolls: covers arrive by the dozen then, and drawing them
			// translucent every animation step took hundreds of milliseconds per frame
			if (!validImage && base.View?.InScrollOrResize != true)
			{
				opacity = 0f;
				AddAnimation(this);
			}
			validImage = true;
		}

		protected virtual Size GetDefaultMaximumSize(Size defaultSize)
		{
			int height = defaultSize.Height;
			return new Size(height * 2, height);
		}

		protected virtual Size GetEstimatedSize(Size canvasSize)
		{
			return Size.Empty;
		}

		protected Size GetThumbnailSizeSafe(Size defaultSize)
		{
			using (IItemLock<ThumbnailImage> itemLock = Program.ImagePool.Thumbs.GetImage(ThumbnailKey, memoryOnly: true))
			{
				Size imageSize = itemLock?.Item.GetThumbnailSize(defaultSize.Height) ?? Size.Empty;
				Size defaultMaximumSize = GetDefaultMaximumSize(defaultSize);
				if (imageSize.IsEmpty)
				{
					Size estimatedSize = GetEstimatedSize(defaultMaximumSize);
					if (!estimatedSize.IsEmpty)
					{
						return estimatedSize;
					}
				}
				return ThumbRenderer.GetSafeScaledImageSize(imageSize, defaultMaximumSize);
			}
		}

		protected Size AddBorder(Size size)
		{
			return size + border + border;
		}

		public override void OnDraw(ItemDrawInformation drawInfo)
		{
			base.OnDraw(drawInfo);
			if (drawInfo.SubItem < 0)
			{
				clickRegions = null;
				return;
			}
			Rectangle bounds = drawInfo.Bounds;
			if (drawInfo.DrawBorder)
				drawInfo.Graphics.DrawLine(ThemePens.ThumbnailViewItem.Border, bounds.Location, new Point(bounds.Left, bounds.Bottom));
		}

		protected void MakePageThumbnail(ThumbnailKey key)
		{
			MakePageThumbnail(key, updateSize: true);
		}

		protected void MakePageThumbnail(ThumbnailKey key, bool updateSize)
		{
			if (!disposed)
			{
				updateSize &= base.View.ItemViewMode == ItemViewMode.Thumbnail;
				if (!Program.ImagePool.Thumbs.IsAvailable(key))
				{
					CreateThumbnail(key);
				}
				else
				{
					updateSize = false;
				}
				if (base.View != null)
				{
					Update(updateSize);
				}
			}
		}

		protected abstract void CreateThumbnail(ThumbnailKey key);

		protected virtual void OnRefreshImage()
		{
			Program.ImagePool.Pages.RefreshImage(new PageKey(ThumbnailKey));
			Program.ImagePool.Thumbs.RefreshImage(ThumbnailKey);
		}

		protected void AddClickRegion(Rectangle bounds, Action<Rectangle, Point> click)
		{
			clickRegions = clickRegions.SafeAdd(new ClickRegion
			{
				Bounds = bounds,
				Click = click
			});
		}

		public override bool OnClick(Point pt)
		{
			ClickRegion clickRegion = clickRegions?.FirstOrDefault((ClickRegion c) => c.Bounds.Contains(pt));
			if (clickRegion == null)
			{
				return false;
			}
			clickRegion.Click(clickRegion.Bounds, pt.Subtract(clickRegion.Bounds.Location));
			return true;
		}

		public void Dispose()
		{
			if (!disposed)
			{
				try
				{
					Dispose(disposing: true);
					GC.SuppressFinalize(this);
				}
				finally
				{
					disposed = true;
				}
			}
		}

		protected virtual void Dispose(bool disposing)
		{
		}
	}
}
