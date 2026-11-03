using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using cYo.Common.Windows.Forms.Theme;
using cYo.Projects.ComicRack.Downloader;

namespace cYo.Projects.ComicRack.Viewer.Views
{
	// "Downloads" tab: paste a gallery/series URL and it is downloaded with the HDoujin Downloader
	// site modules, packed into the chosen format and added to the library.
	public class DownloaderView : SubView
	{
		private static DownloadManager manager;

		private readonly Dictionary<DownloadJob, ListViewItem> items = new Dictionary<DownloadJob, ListViewItem>();

		private readonly TextBox txtUrl = new TextBox();

		private readonly Button btPaste = new Button();

		private readonly Button btDownload = new Button();

		private readonly ComboBox cbFormat = new ComboBox();

		private readonly TextBox txtFolder = new TextBox();

		private readonly Button btBrowse = new Button();

		private readonly CheckBox chkAddToLibrary = new CheckBox();

		private readonly CheckBox chkOpen = new CheckBox();

		private readonly CheckBox chkChapters = new CheckBox();

		private readonly CheckBox chkClipboard = new CheckBox();

		private readonly LinkLabel lnkSites = new LinkLabel();

		private readonly LinkLabel lnkUpdate = new LinkLabel();

		private readonly LinkLabel lnkExtension = new LinkLabel();

		private readonly ListView lvJobs = new ListView();

		private readonly TextBox txtLog = new TextBox();

		private readonly ContextMenuStrip jobMenu = new ContextMenuStrip();

		private readonly Timer clipboardTimer = new Timer { Interval = 1000 };

		private readonly Timer logTimer = new Timer { Interval = 500 };

		private string lastClipboard;

		private static readonly (OutputFormat Format, string Text)[] formats =
		{
			(OutputFormat.Cbz, "CBZ - Comic Book ZIP (recommended)"),
			(OutputFormat.Cb7, "CB7 - Comic Book 7-Zip"),
			(OutputFormat.Cbt, "CBT - Comic Book TAR"),
			(OutputFormat.Pdf, "PDF Document"),
			(OutputFormat.Folder, "Image folder")
		};

		public static string UserModulesFolder => Path.Combine(Program.Paths.ApplicationDataPath, "DownloaderModules");

		public static string BundledModulesFolder => Path.Combine(Application.StartupPath, "DownloaderModules");

		public static string BrowserExtensionFolder => Path.Combine(Application.StartupPath, "BrowserExtension");

		private static BrowserBridge browserBridge;

		public static DownloadManager Manager
		{
			get
			{
				if (manager == null)
				{
					manager = new DownloadManager(() => new ModuleCatalog(UserModulesFolder, BundledModulesFolder));
					// Handled here rather than by the view, which may never be opened (browser downloads)
					manager.JobCompleted += Manager_JobCompleted;
				}
				return manager;
			}
		}

		public static string DefaultOutputFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Comics", "Downloads");

		// Download options as last chosen on the Downloads tab
		public static DownloadOptions OptionsFromSettings()
		{
			string folder = Program.Settings.DownloaderOutputFolder;
			return new DownloadOptions
			{
				OutputFolder = string.IsNullOrEmpty(folder) ? DefaultOutputFolder : folder,
				Format = Enum.TryParse(Program.Settings.DownloaderFormat, true, out OutputFormat f) ? f : OutputFormat.Cbz
			};
		}

		// Lets the ComicRack browser extension send pages to ComicRack (127.0.0.1 only)
		public static void StartBrowserBridge()
		{
			if (browserBridge != null)
			{
				return;
			}
			try
			{
				BrowserBridge bridge = new BrowserBridge(Manager, OptionsFromSettings)
				{
					AppVersion = Application.ProductVersion
				};
				bridge.Start();
				browserBridge = bridge;
			}
			catch (Exception)
			{
				// Port in use (e.g. a second copy of ComicRack); the extension shows ComicRack as not running
			}
		}

		// The controls are built the first time the tab is shown, so the tab costs nothing at startup
		private bool initialized;

		public DownloaderView()
		{
			Text = "Downloads";
		}

		private void BuildLayout()
		{
			SuspendLayout();
			Padding = new Padding(8);

			TableLayoutPanel top = new TableLayoutPanel
			{
				Dock = DockStyle.Top,
				AutoSize = true,
				ColumnCount = 5,
				RowCount = 3,
				Padding = new Padding(0, 0, 0, 6)
			};
			top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));
			top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60f));
			top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

			top.Controls.Add(MakeLabel("Gallery URL:"), 0, 0);
			txtUrl.Dock = DockStyle.Fill;
			txtUrl.Margin = new Padding(3, 4, 3, 3);
			txtUrl.KeyDown += (s, e) =>
			{
				if (e.KeyCode == Keys.Enter)
				{
					e.SuppressKeyPress = true;
					StartDownload(txtUrl.Text);
				}
			};
			top.Controls.Add(txtUrl, 1, 0);
			top.SetColumnSpan(txtUrl, 2);
			btPaste.Text = "Paste && Download";
			btPaste.AutoSize = true;
			btPaste.Click += (s, e) => StartDownload(Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty);
			top.Controls.Add(btPaste, 3, 0);
			btDownload.Text = "Download";
			btDownload.AutoSize = true;
			btDownload.Click += (s, e) => StartDownload(txtUrl.Text);
			top.Controls.Add(btDownload, 4, 0);

			top.Controls.Add(MakeLabel("Save as:"), 0, 1);
			cbFormat.DropDownStyle = ComboBoxStyle.DropDownList;
			cbFormat.Dock = DockStyle.Fill;
			cbFormat.Items.AddRange(formats.Select(f => (object)f.Text).ToArray());
			top.Controls.Add(cbFormat, 1, 1);
			txtFolder.Dock = DockStyle.Fill;
			txtFolder.Margin = new Padding(3, 4, 3, 3);
			top.Controls.Add(txtFolder, 2, 1);
			btBrowse.Text = "Folder…";
			btBrowse.AutoSize = true;
			btBrowse.Click += (s, e) => BrowseFolder();
			top.Controls.Add(btBrowse, 3, 1);

			FlowLayoutPanel options = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				WrapContents = true,
				Margin = new Padding(0)
			};
			chkAddToLibrary.Text = "Add to library";
			chkOpen.Text = "Open when finished";
			chkChapters.Text = "Choose chapters";
			chkClipboard.Text = "Watch clipboard for links";
			foreach (CheckBox cb in new[] { chkAddToLibrary, chkOpen, chkChapters, chkClipboard })
			{
				cb.AutoSize = true;
				cb.Margin = new Padding(3, 4, 12, 3);
				options.Controls.Add(cb);
			}
			chkClipboard.CheckedChanged += (s, e) =>
			{
				lastClipboard = SafeClipboardText();
				clipboardTimer.Enabled = chkClipboard.Checked;
			};
			lnkSites.AutoSize = true;
			lnkSites.Margin = new Padding(12, 5, 6, 3);
			lnkSites.LinkClicked += (s, e) => ShowSupportedSites();
			lnkUpdate.Text = "Update site modules";
			lnkUpdate.AutoSize = true;
			lnkUpdate.Margin = new Padding(6, 5, 6, 3);
			lnkUpdate.LinkClicked += (s, e) => UpdateModules();
			lnkExtension.Text = "Browser extension…";
			lnkExtension.AutoSize = true;
			lnkExtension.Margin = new Padding(6, 5, 6, 3);
			lnkExtension.LinkClicked += (s, e) => ShowBrowserExtensionHelp();
			options.Controls.Add(lnkSites);
			options.Controls.Add(lnkUpdate);
			options.Controls.Add(lnkExtension);
			top.Controls.Add(options, 0, 2);
			top.SetColumnSpan(options, 5);

			lvJobs.Dock = DockStyle.Fill;
			lvJobs.View = View.Details;
			lvJobs.FullRowSelect = true;
			lvJobs.HideSelection = false;
			lvJobs.OwnerDraw = true;
			lvJobs.Columns.Add("Title", 360);
			lvJobs.Columns.Add("Site", 140);
			lvJobs.Columns.Add("Progress", 140);
			lvJobs.Columns.Add("Status", 260);
			lvJobs.DrawColumnHeader += ThemeExtensions.ListView_DrawColumnHeader;
			lvJobs.DrawItem += (s, e) => { };
			lvJobs.DrawSubItem += DrawJobSubItem;
			lvJobs.SelectedIndexChanged += (s, e) => UpdateLog();
			lvJobs.DoubleClick += (s, e) => OpenSelected();
			lvJobs.KeyDown += (s, e) =>
			{
				if (e.KeyCode == Keys.Delete)
				{
					SelectedJobs().ToList().ForEach(Manager.Remove);
				}
			};
			lvJobs.ContextMenuStrip = jobMenu;

			jobMenu.Items.Add("Open", null, (s, e) => OpenSelected());
			jobMenu.Items.Add("Show in Folder", null, (s, e) => ShowSelectedInFolder());
			jobMenu.Items.Add(new ToolStripSeparator());
			jobMenu.Items.Add("Retry", null, (s, e) => SelectedJobs().ToList().ForEach(Manager.Retry));
			jobMenu.Items.Add("Cancel", null, (s, e) => SelectedJobs().ToList().ForEach(j => j.Cancel()));
			jobMenu.Items.Add("Remove", null, (s, e) => SelectedJobs().ToList().ForEach(Manager.Remove));
			jobMenu.Items.Add("Copy URL", null, (s, e) =>
			{
				string urls = string.Join(Environment.NewLine, SelectedJobs().Select(j => j.Url));
				if (urls.Length > 0)
				{
					Clipboard.SetText(urls);
				}
			});
			jobMenu.Items.Add(new ToolStripSeparator());
			jobMenu.Items.Add("Clear Finished", null, (s, e) => Manager.ClearFinished());

			txtLog.Dock = DockStyle.Fill;
			txtLog.Multiline = true;
			txtLog.ReadOnly = true;
			txtLog.ScrollBars = ScrollBars.Vertical;
			txtLog.Font = new Font(FontFamily.GenericMonospace, 8.25f);

			SplitContainer split = new SplitContainer
			{
				Dock = DockStyle.Fill,
				Orientation = Orientation.Horizontal,
				SplitterWidth = 6
			};
			split.Panel1.Controls.Add(lvJobs);
			split.Panel2.Controls.Add(txtLog);
			// The view can be created while hidden or tiny, so only place the splitter once there is room for it
			bool splitPlaced = false;
			split.SizeChanged += (s, e) =>
			{
				int max = split.Height - split.Panel2MinSize - split.SplitterWidth;
				if (splitPlaced || max <= split.Panel1MinSize)
					return;
				split.SplitterDistance = Math.Max(split.Panel1MinSize, Math.Min(max, Math.Max(100, split.Height * 2 / 3)));
				splitPlaced = split.Height > 200;
			};

			Controls.Add(split);
			Controls.Add(top);

			clipboardTimer.Tick += (s, e) => CheckClipboard();
			logTimer.Tick += (s, e) => UpdateLog();
			ResumeLayout();
		}

		private static Label MakeLabel(string text)
		{
			return new Label
			{
				Text = text,
				AutoSize = true,
				Anchor = AnchorStyles.Left,
				Margin = new Padding(0, 6, 6, 3)
			};
		}

		private void EnsureInitialized()
		{
			if (initialized || DesignMode)
			{
				return;
			}
			initialized = true;
			BuildLayout();
			LoadSettings();
			this.Theme();
			if (ThemeManager.IsDarkModeEnabled)
			{
				foreach (LinkLabel link in new[] { lnkSites, lnkUpdate, lnkExtension })
				{
					link.LinkColor = Color.FromArgb(110, 170, 255);
					link.ActiveLinkColor = Color.FromArgb(160, 200, 255);
					link.VisitedLinkColor = link.LinkColor;
					link.DisabledLinkColor = Color.Gray;
				}
			}
			// Status column takes the remaining width, so no empty header strip is left
			lvJobs.Resize += (s, e) => FillLastColumn();
			FillLastColumn();
			Manager.JobAdded += Manager_JobAdded;
			Manager.JobChanged += Manager_JobChanged;
			Manager.JobRemoved += Manager_JobRemoved;
			Manager.SelectChapters = SelectChapters;
			foreach (DownloadJob job in Manager.Jobs)
			{
				AddItem(job);
			}
			logTimer.Start();
			Task.Run(() => Manager.Catalog).ContinueWith(t => BeginInvokeSafe(UpdateSitesLink));
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (initialized)
				{
					SaveSettings();
				}
				if (manager != null)
				{
					manager.JobAdded -= Manager_JobAdded;
					manager.JobChanged -= Manager_JobChanged;
					manager.JobRemoved -= Manager_JobRemoved;
				}
				clipboardTimer.Dispose();
				logTimer.Dispose();
			}
			base.Dispose(disposing);
		}

		protected override void OnVisibleChanged(EventArgs e)
		{
			base.OnVisibleChanged(e);
			if (Visible)
			{
				EnsureInitialized();
			}
			else if (initialized)
			{
				SaveSettings();
			}
		}

		private void LoadSettings()
		{
			string folder = Program.Settings.DownloaderOutputFolder;
			txtFolder.Text = string.IsNullOrEmpty(folder) ? DefaultOutputFolder : folder;
			OutputFormat format = Enum.TryParse(Program.Settings.DownloaderFormat, true, out OutputFormat f) ? f : OutputFormat.Cbz;
			cbFormat.SelectedIndex = Math.Max(0, Array.FindIndex(formats, x => x.Format == format));
			chkAddToLibrary.Checked = Program.Settings.DownloaderAddToLibrary;
			chkOpen.Checked = Program.Settings.DownloaderOpenWhenDone;
			chkChapters.Checked = Program.Settings.DownloaderSelectChapters;
			chkClipboard.Checked = Program.Settings.DownloaderWatchClipboard;
			// Browser downloads finish without this tab, so the choices are saved right away
			foreach (CheckBox cb in new[] { chkAddToLibrary, chkOpen, chkChapters, chkClipboard })
			{
				cb.CheckedChanged += (s, e) => SaveSettings();
			}
			cbFormat.SelectedIndexChanged += (s, e) => SaveSettings();
			txtFolder.Leave += (s, e) => SaveSettings();
		}

		private void SaveSettings()
		{
			Program.Settings.DownloaderOutputFolder = txtFolder.Text;
			Program.Settings.DownloaderFormat = CurrentFormat.ToString();
			Program.Settings.DownloaderAddToLibrary = chkAddToLibrary.Checked;
			Program.Settings.DownloaderOpenWhenDone = chkOpen.Checked;
			Program.Settings.DownloaderSelectChapters = chkChapters.Checked;
			Program.Settings.DownloaderWatchClipboard = chkClipboard.Checked;
		}

		private OutputFormat CurrentFormat => formats[Math.Max(0, cbFormat.SelectedIndex)].Format;

		private DownloadOptions CurrentOptions()
		{
			return new DownloadOptions
			{
				OutputFolder = txtFolder.Text.Trim(),
				Format = CurrentFormat
			};
		}

		// Accepts one or more URLs (one per line or separated by spaces)
		public void StartDownload(string text)
		{
			List<string> urls = (text ?? string.Empty).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.Where(u => Uri.TryCreate(u.Trim(), UriKind.Absolute, out Uri uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
				.Distinct()
				.ToList();
			if (urls.Count == 0)
			{
				MessageBox.Show(this, "Please enter or copy the link (URL) of a gallery, chapter or series page.", "Downloads", MessageBoxButtons.OK, MessageBoxIcon.Information);
				txtUrl.Focus();
				return;
			}
			SaveSettings();
			foreach (string url in urls)
			{
				Manager.Add(url, CurrentOptions());
			}
			txtUrl.Clear();
		}

		private void BrowseFolder()
		{
			using (FolderBrowserDialog dlg = new FolderBrowserDialog { SelectedPath = txtFolder.Text, Description = "Folder for downloaded books" })
			{
				if (dlg.ShowDialog(this) == DialogResult.OK)
				{
					txtFolder.Text = dlg.SelectedPath;
					SaveSettings();
				}
			}
		}

		#region Job list

		private void Manager_JobAdded(object sender, DownloadJob job)
		{
			BeginInvokeSafe(() => AddItem(job));
		}

		private void Manager_JobChanged(object sender, DownloadJob job)
		{
			BeginInvokeSafe(() => UpdateItem(job));
		}

		private void Manager_JobRemoved(object sender, DownloadJob job)
		{
			BeginInvokeSafe(() =>
			{
				if (items.TryGetValue(job, out ListViewItem item))
				{
					lvJobs.Items.Remove(item);
					items.Remove(job);
				}
			});
		}

		private static void Manager_JobCompleted(object sender, DownloadJob job)
		{
			MainForm mainForm = Program.MainForm;
			if (job.Status != DownloadStatus.Completed || mainForm == null || mainForm.IsDisposed)
			{
				return;
			}
			try
			{
				mainForm.BeginInvoke(new Action(() =>
				{
					string[] files = job.OutputFiles;
					if (files.Length == 0 || job.Options.Format == OutputFormat.Folder)
					{
						return;
					}
					if (Program.Settings.DownloaderAddToLibrary)
					{
						foreach (string file in files)
						{
							Program.Scanner.ScanFileOrFolder(file, all: true, removeMissing: false);
						}
					}
					if (Program.Settings.DownloaderOpenWhenDone)
					{
						mainForm.OpenBooks?.Open(files[0], inNewSlot: true);
					}
				}));
			}
			catch (InvalidOperationException)
			{
			}
		}

		private void AddItem(DownloadJob job)
		{
			if (items.ContainsKey(job))
			{
				return;
			}
			ListViewItem item = new ListViewItem(new[] { job.Title, job.Site ?? string.Empty, string.Empty, job.StatusText })
			{
				Tag = job
			};
			items[job] = item;
			lvJobs.Items.Add(item);
		}

		private void UpdateItem(DownloadJob job)
		{
			if (!items.TryGetValue(job, out ListViewItem item))
			{
				return;
			}
			item.SubItems[0].Text = job.Title;
			item.SubItems[1].Text = job.Site ?? string.Empty;
			item.SubItems[3].Text = job.StatusText;
			lvJobs.Invalidate(item.Bounds);
		}

		private void DrawJobSubItem(object sender, DrawListViewSubItemEventArgs e)
		{
			DownloadJob job = e.Item.Tag as DownloadJob;
			if (e.ColumnIndex != 2 || job == null)
			{
				e.DrawDefault = true;
				return;
			}
			e.DrawBackground();
			if (e.Item.Selected)
			{
				e.Graphics.FillRectangle(SystemBrushes.Highlight, e.Bounds);
			}
			Rectangle bar = Rectangle.Inflate(e.Bounds, -4, -4);
			Color accent;
			switch (job.Status)
			{
				case DownloadStatus.Failed:
					accent = Color.IndianRed;
					break;
				case DownloadStatus.Canceled:
					accent = Color.Gray;
					break;
				case DownloadStatus.Completed:
					accent = Color.SeaGreen;
					break;
				default:
					accent = Color.FromArgb(52, 132, 228);
					break;
			}
			using (SolidBrush back = new SolidBrush(Color.FromArgb(40, accent)))
			using (SolidBrush fill = new SolidBrush(accent))
			{
				e.Graphics.FillRectangle(back, bar);
				e.Graphics.FillRectangle(fill, new Rectangle(bar.X, bar.Y, (int)(bar.Width * job.Progress), bar.Height));
			}
			TextRenderer.DrawText(e.Graphics, $"{job.Progress:P0}", lvJobs.Font, bar, e.Item.Selected ? SystemColors.HighlightText : lvJobs.ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
		}

		private void FillLastColumn()
		{
			int others = lvJobs.Columns.Cast<ColumnHeader>().Take(lvJobs.Columns.Count - 1).Sum(c => c.Width);
			int width = lvJobs.ClientSize.Width - others;
			if (width > 120)
			{
				lvJobs.Columns[lvJobs.Columns.Count - 1].Width = width;
			}
		}

		private IEnumerable<DownloadJob> SelectedJobs()
		{
			return lvJobs.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<DownloadJob>();
		}

		private void UpdateLog()
		{
			DownloadJob job = SelectedJobs().FirstOrDefault();
			string text = job == null ? string.Empty : $"{job.Url}{Environment.NewLine}{job.Log}";
			if (txtLog.Text != text)
			{
				txtLog.Text = text;
				txtLog.SelectionStart = txtLog.TextLength;
				txtLog.ScrollToCaret();
			}
		}

		private void OpenSelected()
		{
			string file = SelectedJobs().SelectMany(j => j.OutputFiles).FirstOrDefault();
			if (file == null)
			{
				return;
			}
			if (Directory.Exists(file))
			{
				Process.Start(file);
			}
			else
			{
				(Main?.OpenBooks ?? Program.MainForm?.OpenBooks)?.Open(file, inNewSlot: true);
			}
		}

		private void ShowSelectedInFolder()
		{
			string file = SelectedJobs().SelectMany(j => j.OutputFiles).FirstOrDefault();
			if (file != null)
			{
				Process.Start("explorer.exe", "/select,\"" + file + "\"");
			}
		}

		#endregion

		#region Chapters, sites and modules

		// Called on a download thread when a series has several chapters
		private IList<ChapterEntry> SelectChapters(DownloadJob job, GalleryInfo info, IList<ChapterEntry> chapters)
		{
			if (!chkChapters.Checked || IsDisposed)
			{
				return chapters;
			}
			IList<ChapterEntry> result = chapters;
			Invoke(new Action(() =>
			{
				using (ChapterSelectionDialog dlg = new ChapterSelectionDialog(info["Title"], chapters))
				{
					result = dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedChapters : null;
				}
			}));
			return result;
		}

		private void UpdateSitesLink()
		{
			int count = Manager.Catalog.Sites.Select(s => s.Domain).Distinct().Count();
			lnkSites.Text = $"Supported sites ({count})";
		}

		private void ShowSupportedSites()
		{
			using (SupportedSitesDialog dlg = new SupportedSitesDialog(Manager.Catalog.Sites))
			{
				dlg.ShowDialog(this);
			}
		}

		// Downloads the latest site modules from the HDoujin Downloader repository into the user folder
		private void UpdateModules()
		{
			lnkUpdate.Enabled = false;
			lnkUpdate.Text = "Updating site modules…";
			Task.Run(() =>
			{
				int count = ModuleUpdater.Update(UserModulesFolder);
				Manager.ReloadCatalog();
				_ = Manager.Catalog;
				return $"Updated {count} site modules.";
			}).ContinueWith(t => BeginInvokeSafe(() =>
			{
				lnkUpdate.Enabled = true;
				lnkUpdate.Text = "Update site modules";
				UpdateSitesLink();
				if (t.IsFaulted)
				{
					MessageBox.Show(this, "Could not update the site modules:\n" + t.Exception.GetBaseException().Message, "Downloads", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				}
				else
				{
					MessageBox.Show(this, t.Result, "Downloads", MessageBoxButtons.OK, MessageBoxIcon.Information);
				}
			}));
		}

		private void ShowBrowserExtensionHelp()
		{
			string text = "The ComicRack browser extension downloads the comic on the page you're viewing, "
				+ "in your browser, and sends it here to be packed and added to your library.\n\n"
				+ "To install it in Chrome, Edge, Brave, Opera or Vivaldi:\n"
				+ "1. Open the extensions page (chrome://extensions or edge://extensions)\n"
				+ "2. Turn on \"Developer mode\"\n"
				+ "3. Click \"Load unpacked\" and select the folder that opens next\n\n"
				+ "The toolbar icon turns green on pages ComicRack has a site module for. "
				+ "Downloads use the format and folder chosen on this tab.\n\nOpen the extension folder now?";
			if (MessageBox.Show(this, text, "Browser extension", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK && Directory.Exists(BrowserExtensionFolder))
			{
				Process.Start("explorer.exe", "\"" + BrowserExtensionFolder + "\"");
			}
		}

		private static string SafeClipboardText()
		{
			try
			{
				return Clipboard.ContainsText() ? Clipboard.GetText().Trim() : null;
			}
			catch (Exception)
			{
				return null;
			}
		}

		// Like HDoujin's clipboard monitor: copied links of supported sites are queued automatically
		private void CheckClipboard()
		{
			string text = SafeClipboardText();
			if (string.IsNullOrEmpty(text) || text == lastClipboard)
			{
				return;
			}
			lastClipboard = text;
			if (!Uri.TryCreate(text, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			{
				return;
			}
			if (Manager.Catalog.FindSite(text) == null || Manager.Jobs.Any(j => j.Url == text && !j.IsFinished))
			{
				return;
			}
			Manager.Add(text, CurrentOptions());
		}

		#endregion

		private void BeginInvokeSafe(Action action)
		{
			if (IsDisposed || !IsHandleCreated)
			{
				return;
			}
			try
			{
				BeginInvoke(action);
			}
			catch (InvalidOperationException)
			{
			}
		}

		private class ChapterSelectionDialog : Form
		{
			private readonly CheckedListBox list = new CheckedListBox();

			private readonly IList<ChapterEntry> chapters;

			public IList<ChapterEntry> SelectedChapters => list.CheckedIndices.Cast<int>().Select(i => chapters[i]).ToList();

			public ChapterSelectionDialog(string title, IList<ChapterEntry> chapters)
			{
				this.chapters = chapters;
				Text = "Select Chapters - " + title;
				StartPosition = FormStartPosition.CenterParent;
				Size = new Size(520, 560);
				MinimizeBox = false;
				ShowInTaskbar = false;
				Padding = new Padding(8);

				list.Dock = DockStyle.Fill;
				list.CheckOnClick = true;
				list.IntegralHeight = false;
				foreach (ChapterEntry c in chapters)
				{
					list.Items.Add(string.IsNullOrWhiteSpace(c.Title) ? c.Url : c.Title, isChecked: true);
				}

				Label info = new Label
				{
					Dock = DockStyle.Top,
					AutoSize = false,
					Height = 24,
					Text = $"{chapters.Count} chapters found. Each chapter is saved as its own book."
				};
				FlowLayoutPanel buttons = new FlowLayoutPanel
				{
					Dock = DockStyle.Bottom,
					FlowDirection = FlowDirection.RightToLeft,
					AutoSize = true,
					Padding = new Padding(0, 6, 0, 0)
				};
				Button ok = new Button { Text = "Download", DialogResult = DialogResult.OK, AutoSize = true };
				Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
				Button none = new Button { Text = "Select None", AutoSize = true };
				Button all = new Button { Text = "Select All", AutoSize = true };
				all.Click += (s, e) => SetAll(true);
				none.Click += (s, e) => SetAll(false);
				buttons.Controls.AddRange(new Control[] { cancel, ok, none, all });
				AcceptButton = ok;
				CancelButton = cancel;
				Controls.Add(list);
				Controls.Add(info);
				Controls.Add(buttons);
				this.Theme();
			}

			private void SetAll(bool value)
			{
				for (int i = 0; i < list.Items.Count; i++)
				{
					list.SetItemChecked(i, value);
				}
			}
		}

		private class SupportedSitesDialog : Form
		{
			private readonly IReadOnlyList<SiteInfo> sites;

			private readonly ListView list = new ListView();

			private readonly TextBox search = new TextBox();

			public SupportedSitesDialog(IReadOnlyList<SiteInfo> sites)
			{
				this.sites = sites;
				Text = "Supported Sites";
				StartPosition = FormStartPosition.CenterParent;
				Size = new Size(560, 620);
				MinimizeBox = false;
				ShowInTaskbar = false;
				Padding = new Padding(8);

				search.Dock = DockStyle.Top;
				search.TextChanged += (s, e) => Fill();
				list.Dock = DockStyle.Fill;
				list.View = View.Details;
				list.FullRowSelect = true;
				list.Columns.Add("Domain", 220);
				list.Columns.Add("Name", 200);
				list.Columns.Add("18+", 50);
				list.DoubleClick += (s, e) =>
				{
					if (list.SelectedItems.Count > 0)
					{
						Process.Start("https://" + list.SelectedItems[0].Text);
					}
				};
				Label hint = new Label
				{
					Dock = DockStyle.Bottom,
					Height = 36,
					Text = "Site modules come from the HDoujin Downloader project (MIT license). Other sites are tried with a generic image grabber."
				};
				Controls.Add(list);
				Controls.Add(search);
				Controls.Add(hint);
				Fill();
				this.Theme();
			}

			private void Fill()
			{
				string filter = search.Text.Trim();
				list.BeginUpdate();
				list.Items.Clear();
				foreach (SiteInfo site in sites.Where(s => s.Domain != "*" && (filter.Length == 0 || s.Domain.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)).GroupBy(s => s.Domain).Select(g => g.First()))
				{
					list.Items.Add(new ListViewItem(new[] { site.Domain, site.Name, site.Adult ? "yes" : string.Empty }));
				}
				list.EndUpdate();
			}
		}
	}
}
