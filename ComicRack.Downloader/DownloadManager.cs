using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace cYo.Projects.ComicRack.Downloader
{
	// Queue of gallery downloads processed by a few background workers
	public class DownloadManager : IDisposable
	{
		private readonly object sync = new object();

		private readonly List<DownloadJob> jobs = new List<DownloadJob>();

		private readonly AutoResetEvent workAvailable = new AutoResetEvent(false);

		private readonly List<Thread> workers = new List<Thread>();

		private volatile bool disposed;

		private ModuleCatalog catalog;

		private readonly Func<ModuleCatalog> catalogFactory;

		public int MaxConcurrentJobs { get; }

		public Func<string, string> GetSetting { get; set; }

		public Func<DownloadJob, GalleryInfo, IList<ChapterEntry>, IList<ChapterEntry>> SelectChapters { get; set; }

		public event EventHandler<DownloadJob> JobAdded;

		public event EventHandler<DownloadJob> JobChanged;

		public event EventHandler<DownloadJob> JobRemoved;

		public event EventHandler<DownloadJob> JobCompleted;

		public DownloadManager(Func<ModuleCatalog> catalogFactory, int maxConcurrentJobs = 2)
		{
			this.catalogFactory = catalogFactory;
			MaxConcurrentJobs = Math.Max(1, maxConcurrentJobs);
		}

		public ModuleCatalog Catalog
		{
			get
			{
				lock (sync)
				{
					return catalog ?? (catalog = catalogFactory());
				}
			}
		}

		// Re-reads the module folders (e.g. after updating the modules)
		public void ReloadCatalog()
		{
			lock (sync)
			{
				catalog = null;
			}
		}

		public IReadOnlyList<DownloadJob> Jobs
		{
			get
			{
				lock (sync)
				{
					return jobs.ToList();
				}
			}
		}

		public DownloadJob Add(string url, DownloadOptions options)
		{
			DownloadJob job = new DownloadJob(url, options.Clone());
			job.Changed += Job_Changed;
			lock (sync)
			{
				jobs.Add(job);
				EnsureWorkers();
			}
			JobAdded?.Invoke(this, job);
			workAvailable.Set();
			return job;
		}

		// A job whose pages are supplied from outside (the browser extension) instead of a worker;
		// it is listed like any other job but never picked up by the workers while running
		public DownloadJob AddExternal(string url, DownloadOptions options)
		{
			DownloadJob job = new DownloadJob(url, options.Clone());
			job.Changed += Job_Changed;
			job.Status = DownloadStatus.Running;
			lock (sync)
			{
				jobs.Add(job);
			}
			JobAdded?.Invoke(this, job);
			return job;
		}

		// Hands an external job to the workers (e.g. a series page the browser can't download in one go)
		public void QueueExternal(DownloadJob job)
		{
			job.Reset();
			lock (sync)
			{
				EnsureWorkers();
			}
			workAvailable.Set();
		}

		public void CompleteExternal(DownloadJob job)
		{
			JobCompleted?.Invoke(this, job);
		}

		public void Retry(DownloadJob job)
		{
			if (job.IsFinished)
			{
				job.Reset();
				workAvailable.Set();
			}
		}

		public void Remove(DownloadJob job)
		{
			job.Cancel();
			lock (sync)
			{
				jobs.Remove(job);
			}
			job.Changed -= Job_Changed;
			JobRemoved?.Invoke(this, job);
		}

		public void ClearFinished()
		{
			foreach (DownloadJob job in Jobs.Where(j => j.Status == DownloadStatus.Completed))
			{
				Remove(job);
			}
		}

		private void Job_Changed(object sender, EventArgs e)
		{
			JobChanged?.Invoke(this, (DownloadJob)sender);
		}

		private void EnsureWorkers()
		{
			while (workers.Count < MaxConcurrentJobs)
			{
				// Larger stack: MoonSharp and deeply nested HTML both recurse
				Thread t = new Thread(WorkerLoop, 16 * 1024 * 1024)
				{
					IsBackground = true,
					Name = "Downloader " + (workers.Count + 1),
					Priority = ThreadPriority.BelowNormal
				};
				workers.Add(t);
				t.Start();
			}
		}

		private DownloadJob TakeNext()
		{
			lock (sync)
			{
				DownloadJob job = jobs.FirstOrDefault(j => j.Status == DownloadStatus.Queued);
				if (job != null)
				{
					job.Status = DownloadStatus.Running;
				}
				return job;
			}
		}

		private void WorkerLoop()
		{
			while (!disposed)
			{
				DownloadJob job = TakeNext();
				if (job == null)
				{
					workAvailable.WaitOne(1000);
					continue;
				}
				GalleryDownloader downloader = new GalleryDownloader(Catalog, job)
				{
					Enqueue = url => Add(url, job.Options),
					GetSetting = GetSetting,
					SelectChapters = SelectChapters
				};
				downloader.Run();
				JobCompleted?.Invoke(this, job);
				// Wake another worker in case several jobs are waiting
				workAvailable.Set();
			}
		}

		public void Dispose()
		{
			disposed = true;
			foreach (DownloadJob job in Jobs)
			{
				job.Cancel();
			}
			workAvailable.Set();
		}
	}
}
