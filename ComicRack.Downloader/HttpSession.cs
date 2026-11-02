using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace cYo.Projects.ComicRack.Downloader
{
	public class HttpResult
	{
		public int StatusCode { get; set; }

		public string StatusDescription { get; set; }

		public string Url { get; set; }

		public byte[] Data { get; set; }

		public string ContentType { get; set; }

		public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public CookieCollection Cookies { get; set; } = new CookieCollection();

		public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;

		private string text;

		public string Text => text ?? (text = HttpSession.Decode(Data, ContentType));
	}

	// HTTP client used by the module scripts and the image downloader. All downloads share
	// one cookie jar, so logins/age gates set by one gallery apply to the next.
	public class HttpSession
	{
		public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36";

		private static readonly CookieContainer sharedCookies = new CookieContainer(4096, 300, 16384);

		public CookieContainer Cookies { get; } = sharedCookies;

		public string UserAgent { get; set; } = DefaultUserAgent;

		// Sent by modules (as API_CLIENT) to APIs that ask clients to identify themselves, e.g. MangaDex
		public const string ClientUserAgent = "ComicRackCE-Downloader/1.0";

		public int Timeout { get; set; } = 30000;

		static HttpSession()
		{
			ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
			ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 16);
			ServicePointManager.Expect100Continue = false;
		}

		public HttpResult Request(string method, string url, IDictionary<string, string> headers, string body, string contentType, string referer, bool allowRedirect, CancellationToken cancel)
		{
			byte[] data = body == null ? null : Encoding.UTF8.GetBytes(body);
			return Send(method, url, headers, data, contentType, referer, allowRedirect, cancel);
		}

		public HttpResult Send(string method, string url, IDictionary<string, string> headers, byte[] body, string contentType, string referer, bool allowRedirect, CancellationToken cancel)
		{
			cancel.ThrowIfCancellationRequested();
			Uri uri = new Uri(url);
			if (uri.Scheme == Uri.UriSchemeHttps && managedTlsHosts.ContainsKey(uri.Host))
			{
				return ManagedTlsHttp.Send(this, method, uri, headers, body, contentType, referer, allowRedirect, cancel);
			}
			try
			{
				return SendNative(method, url, headers, body, contentType, referer, allowRedirect, cancel);
			}
			catch (WebException e) when (e.Status == WebExceptionStatus.SecureChannelFailure && uri.Scheme == Uri.UriSchemeHttps)
			{
				// Most likely a TLS 1.3-only server, which Windows 10 can't talk to; remember the host
				managedTlsHosts[uri.Host] = true;
				return ManagedTlsHttp.Send(this, method, uri, headers, body, contentType, referer, allowRedirect, cancel);
			}
		}

		private static readonly ConcurrentDictionary<string, bool> managedTlsHosts = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

		private HttpResult SendNative(string method, string url, IDictionary<string, string> headers, byte[] body, string contentType, string referer, bool allowRedirect, CancellationToken cancel)
		{
			HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
			req.Method = method;
			req.CookieContainer = Cookies;
			req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
			req.AllowAutoRedirect = allowRedirect;
			req.Timeout = Timeout;
			req.ReadWriteTimeout = Timeout * 2;
			req.UserAgent = UserAgent;
			req.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";
			req.Headers["Accept-Language"] = "en-US,en;q=0.9";
			if (!string.IsNullOrEmpty(referer))
			{
				req.Referer = referer;
			}
			if (headers != null)
			{
				foreach (KeyValuePair<string, string> h in headers)
				{
					SetHeader(req, h.Key, h.Value);
				}
			}
			using (cancel.Register(req.Abort))
			{
				if (body != null && method != "GET" && method != "HEAD")
				{
					if (headers == null || !headers.Keys.Any(k => k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
					{
						req.ContentType = contentType ?? "application/x-www-form-urlencoded";
					}
					req.ContentLength = body.Length;
					using (Stream s = req.GetRequestStream())
					{
						s.Write(body, 0, body.Length);
					}
				}
				HttpWebResponse response;
				try
				{
					response = (HttpWebResponse)req.GetResponse();
				}
				catch (WebException e) when (e.Response is HttpWebResponse)
				{
					response = (HttpWebResponse)e.Response;
				}
				catch (WebException) when (cancel.IsCancellationRequested)
				{
					throw new OperationCanceledException(cancel);
				}
				using (response)
				{
					HttpResult result = new HttpResult
					{
						StatusCode = (int)response.StatusCode,
						StatusDescription = response.StatusDescription,
						Url = response.ResponseUri?.AbsoluteUri ?? url,
						ContentType = response.ContentType,
						Cookies = response.Cookies
					};
					foreach (string key in response.Headers.AllKeys)
					{
						result.Headers[key] = response.Headers[key];
					}
					if (method != "HEAD")
					{
						using (Stream s = response.GetResponseStream())
						using (MemoryStream ms = new MemoryStream())
						{
							s.CopyTo(ms);
							result.Data = ms.ToArray();
						}
					}
					return result;
				}
			}
		}

		private static void SetHeader(HttpWebRequest req, string name, string value)
		{
			try
			{
				switch (name.ToLowerInvariant())
				{
					case "accept":
						req.Accept = value;
						break;
					case "referer":
						req.Referer = value;
						break;
					case "user-agent":
						req.UserAgent = value;
						break;
					case "content-type":
						req.ContentType = value;
						break;
					case "host":
						req.Host = value;
						break;
					case "connection":
					case "content-length":
					case "transfer-encoding":
						break;
					default:
						req.Headers[name] = value;
						break;
				}
			}
			catch (ArgumentException)
			{
			}
		}

		public IEnumerable<Cookie> GetCookies(string url)
		{
			if (!Uri.TryCreate(url, UriKind.Absolute, out Uri u))
			{
				return Enumerable.Empty<Cookie>();
			}
			return Cookies.GetCookies(u).Cast<Cookie>().ToList();
		}

		public void SetCookie(string domain, string name, string value)
		{
			domain = (domain ?? string.Empty).Trim();
			if (domain.Contains("://"))
			{
				domain = new Uri(domain).Host;
			}
			if (domain.Length == 0)
			{
				return;
			}
			Cookies.Add(new Cookie(name, value, "/", domain.StartsWith(".") ? domain : "." + domain));
		}

		public static string Decode(byte[] data, string contentType)
		{
			if (data == null || data.Length == 0)
			{
				return string.Empty;
			}
			Encoding encoding = null;
			Match m = Regex.Match(contentType ?? string.Empty, "charset=\"?([\\w-]+)", RegexOptions.IgnoreCase);
			if (!m.Success)
			{
				// <meta charset="..."> / <meta http-equiv="Content-Type" content="...charset=...">
				string head = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 4096));
				m = Regex.Match(head, "<meta[^>]+charset=[\"']?([\\w-]+)", RegexOptions.IgnoreCase);
			}
			if (m.Success)
			{
				try
				{
					encoding = Encoding.GetEncoding(m.Groups[1].Value);
				}
				catch (ArgumentException)
				{
				}
			}
			string s = (encoding ?? Encoding.UTF8).GetString(data);
			return s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;
		}
	}
}
