using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace cYo.Projects.ComicRack.Downloader
{
	// Minimal HTTP/1.1 client over BouncyCastle TLS. Windows 10's SChannel has no TLS 1.3 client,
	// so sites that only accept TLS 1.3 (e.g. MangaDex) fail with "Could not create SSL/TLS secure
	// channel". HttpSession falls back to this for those hosts. The server certificate is still
	// validated against the Windows trust store, and the host name is checked.
	internal static class ManagedTlsHttp
	{
		private const int MaxRedirects = 10;

		public static HttpResult Send(HttpSession session, string method, Uri uri, IDictionary<string, string> headers, byte[] body, string contentType, string referer, bool allowRedirect, CancellationToken cancel)
		{
			for (int redirects = 0; ; redirects++)
			{
				HttpResult result = SendOnce(session, method, uri, headers, body, contentType, referer, cancel);
				if (!allowRedirect || redirects >= MaxRedirects || !IsRedirect(result.StatusCode) || !result.Headers.TryGetValue("Location", out string location))
				{
					return result;
				}
				uri = new Uri(uri, location);
				if (result.StatusCode == 303 || ((result.StatusCode == 301 || result.StatusCode == 302) && method == "POST"))
				{
					method = "GET";
					body = null;
				}
				if (uri.Scheme != Uri.UriSchemeHttps)
				{
					return session.Send(method, uri.AbsoluteUri, headers, body, contentType, referer, true, cancel);
				}
			}
		}

		private static bool IsRedirect(int code) => code == 301 || code == 302 || code == 303 || code == 307 || code == 308;

		private static HttpResult SendOnce(HttpSession session, string method, Uri uri, IDictionary<string, string> headers, byte[] body, string contentType, string referer, CancellationToken cancel)
		{
			cancel.ThrowIfCancellationRequested();
			using (TcpClient tcp = new TcpClient())
			using (cancel.Register(tcp.Close))
			{
				tcp.ReceiveTimeout = session.Timeout * 2;
				tcp.SendTimeout = session.Timeout;
				try
				{
					var connect = tcp.ConnectAsync(uri.DnsSafeHost, uri.Port);
					if (!connect.Wait(session.Timeout, cancel))
					{
						throw new WebException("The operation has timed out.", WebExceptionStatus.Timeout);
					}
					using (Stream tls = ConnectTls(tcp.GetStream(), uri.IdnHost))
					{
						byte[] request = BuildRequest(session, method, uri, headers, body, contentType, referer);
						tls.Write(request, 0, request.Length);
						if (body != null && method != "GET" && method != "HEAD")
						{
							tls.Write(body, 0, body.Length);
						}
						tls.Flush();
						return ReadResponse(session, new BufferedStream(tls, 65536), method, uri);
					}
				}
				catch (Exception e) when (cancel.IsCancellationRequested && !(e is OperationCanceledException))
				{
					throw new OperationCanceledException(cancel);
				}
				catch (AggregateException e)
				{
					throw new WebException(e.InnerException?.Message ?? e.Message, e.InnerException, WebExceptionStatus.ConnectFailure, null);
				}
			}
		}

		private static Stream ConnectTls(Stream network, string host)
		{
			TlsClientProtocol protocol = new TlsClientProtocol(network);
			Client client = new Client(new BcTlsCrypto(new SecureRandom()), host);
			try
			{
				protocol.Connect(client);
			}
			catch (Exception e) when (client.CertificateError != null)
			{
				throw new WebException(client.CertificateError, e, WebExceptionStatus.TrustFailure, null);
			}
			catch (TlsException e)
			{
				throw new WebException("TLS handshake failed: " + e.Message, e, WebExceptionStatus.SecureChannelFailure, null);
			}
			return protocol.Stream;
		}

		private static byte[] BuildRequest(HttpSession session, string method, Uri uri, IDictionary<string, string> headers, byte[] body, string contentType, string referer)
		{
			// Same defaults HttpWebRequest gets in HttpSession; module headers replace them
			var list = new List<KeyValuePair<string, string>>();
			void Set(string name, string value)
			{
				int i = list.FindIndex(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
				if (i >= 0)
				{
					list[i] = new KeyValuePair<string, string>(list[i].Key, value);
				}
				else
				{
					list.Add(new KeyValuePair<string, string>(name, value));
				}
			}
			Set("Host", uri.IsDefaultPort ? uri.IdnHost : uri.IdnHost + ":" + uri.Port);
			Set("User-Agent", session.UserAgent);
			Set("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
			Set("Accept-Language", "en-US,en;q=0.9");
			Set("Accept-Encoding", "gzip, deflate");
			if (!string.IsNullOrEmpty(referer))
			{
				Set("Referer", referer);
			}
			if (headers != null)
			{
				foreach (KeyValuePair<string, string> h in headers)
				{
					string name = h.Key.ToLowerInvariant();
					if (name != "connection" && name != "content-length" && name != "transfer-encoding" && name != "accept-encoding")
					{
						Set(h.Key, h.Value);
					}
				}
			}
			string jar = session.Cookies.GetCookieHeader(uri);
			if (!string.IsNullOrEmpty(jar))
			{
				var custom = list.FirstOrDefault(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).Value;
				Set("Cookie", string.IsNullOrEmpty(custom) ? jar : custom + "; " + jar);
			}
			if (body != null && method != "GET" && method != "HEAD")
			{
				if (!list.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
				{
					Set("Content-Type", contentType ?? "application/x-www-form-urlencoded");
				}
				Set("Content-Length", body.Length.ToString());
			}
			Set("Connection", "close");

			StringBuilder sb = new StringBuilder();
			sb.Append(method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
			foreach (KeyValuePair<string, string> h in list)
			{
				sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
			}
			sb.Append("\r\n");
			return Encoding.UTF8.GetBytes(sb.ToString());
		}

		private static HttpResult ReadResponse(HttpSession session, Stream s, string method, Uri uri)
		{
			int status;
			string reason;
			var setCookies = new List<string>();
			HttpResult result = new HttpResult { Url = uri.AbsoluteUri };
			do
			{
				string statusLine = ReadLine(s) ?? throw new WebException("The server closed the connection without a response.", WebExceptionStatus.ReceiveFailure);
				string[] parts = statusLine.Split(new[] { ' ' }, 3);
				if (parts.Length < 2 || !int.TryParse(parts[1], out status))
				{
					throw new WebException("Invalid HTTP response: " + statusLine, WebExceptionStatus.ServerProtocolViolation);
				}
				reason = parts.Length > 2 ? parts[2] : string.Empty;
				result.Headers.Clear();
				setCookies.Clear();
				string line;
				while (!string.IsNullOrEmpty(line = ReadLine(s)))
				{
					int colon = line.IndexOf(':');
					if (colon <= 0)
					{
						continue;
					}
					string name = line.Substring(0, colon).Trim();
					string value = line.Substring(colon + 1).Trim();
					if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
					{
						setCookies.Add(value);
					}
					result.Headers[name] = result.Headers.TryGetValue(name, out string prev) ? prev + ", " + value : value;
				}
			}
			while (status >= 100 && status < 200);

			result.StatusCode = status;
			result.StatusDescription = reason;
			result.Headers.TryGetValue("Content-Type", out string ct);
			result.ContentType = ct;

			CookieContainer received = new CookieContainer();
			foreach (string cookie in setCookies)
			{
				try
				{
					received.SetCookies(uri, cookie);
					session.Cookies.SetCookies(uri, cookie);
				}
				catch (CookieException)
				{
				}
			}
			result.Cookies = received.GetCookies(uri);

			if (method != "HEAD" && status != 204 && status != 304)
			{
				result.Data = Decode(ReadBody(s, result.Headers), result.Headers);
			}
			return result;
		}

		private static byte[] ReadBody(Stream s, Dictionary<string, string> headers)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				if (headers.TryGetValue("Transfer-Encoding", out string te) && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					while (true)
					{
						string sizeLine = ReadLine(s) ?? throw new WebException("Truncated chunked response.", WebExceptionStatus.ReceiveFailure);
						int ext = sizeLine.IndexOf(';');
						int size = Convert.ToInt32((ext >= 0 ? sizeLine.Substring(0, ext) : sizeLine).Trim(), 16);
						if (size == 0)
						{
							while (!string.IsNullOrEmpty(ReadLine(s)))
							{
							}
							break;
						}
						CopyExactly(s, ms, size);
						ReadLine(s);
					}
				}
				else if (headers.TryGetValue("Content-Length", out string cl) && long.TryParse(cl, out long length))
				{
					CopyExactly(s, ms, length);
				}
				else
				{
					try
					{
						s.CopyTo(ms);
					}
					catch (TlsNoCloseNotifyException)
					{
						// Server closed the socket without close_notify: the body still ended here
					}
				}
				return ms.ToArray();
			}
		}

		private static byte[] Decode(byte[] data, Dictionary<string, string> headers)
		{
			if (data.Length == 0 || !headers.TryGetValue("Content-Encoding", out string encoding))
			{
				return data;
			}
			encoding = encoding.Trim().ToLowerInvariant();
			if (encoding != "gzip" && encoding != "deflate")
			{
				return data;
			}
			using (MemoryStream input = new MemoryStream(data))
			{
				// "deflate" is usually zlib-wrapped; DeflateStream wants the raw stream
				if (encoding == "deflate" && data.Length > 2 && data[0] == 0x78)
				{
					input.Position = 2;
				}
				using (Stream z = encoding == "gzip" ? (Stream)new GZipStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress))
				using (MemoryStream output = new MemoryStream())
				{
					z.CopyTo(output);
					return output.ToArray();
				}
			}
		}

		private static void CopyExactly(Stream s, Stream target, long count)
		{
			byte[] buffer = new byte[65536];
			while (count > 0)
			{
				int read = s.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
				if (read <= 0)
				{
					throw new WebException("The connection closed before the response was complete.", WebExceptionStatus.ReceiveFailure);
				}
				target.Write(buffer, 0, read);
				count -= read;
			}
		}

		private static string ReadLine(Stream s)
		{
			List<byte> bytes = new List<byte>(128);
			int b;
			while ((b = s.ReadByte()) >= 0)
			{
				if (b == '\n')
				{
					break;
				}
				if (b != '\r')
				{
					bytes.Add((byte)b);
				}
			}
			if (b < 0 && bytes.Count == 0)
			{
				return null;
			}
			return Encoding.UTF8.GetString(bytes.ToArray());
		}

		private class Client : DefaultTlsClient
		{
			private readonly string host;

			public string CertificateError { get; set; }

			public Client(TlsCrypto crypto, string host)
				: base(crypto)
			{
				this.host = host;
			}

			protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.DownTo(ProtocolVersion.TLSv12);

			protected override IList<ServerName> GetSniServerNames()
			{
				return IPAddress.TryParse(host, out _) ? null : new List<ServerName> { new ServerName(NameType.host_name, Encoding.ASCII.GetBytes(host)) };
			}

			protected override IList<ProtocolName> GetProtocolNames() => new List<ProtocolName> { ProtocolName.Http_1_1 };

			public override TlsAuthentication GetAuthentication() => new ServerAuthentication(this);

			private class ServerAuthentication : TlsAuthentication
			{
				private readonly Client client;

				public ServerAuthentication(Client client)
				{
					this.client = client;
				}

				public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
				{
					client.CertificateError = Validate(serverCertificate.Certificate, client.host);
					if (client.CertificateError != null)
					{
						throw new TlsFatalAlert(AlertDescription.bad_certificate, client.CertificateError);
					}
				}

				public TlsCredentials GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest) => null;
			}
		}

		// Returns null when the chain is trusted by Windows and the certificate covers host
		private static string Validate(Certificate chain, string host)
		{
			if (chain == null || chain.IsEmpty)
			{
				return "The server did not send a certificate.";
			}
			byte[] leafData = chain.GetCertificateAt(0).GetEncoded();
			using (X509Certificate2 leaf = new X509Certificate2(leafData))
			using (X509Chain x509 = new X509Chain())
			{
				x509.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
				x509.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")); // server authentication
				for (int i = 1; i < chain.Length; i++)
				{
					x509.ChainPolicy.ExtraStore.Add(new X509Certificate2(chain.GetCertificateAt(i).GetEncoded()));
				}
				if (!x509.Build(leaf))
				{
					string status = string.Join(", ", x509.ChainStatus.Select(st => st.StatusInformation.Trim()).Where(st => st.Length > 0));
					return $"The certificate for {host} is not trusted: {status}";
				}
			}
			var names = DnsNames(leafData).ToList();
			if (!names.Any(n => HostMatches(n, host)))
			{
				return $"The certificate is for {string.Join(", ", names)}, not {host}.";
			}
			return null;
		}

		private static IEnumerable<string> DnsNames(byte[] certificate)
		{
			var cert = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(certificate);
			var sans = cert.GetSubjectAlternativeNames();
			if (sans == null)
			{
				yield break;
			}
			foreach (var entry in sans)
			{
				if (entry.Count >= 2 && Convert.ToInt32(entry[0]) == Org.BouncyCastle.Asn1.X509.GeneralName.DnsName)
				{
					yield return entry[1].ToString();
				}
			}
		}

		private static bool HostMatches(string pattern, string host)
		{
			if (pattern.Equals(host, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			// "*.example.com" covers exactly one label
			if (pattern.StartsWith("*.", StringComparison.Ordinal))
			{
				int dot = host.IndexOf('.');
				return dot > 0 && host.Substring(dot + 1).Equals(pattern.Substring(2), StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
	}
}
