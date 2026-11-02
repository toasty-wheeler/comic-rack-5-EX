using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MoonSharp.Interpreter;

namespace cYo.Projects.ComicRack.Downloader
{
	public class GalleryInfo
	{
		public Dictionary<string, string> Fields { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public string Url { get; set; }

		public string ModuleName { get; set; }

		public bool AdultSite { get; set; }

		public string this[string key] => Fields.TryGetValue(key, out string v) ? v : string.Empty;
	}

	public class ChapterEntry
	{
		public string Url { get; set; }

		public string Title { get; set; }

		public string Volume { get; set; }

		public string Chapter { get; set; }

		public string Language { get; set; }
	}

	internal class PageRequest
	{
		public int Index { get; set; }

		public string Url { get; set; }

		public string Referer { get; set; }

		public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public List<string> BackupUrls { get; } = new List<string>();

		public string FileExtensionHint { get; set; }
	}

	// Runs one download job: executes the site module (GetInfo / GetChapters / GetPages),
	// downloads the images and packs every gallery or chapter into the selected format.
	public class GalleryDownloader
	{
		private readonly ModuleCatalog catalog;

		private readonly DownloadJob job;

		private readonly HttpSession session = new HttpSession();

		private CancellationToken cancel;

		public Action<string> Enqueue { get; set; }

		public Func<string, string> GetSetting { get; set; }

		// Lets the user pick chapters of a series; returns the chapters to download
		public Func<DownloadJob, GalleryInfo, IList<ChapterEntry>, IList<ChapterEntry>> SelectChapters { get; set; }

		public GalleryDownloader(ModuleCatalog catalog, DownloadJob job)
		{
			this.catalog = catalog;
			this.job = job;
		}

		public void Run()
		{
			cancel = job.Cancellation.Token;
			string tempRoot = null;
			try
			{
				job.StatusText = "Starting";
				SiteInfo site = catalog.FindSite(job.Url);
				string moduleFile = site?.ModuleFile ?? catalog.GenericModuleFile;
				if (moduleFile == null)
				{
					throw new ModuleException("No module supports this website");
				}
				job.Site = site?.Name ?? "Generic";
				job.WriteLog($"Using module {Path.GetFileName(moduleFile)} for {UrlUtility.Host(job.Url)}");

				int enqueued = 0;
				ModuleScript script = new ModuleScript(catalog, moduleFile, session)
				{
					Cancel = cancel,
					Log = job.WriteLog,
					GetSetting = name => GetSetting?.Invoke(name) ?? DefaultSetting(name),
					Enqueue = url =>
					{
						enqueued++;
						job.WriteLog("Queued " + url);
						Enqueue?.Invoke(url);
					}
				};
				script.Register();
				string moduleName = script.SelectModule(UrlUtility.Host(job.Url));
				if (moduleName != null && site != null)
				{
					job.Site = moduleName;
				}

				job.StatusText = "Reading gallery information";
				script.Call("__reset_lists");
				script.Begin(job.Url);
				script.CallIfDefined("GetInfo");
				GalleryInfo info = ExportInfo(script);
				info.Url = job.Url;
				info.ModuleName = job.Site;
				info.AdultSite = site?.Adult ?? false;
				if (!string.IsNullOrWhiteSpace(info["Title"]))
				{
					job.Title = info["Title"];
				}
				if (IsTrue(info["Ignore"]))
				{
					// Tag/search/favorites pages: the module queued the galleries it found
					job.Progress = 1f;
					job.StatusText = enqueued > 0 ? $"Queued {enqueued} galleries" : "Nothing to download";
					job.Status = DownloadStatus.Completed;
					return;
				}

				List<ChapterEntry> chapters = new List<ChapterEntry>();
				if (script.CallIfDefined("GetChapters"))
				{
					chapters = ExportChapters(script);
				}
				if (!script.Has("GetPages"))
				{
					throw new ModuleException("The module can't download pages from this URL");
				}

				tempRoot = Path.Combine(Path.GetTempPath(), "ComicRackDownloads", job.Id.ToString("N"));
				if (chapters.Count == 0)
				{
					DownloadGallery(script, info, null, job.Url, tempRoot, 0f, 1f);
				}
				else
				{
					job.WriteLog($"Found {chapters.Count} chapters");
					if (SelectChapters != null && chapters.Count > 1)
					{
						job.StatusText = "Waiting for chapter selection";
						chapters = SelectChapters(job, info, chapters)?.ToList() ?? new List<ChapterEntry>();
						if (chapters.Count == 0)
						{
							throw new OperationCanceledException();
						}
					}
					for (int i = 0; i < chapters.Count; i++)
					{
						job.StatusText = $"Chapter {i + 1} of {chapters.Count}";
						DownloadGallery(script, info, chapters[i], chapters[i].Url, Path.Combine(tempRoot, i.ToString()), (float)i / chapters.Count, 1f / chapters.Count);
					}
				}
				job.Progress = 1f;
				job.StatusText = job.OutputFiles.Length == 1 ? "Done" : $"Done ({job.OutputFiles.Length} files)";
				job.Status = DownloadStatus.Completed;
			}
			catch (Exception e) when (e is OperationCanceledException || cancel.IsCancellationRequested)
			{
				job.StatusText = "Canceled";
				job.Status = DownloadStatus.Canceled;
			}
			catch (Exception e)
			{
				string message = e is ModuleException || e is InterpreterException ? e.Message : e.GetType().Name + ": " + e.Message;
				job.Error = message;
				job.WriteLog("Error: " + (e is ModuleException || e is InterpreterException ? message : e.ToString()));
				job.StatusText = "Failed: " + FirstLine(message);
				job.Status = DownloadStatus.Failed;
			}
			finally
			{
				if (tempRoot != null)
				{
					try
					{
						Directory.Delete(tempRoot, recursive: true);
					}
					catch (Exception)
					{
					}
				}
			}
		}

		private void DownloadGallery(ModuleScript script, GalleryInfo info, ChapterEntry chapter, string url, string tempFolder, float progressStart, float progressRange)
		{
			script.Call("__reset_pages");
			script.Begin(url);
			script.Call("GetPages");
			int count = (int)script.Call("__page_count").Number;
			if (count == 0)
			{
				throw new ModuleException("No pages found" + (chapter != null ? " in chapter " + chapter.Title : string.Empty));
			}
			job.WriteLog($"{(chapter != null ? chapter.Title + ": " : string.Empty)}{count} pages");
			Directory.CreateDirectory(tempFolder);
			if (chapter == null)
			{
				job.StatusText = $"Downloading {count} pages";
			}

			string[] files = new string[count];
			int done = 0;
			int failed = 0;
			List<Task> tasks = new List<Task>();
			using (SemaphoreSlim slots = new SemaphoreSlim(Math.Max(1, job.Options.ParallelImages)))
			{
				for (int i = 0; i < count; i++)
				{
					cancel.ThrowIfCancellationRequested();
					// The script isn't thread safe: resolve page URLs here, download in parallel
					PageRequest request = ExportPage(script.Call("__prepare_page", i + 1).Table, i, url);
					slots.Wait(cancel);
					tasks.Add(Task.Run(() =>
					{
						try
						{
							files[request.Index] = DownloadImage(request, tempFolder);
						}
						catch (OperationCanceledException)
						{
						}
						catch (Exception e)
						{
							Interlocked.Increment(ref failed);
							job.WriteLog($"Page {request.Index + 1} failed: {e.Message}");
						}
						finally
						{
							slots.Release();
							int n = Interlocked.Increment(ref done);
							job.Progress = progressStart + progressRange * 0.95f * n / count;
						}
					}));
				}
				Task.WaitAll(tasks.ToArray());
			}
			cancel.ThrowIfCancellationRequested();
			if (failed > 0 && !job.Options.SkipFailedPages)
			{
				throw new ModuleException($"{failed} of {count} pages could not be downloaded");
			}

			job.StatusText = "Packing";
			string output = ComicPackager.Pack(files.Where(f => f != null).ToList(), info, chapter, job.Options);
			job.WriteLog("Saved " + output);
			job.AddOutput(output);
			job.Progress = progressStart + progressRange;
			try
			{
				Directory.Delete(tempFolder, recursive: true);
			}
			catch (Exception)
			{
			}
		}

		private string DownloadImage(PageRequest request, string folder)
		{
			List<string> urls = new List<string> { request.Url };
			urls.AddRange(request.BackupUrls);
			Exception last = null;
			foreach (string url in urls.Where(u => !string.IsNullOrWhiteSpace(u)))
			{
				for (int attempt = 0; attempt <= job.Options.Retries; attempt++)
				{
					cancel.ThrowIfCancellationRequested();
					try
					{
						byte[] data;
						string contentType = null;
						if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
						{
							data = DecodeDataUrl(url, out contentType);
						}
						else
						{
							Dictionary<string, string> headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
							if (!headers.ContainsKey("Accept"))
							{
								headers["Accept"] = "image/avif,image/webp,image/apng,image/*,*/*;q=0.8";
							}
							HttpResult r = session.Send("GET", url, headers, null, null, request.Referer, true, cancel);
							if (!r.IsSuccess)
							{
								throw new IOException($"HTTP {r.StatusCode} {r.StatusDescription}");
							}
							data = r.Data;
							contentType = r.ContentType;
						}
						string ext = ImageType.GetExtension(data, contentType, request.FileExtensionHint ?? url);
						if (ext == null)
						{
							throw new IOException("Not an image (" + (contentType ?? "unknown type") + ")");
						}
						string file = Path.Combine(folder, (request.Index + 1).ToString("D4") + ext);
						File.WriteAllBytes(file, data);
						return file;
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception e)
					{
						last = e;
						if (attempt < job.Options.Retries)
						{
							cancel.WaitHandle.WaitOne(500 * (attempt + 1));
						}
					}
				}
			}
			throw last ?? new IOException("No image URL");
		}

		private static byte[] DecodeDataUrl(string url, out string contentType)
		{
			int comma = url.IndexOf(',');
			string header = url.Substring(5, comma - 5);
			contentType = header.Split(';')[0];
			string payload = url.Substring(comma + 1);
			return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase) ? Convert.FromBase64String(payload) : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
		}

		private static GalleryInfo ExportInfo(ModuleScript script)
		{
			GalleryInfo info = new GalleryInfo();
			foreach (TablePair p in script.Call("__export_info").Table.Pairs)
			{
				info.Fields[p.Key.String] = p.Value.ToPrintString().Trim();
			}
			return info;
		}

		private static List<ChapterEntry> ExportChapters(ModuleScript script)
		{
			return script.Call("__export_chapters").Table.Values.Select(v => new ChapterEntry
			{
				Url = v.Table.Get("Url").ToPrintString(),
				Title = v.Table.Get("Title").ToPrintString().Trim(),
				Volume = v.Table.Get("Volume").ToPrintString(),
				Chapter = v.Table.Get("Chapter").ToPrintString(),
				Language = v.Table.Get("Language").ToPrintString()
			}).Where(c => !string.IsNullOrWhiteSpace(c.Url)).ToList();
		}

		private static PageRequest ExportPage(Table t, int index, string pageUrl)
		{
			PageRequest r = new PageRequest
			{
				Index = index,
				Url = t.Get("Url").ToPrintString(),
				Referer = t.Get("Referer").IsNil() ? pageUrl : t.Get("Referer").ToPrintString(),
				FileExtensionHint = t.Get("FileExtensionHint").IsNil() ? null : t.Get("FileExtensionHint").ToPrintString()
			};
			foreach (TablePair p in t.Get("Headers").Table.Pairs)
			{
				r.Headers[p.Key.ToPrintString()] = p.Value.ToPrintString();
			}
			r.BackupUrls.AddRange(t.Get("BackupUrls").Table.Values.Select(v => v.ToPrintString()));
			return r;
		}

		private static string DefaultSetting(string name)
		{
			switch (name)
			{
				case "Downloads.MaxScrapingDepth":
					return "1";
				default:
					return null;
			}
		}

		private static bool IsTrue(string s)
		{
			return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
		}

		private static string FirstLine(string s)
		{
			int i = s.IndexOfAny(new[] { '\r', '\n' });
			return i < 0 ? s : s.Substring(0, i);
		}
	}
}
