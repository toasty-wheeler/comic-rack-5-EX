using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace cYo.Projects.ComicRack.Downloader
{
	// Local endpoint for the ComicRack browser extension. The extension fetches the page and its
	// images in the browser (where the user is viewing them) and hands them to ComicRack, which
	// reads the metadata with the site module, lists the job in Downloads and packs the book.
	//
	//   GET  /api/status                   is ComicRack running
	//   GET  /api/check?url=               is there a site module for the page (icon color)
	//   POST /api/prepare                  {url, title, html, meta, images} -> job id + page URLs
	//   POST /api/page?job=&index=         one image (raw bytes)
	//   POST /api/complete?job=            {failed} -> packs the book
	//   POST /api/fail?job=                {message}
	//
	// Only requests from browser extensions (or local programs) are accepted: web pages always
	// send their own Origin, which is rejected.
	public class BrowserBridge : IDisposable
	{
		public const int DefaultPort = 17385;

		private readonly DownloadManager manager;

		private readonly Func<DownloadOptions> getOptions;

		private readonly ConcurrentDictionary<Guid, BrowserJob> jobs = new ConcurrentDictionary<Guid, BrowserJob>();

		private readonly HttpSession session = new HttpSession();

		private HttpListener listener;

		public string AppVersion { get; set; } = string.Empty;

		public int Port { get; private set; }

		public BrowserBridge(DownloadManager manager, Func<DownloadOptions> getOptions)
		{
			this.manager = manager;
			this.getOptions = getOptions;
		}

		public void Start(int port = DefaultPort)
		{
			Port = port;
			listener = new HttpListener();
			listener.Prefixes.Add($"http://127.0.0.1:{port}/");
			listener.Start();
			Thread thread = new Thread(Listen)
			{
				IsBackground = true,
				Name = "Browser Bridge"
			};
			thread.Start();
		}

		public void Dispose()
		{
			try
			{
				listener?.Close();
			}
			catch (Exception)
			{
			}
			foreach (BrowserJob job in jobs.Values)
			{
				job.Job.Cancel();
				DeleteTemp(job);
			}
		}

		private void Listen()
		{
			while (listener != null && listener.IsListening)
			{
				HttpListenerContext context;
				try
				{
					context = listener.GetContext();
				}
				catch (Exception)
				{
					return;
				}
				ThreadPool.QueueUserWorkItem(_ => Handle(context));
			}
		}

		private void Handle(HttpListenerContext context)
		{
			HttpListenerRequest request = context.Request;
			HttpListenerResponse response = context.Response;
			try
			{
				string origin = request.Headers["Origin"];
				bool extensionOrigin = origin != null && (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) || origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase) || origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase));
				if (origin != null && !extensionOrigin)
				{
					Reply(response, 403, new JObject { ["error"] = "Only the ComicRack browser extension may use this endpoint" });
					return;
				}
				if (extensionOrigin)
				{
					response.Headers["Access-Control-Allow-Origin"] = origin;
					response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Source-Url";
					response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
				}
				if (request.HttpMethod == "OPTIONS")
				{
					Reply(response, 204, null);
					return;
				}
				switch (request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant())
				{
					case "/api/status":
						Reply(response, 200, new JObject { ["app"] = "ComicRackCE", ["version"] = AppVersion });
						break;
					case "/api/check":
						Reply(response, 200, Check(request.QueryString["url"]));
						break;
					case "/api/prepare":
						Reply(response, 200, Prepare(ReadJson(request)));
						break;
					case "/api/page":
						Reply(response, 200, ReceivePage(FindJob(request), request));
						break;
					case "/api/complete":
						Reply(response, 200, Complete(FindJob(request), ReadJson(request)));
						break;
					case "/api/fail":
						Reply(response, 200, Fail(FindJob(request), (string)ReadJson(request)["message"]));
						break;
					default:
						Reply(response, 404, new JObject { ["error"] = "Unknown endpoint" });
						break;
				}
			}
			catch (BridgeException e)
			{
				Reply(response, e.Status, new JObject { ["error"] = e.Message });
			}
			catch (Exception e)
			{
				Reply(response, 500, new JObject { ["error"] = e.Message });
			}
		}

		#region Endpoints

		private JObject Check(string url)
		{
			if (!IsWebUrl(url))
			{
				return new JObject { ["supported"] = false };
			}
			SiteInfo site = manager.Catalog.FindSite(url);
			return new JObject
			{
				["supported"] = site != null,
				["site"] = site?.Name
			};
		}

		private JObject Prepare(JObject body)
		{
			string url = (string)body["url"];
			if (!IsWebUrl(url))
			{
				throw new BridgeException(400, "Missing page URL");
			}
			BrowserJob bj = new BrowserJob
			{
				Job = manager.AddExternal(url, getOptions())
			};
			DownloadJob job = bj.Job;
			job.Site = "Browser";
			job.StatusText = "Reading page";
			job.WriteLog("Sent from the browser extension");
			jobs[job.Id] = bj;

			List<PageRequest> pages = null;
			bool series = false;
			// MoonSharp and deep HTML recurse, so modules run on a thread with a large stack
			Thread worker = new Thread(() => RunModule(bj, url, (string)body["html"], ref pages, ref series), 16 * 1024 * 1024)
			{
				IsBackground = true,
				Name = "Browser module"
			};
			worker.Start();
			worker.Join();

			if (series)
			{
				// A series page: the chapters are separate pages, so ComicRack downloads them itself
				jobs.TryRemove(job.Id, out _);
				job.WriteLog("Series page: chapters are downloaded by ComicRack");
				manager.QueueExternal(job);
				return new JObject { ["mode"] = "queued", ["jobId"] = job.Id.ToString("N"), ["title"] = job.Title };
			}

			GalleryInfo info = bj.Info ?? new GalleryInfo();
			ApplyPageMetadata(info, body, url);
			info.Url = url;
			info.ModuleName = job.Site;
			bj.Info = info;
			job.Title = FirstNonEmpty(info["Title"], (string)body["title"], url);

			if (pages == null || pages.Count == 0)
			{
				// No module (or it found nothing): use the large images the browser found on the page
				pages = ((body["images"] as JArray) ?? new JArray())
					.Select(t => t.Type == JTokenType.Object ? (string)t["url"] : (string)t)
					.Where(u => !string.IsNullOrWhiteSpace(u))
					.Distinct()
					.Select((u, i) => new PageRequest { Index = i, Url = u, Referer = url })
					.ToList();
				if (pages.Count > 0)
				{
					job.WriteLog($"Using the {pages.Count} images shown on the page");
				}
			}
			if (pages.Count == 0)
			{
				Fail(bj, "No comic pages found on this page");
				throw new BridgeException(422, "No comic pages found on this page");
			}
			bj.Files = new string[pages.Count];
			bj.TempFolder = Path.Combine(Path.GetTempPath(), "ComicRackDownloads", job.Id.ToString("N"));
			Directory.CreateDirectory(bj.TempFolder);
			job.StatusText = $"Downloading {pages.Count} pages in the browser";
			job.Progress = 0.02f;
			return new JObject
			{
				["mode"] = "pages",
				["jobId"] = job.Id.ToString("N"),
				["title"] = job.Title,
				["site"] = job.Site,
				["pages"] = new JArray(pages.Select(p => new JObject { ["url"] = p.Url, ["referer"] = p.Referer ?? url }))
			};
		}

		private void RunModule(BrowserJob bj, string url, string html, ref List<PageRequest> pages, ref bool series)
		{
			DownloadJob job = bj.Job;
			SiteInfo site = manager.Catalog.FindSite(url);
			if (site == null)
			{
				job.WriteLog("No site module for " + UrlUtility.Host(url) + "; using the page itself");
				return;
			}
			try
			{
				job.Site = site.Name;
				job.WriteLog($"Using module {Path.GetFileName(site.ModuleFile)} with the page from the browser");
				ModuleScript script = new ModuleScript(manager.Catalog, site.ModuleFile, session)
				{
					Cancel = job.Cancellation.Token,
					Log = job.WriteLog,
					GetSetting = name => manager.GetSetting?.Invoke(name) ?? GalleryDownloader.DefaultSetting(name)
				};
				script.Register();
				job.Site = script.SelectModule(UrlUtility.Host(url)) ?? site.Name;
				if (!string.IsNullOrEmpty(html))
				{
					script.SeedDocument(url, html);
				}
				script.Call("__reset_lists");
				script.Begin(url);
				script.CallIfDefined("GetInfo");
				bj.Info = GalleryDownloader.ExportInfo(script);
				if (script.CallIfDefined("GetChapters") && GalleryDownloader.ExportChapters(script).Count > 1)
				{
					series = true;
					return;
				}
				if (!script.Has("GetPages"))
				{
					return;
				}
				script.Call("__reset_pages");
				script.Begin(url);
				script.Call("GetPages");
				int count = (int)script.Call("__page_count").Number;
				List<PageRequest> list = new List<PageRequest>();
				for (int i = 0; i < count; i++)
				{
					list.Add(GalleryDownloader.ExportPage(script.Call("__prepare_page", i + 1).Table, i, url));
				}
				job.WriteLog($"{count} pages");
				pages = list;
			}
			catch (Exception e)
			{
				// Metadata from the page and the images the browser found are still used
				job.WriteLog("Module failed, using the page itself: " + ((e as ModuleException)?.Details ?? e.Message));
			}
		}

		private JObject ReceivePage(BrowserJob bj, HttpListenerRequest request)
		{
			DownloadJob job = bj.Job;
			if (job.Cancellation.IsCancellationRequested)
			{
				throw new BridgeException(409, "Canceled in ComicRack");
			}
			if (!int.TryParse(request.QueryString["index"], out int index) || index < 0 || index >= bj.Files.Length)
			{
				throw new BridgeException(400, "Bad page index");
			}
			byte[] data;
			using (MemoryStream ms = new MemoryStream())
			{
				request.InputStream.CopyTo(ms);
				data = ms.ToArray();
			}
			string ext = ImageType.GetExtension(data, request.ContentType, request.Headers["X-Source-Url"] ?? string.Empty);
			if (ext == null)
			{
				job.WriteLog($"Page {index + 1} is not an image ({request.ContentType})");
				throw new BridgeException(415, "Not an image");
			}
			string file = Path.Combine(bj.TempFolder, (index + 1).ToString("D4") + ext);
			File.WriteAllBytes(file, data);
			int received;
			lock (bj)
			{
				if (bj.Files[index] == null)
				{
					bj.Received++;
				}
				bj.Files[index] = file;
				received = bj.Received;
			}
			job.Progress = 0.02f + 0.93f * received / bj.Files.Length;
			job.StatusText = $"Downloading in the browser ({received} of {bj.Files.Length})";
			return new JObject { ["ok"] = true };
		}

		private JObject Complete(BrowserJob bj, JObject body)
		{
			DownloadJob job = bj.Job;
			try
			{
				List<string> files = bj.Files.Where(f => f != null).ToList();
				int missing = bj.Files.Length - files.Count;
				if (files.Count == 0)
				{
					throw new BridgeException(422, "No pages were downloaded");
				}
				if (missing > 0)
				{
					job.WriteLog($"{missing} of {bj.Files.Length} pages could not be downloaded by the browser");
					if (!job.Options.SkipFailedPages)
					{
						throw new BridgeException(422, $"{missing} of {bj.Files.Length} pages could not be downloaded");
					}
				}
				job.StatusText = "Packing";
				string output = ComicPackager.Pack(files, bj.Info, null, job.Options);
				job.WriteLog("Saved " + output);
				job.AddOutput(output);
				job.Progress = 1f;
				job.StatusText = "Done";
				job.Status = DownloadStatus.Completed;
				manager.CompleteExternal(job);
				return new JObject { ["ok"] = true, ["file"] = output, ["title"] = job.Title };
			}
			catch (BridgeException e)
			{
				Fail(bj, e.Message);
				throw;
			}
			catch (Exception e)
			{
				Fail(bj, e.Message);
				throw new BridgeException(500, e.Message);
			}
			finally
			{
				jobs.TryRemove(job.Id, out _);
				DeleteTemp(bj);
			}
		}

		private JObject Fail(BrowserJob bj, string message)
		{
			DownloadJob job = bj.Job;
			message = string.IsNullOrWhiteSpace(message) ? "Failed in the browser" : message;
			if (!job.IsFinished)
			{
				job.Error = message;
				job.WriteLog("Error: " + message);
				job.StatusText = (job.Cancellation.IsCancellationRequested ? "Canceled" : "Failed: ") + (job.Cancellation.IsCancellationRequested ? string.Empty : message);
				job.Status = job.Cancellation.IsCancellationRequested ? DownloadStatus.Canceled : DownloadStatus.Failed;
				manager.CompleteExternal(job);
			}
			jobs.TryRemove(job.Id, out _);
			DeleteTemp(bj);
			return new JObject { ["ok"] = true };
		}

		#endregion

		// Fills what the module didn't provide from the page's own tags (title, description, ...)
		private static void ApplyPageMetadata(GalleryInfo info, JObject body, string url)
		{
			JObject meta = body["meta"] as JObject ?? new JObject();
			void Fill(string field, params string[] values)
			{
				if (string.IsNullOrWhiteSpace(info[field]))
				{
					string v = FirstNonEmpty(values);
					if (!string.IsNullOrWhiteSpace(v))
					{
						info.Fields[field] = v.Trim();
					}
				}
			}
			Fill("Title", (string)meta["title"], (string)body["title"]);
			Fill("Summary", (string)meta["description"]);
			Fill("Author", (string)meta["author"]);
			Fill("Publisher", (string)meta["siteName"], UrlUtility.Host(url));
			Fill("Language", (string)meta["language"]);
			Fill("Series", (string)meta["series"], SeriesFromPageTitle(info["Title"], url, (string)meta["siteName"], (string)body["title"], (string)meta["title"]));
			Fill("Tags", (string)meta["keywords"]);
		}

		private static readonly string[] titleSeparators = { " | ", " - ", " – ", " — ", " :: ", " : " };

		// Chapter pages are usually titled "Ep. 3 | Series" or "Series - Chapter 3": when the module
		// gave the chapter title, the remaining part of the page title is the series
		private static string SeriesFromPageTitle(string title, string url, string siteName, params string[] pageTitles)
		{
			if (string.IsNullOrWhiteSpace(title))
			{
				return null;
			}
			string host = UrlUtility.Host(url).Replace("www.", string.Empty);
			foreach (string pageTitle in pageTitles.Where(t => !string.IsNullOrWhiteSpace(t)))
			{
				List<string> parts = pageTitle.Split(titleSeparators, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
				if (parts.Count < 2 || !parts.Any(p => p.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}
				List<string> rest = parts.Where(p => !p.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase)
					&& !p.Equals(siteName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
					&& p.IndexOf(host, StringComparison.OrdinalIgnoreCase) < 0
					&& !p.Equals(host.Split('.')[0], StringComparison.OrdinalIgnoreCase)
					&& !Regex.IsMatch(p, @"(?i)\b(read|online|free|manga|comics?|webtoons?)\b.*\b(online|free|for free)\b")).ToList();
				if (rest.Count == 1)
				{
					return rest[0];
				}
			}
			return null;
		}

		private BrowserJob FindJob(HttpListenerRequest request)
		{
			if (Guid.TryParseExact(request.QueryString["job"] ?? string.Empty, "N", out Guid id) && jobs.TryGetValue(id, out BrowserJob bj))
			{
				return bj;
			}
			throw new BridgeException(410, "This download is no longer active in ComicRack");
		}

		private static JObject ReadJson(HttpListenerRequest request)
		{
			// HttpListener falls back to the ANSI code page when no charset is given; JSON is UTF-8
			bool charset = (request.ContentType ?? string.Empty).IndexOf("charset=", StringComparison.OrdinalIgnoreCase) >= 0;
			using (StreamReader reader = new StreamReader(request.InputStream, charset ? request.ContentEncoding : Encoding.UTF8))
			{
				string text = reader.ReadToEnd();
				return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
			}
		}

		private static void Reply(HttpListenerResponse response, int status, JObject body)
		{
			try
			{
				response.StatusCode = status;
				if (body != null)
				{
					byte[] data = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
					response.ContentType = "application/json; charset=utf-8";
					response.ContentLength64 = data.Length;
					response.OutputStream.Write(data, 0, data.Length);
				}
				response.Close();
			}
			catch (Exception)
			{
				// Extension went away mid-request
			}
		}

		private static void DeleteTemp(BrowserJob bj)
		{
			try
			{
				if (bj.TempFolder != null && Directory.Exists(bj.TempFolder))
				{
					Directory.Delete(bj.TempFolder, recursive: true);
				}
			}
			catch (Exception)
			{
			}
		}

		private static bool IsWebUrl(string url)
		{
			return Uri.TryCreate(url, UriKind.Absolute, out Uri uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
		}

		private static string FirstNonEmpty(params string[] values)
		{
			return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
		}

		private class BrowserJob
		{
			public DownloadJob Job;

			public GalleryInfo Info;

			public string TempFolder;

			public string[] Files = new string[0];

			public int Received;
		}

		private class BridgeException : Exception
		{
			public int Status { get; }

			public BridgeException(int status, string message)
				: base(message)
			{
				Status = status;
			}
		}
	}
}
