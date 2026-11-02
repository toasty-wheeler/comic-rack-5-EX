using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using HtmlAgilityPack;
using MoonSharp.Interpreter;
using Newtonsoft.Json.Linq;

namespace cYo.Projects.ComicRack.Downloader
{
	// Opaque reference to a C# object (HTML node, JSON token, JS engine/value) handed to Lua
	public sealed class LuaHandle
	{
		public object Value { get; }

		public LuaHandle(object value)
		{
			Value = value;
		}
	}

	// One MoonSharp script running a single HDoujin module file
	public class ModuleScript
	{
		private static readonly string prelude;

		private readonly Script script;

		private readonly ModuleCatalog catalog;

		private readonly HttpSession session;

		private readonly Dictionary<string, string> documentCache = new Dictionary<string, string>();

		public string FileName { get; }

		public Action<string> Log { get; set; }

		public Action<string> Enqueue { get; set; }

		public Func<string, string> GetSetting { get; set; }

		public CancellationToken Cancel { get; set; }

		static ModuleScript()
		{
			UserData.RegisterType<LuaHandle>();
			using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Prelude.lua"))
			using (StreamReader reader = new StreamReader(s))
			{
				prelude = reader.ReadToEnd();
			}
		}

		public ModuleScript(ModuleCatalog catalog, string fileName, HttpSession session)
		{
			this.catalog = catalog;
			this.session = session;
			FileName = fileName;
			script = new Script(CoreModules.Preset_Complete);
			script.Options.DebugPrint = s => Log?.Invoke(s);
			script.Globals["host"] = CreateHost();
			script.DoString(prelude, null, "prelude");
			script.DoString(File.ReadAllText(fileName), null, Path.GetFileNameWithoutExtension(fileName));
		}

		public Table Globals => script.Globals;

		public bool Has(string function)
		{
			return script.Globals.Get(function).Type == DataType.Function;
		}

		public DynValue Call(string function, params object[] args)
		{
			Cancel.ThrowIfCancellationRequested();
			try
			{
				return script.Call(script.Globals.Get(function), args);
			}
			catch (InterpreterException e)
			{
				string details = e.DecoratedMessage ?? e.Message;
				if (e.CallStack != null && e.CallStack.Count > 0)
				{
					details += Environment.NewLine + string.Join(Environment.NewLine, e.CallStack.Select(w => "   at " + (w.Name ?? "?") + " " + (w.Location?.FormatLocation(script) ?? string.Empty)));
				}
				throw new ModuleException(e.Message, e)
				{
					Details = details
				};
			}
		}

		public bool CallIfDefined(string function)
		{
			if (!Has(function))
			{
				return false;
			}
			Call(function);
			return true;
		}

		public void Register()
		{
			CallIfDefined("Register");
		}

		public string SelectModule(string host)
		{
			DynValue v = Call("__select_module", host);
			return v.IsNil() ? null : v.String;
		}

		public IEnumerable<string> GetDomains()
		{
			Table t = Call("__domains").Table;
			return t.Values.Select(v => v.ToPrintString());
		}

		public void Begin(string url)
		{
			Call("__begin", url);
		}

		#region Host functions

		private Table CreateHost()
		{
			Table host = new Table(script);
			void Fn(string name, Func<CallbackArguments, DynValue> f)
			{
				host[name] = DynValue.NewCallback((ctx, args) =>
				{
					Cancel.ThrowIfCancellationRequested();
					try
					{
						return f(args);
					}
					catch (ScriptRuntimeException)
					{
						throw;
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception e)
					{
						throw new ScriptRuntimeException(e.Message);
					}
				}, name);
			}

			// HTML / XPath
			Fn("html_parse", a => Handle(HtmlApi.Parse(Str(a, 0))));
			Fn("xpath_value", a => DynValue.NewString(HtmlApi.SelectValues(Node(a, 0), Str(a, 1)).FirstOrDefault() ?? string.Empty));
			Fn("xpath_values", a => Array(HtmlApi.SelectValues(Node(a, 0), Str(a, 1)).Select(DynValue.NewString)));
			Fn("xpath_nodes", a => Array(HtmlApi.SelectNodes(Node(a, 0), Str(a, 1)).Select(Handle)));
			Fn("node_attr", a => DynValue.NewString(HtmlEntity.DeEntitize(Node(a, 0)?.GetAttributeValue(Str(a, 1), string.Empty) ?? string.Empty)));
			Fn("node_text", a => DynValue.NewString(HtmlApi.Text(Node(a, 0))));
			Fn("node_html", a => DynValue.NewString(a[1].CastToBool() ? Node(a, 0)?.OuterHtml ?? string.Empty : Node(a, 0)?.InnerHtml ?? string.Empty));

			// JSON / JSONPath
			Fn("json_parse", a => Handle(JsonApi.Parse(Str(a, 0))));
			Fn("json_tokens", a => Array(JsonApi.Select(Token(a, 0), Str(a, 1)).Select(Handle)));
			Fn("json_values", a => Array(JsonApi.Select(Token(a, 0), Str(a, 1)).Take(a[2].CastToBool() ? 1 : int.MaxValue).Select(t => DynValue.NewString(JsonApi.ToText(t)))));
			Fn("json_string", a => DynValue.NewString(JsonApi.ToText(Token(a, 0))));
			Fn("json_kind", a => DynValue.NewString(Token(a, 0) is JContainer ? "container" : "value"));
			Fn("json_count", a => DynValue.NewNumber(JsonApi.Count(Token(a, 0))));
			Fn("json_keys", a => Array((Token(a, 0) as JObject)?.Properties().Select(p => DynValue.NewString(p.Name)) ?? Enumerable.Empty<DynValue>()));
			Fn("json_key", a => DynValue.NewString((Token(a, 0)?.Parent as JProperty)?.Name ?? string.Empty));
			Fn("json_children", a => Array(JsonApi.Children(Token(a, 0)).Select(Handle)));
			Fn("json_index", a => Handle(JsonApi.Index(Token(a, 0), a[1].Type == DataType.Number ? (object)(int)a[1].Number : a[1].ToPrintString())));

			// JavaScript
			Fn("js_new", a => Handle(new JavaScriptEngine(session.UserAgent)));
			Fn("js_exec", a => Handle(((JavaScriptEngine)((LuaHandle)a[0].UserData.Object).Value).Execute(Str(a, 1))));
			Fn("js_tostring", a => DynValue.NewString(JavaScriptEngine.ToText(Unwrap(a[0]))));
			Fn("js_tojson", a => DynValue.NewString(JavaScriptEngine.ToJson(Unwrap(a[0]))));
			Fn("js_deobfuscate", a => DynValue.NewString(JavaScriptEngine.Deobfuscate(Str(a, 0))));

			// HTTP
			Fn("http", Http);
			Fn("document", a => DynValue.NewString(GetDocument(Str(a, 0))));
			Fn("cookies", a => Array(session.GetCookies(Str(a, 0)).Select(c =>
			{
				Table t = new Table(script);
				t["Name"] = c.Name;
				t["Value"] = c.Value;
				t["Domain"] = c.Domain;
				return DynValue.NewTable(t);
			})));
			Fn("set_cookie", a =>
			{
				session.SetCookie(Str(a, 0), Str(a, 1), Str(a, 2));
				return DynValue.Nil;
			});
			Fn("form_encode", a => DynValue.NewString(HttpUtility.UrlEncode(Str(a, 0))));

			// Text helpers
			Fn("regex", a =>
			{
				Match m = Regex.Match(Str(a, 0), Str(a, 1));
				return DynValue.NewString(m.Success ? m.Groups[(int)a[2].Number].Value : string.Empty);
			});
			Fn("regex_many", a => Array(Regex.Matches(Str(a, 0), Str(a, 1)).Cast<Match>().Select(m => DynValue.NewString(m.Groups[(int)a[2].Number].Value))));
			Fn("regex_matches", a => Array(Regex.Matches(Str(a, 0), Str(a, 1)).Cast<Match>().Select(m =>
			{
				// match[0] is the whole match, match[1..n] the groups
				Table t = new Table(script);
				for (int i = 0; i < m.Groups.Count; i++)
				{
					t[i] = m.Groups[i].Value;
				}
				return DynValue.NewTable(t);
			})));
			Fn("regex_replace", a => DynValue.NewString(Regex.Replace(Str(a, 0), Str(a, 1), Str(a, 2))));
			Fn("regex_ismatch", a => DynValue.NewBoolean(Regex.IsMatch(Str(a, 0), Str(a, 1))));
			Fn("regex_escape", a => DynValue.NewString(Regex.Escape(Str(a, 0))));
			Fn("format", a =>
			{
				object[] args = a[1].Table.Values.Select(v => (object)v.ToPrintString()).ToArray();
				return DynValue.NewString(string.Format(CultureInfo.InvariantCulture, Str(a, 0), args));
			});
			Fn("b64decode", a => DynValue.NewString(Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(Str(a, 0))))));
			Fn("b64encode", a => DynValue.NewString(Convert.ToBase64String(Encoding.UTF8.GetBytes(Str(a, 0)))));
			Fn("uri_encode", a => DynValue.NewString(Uri.EscapeDataString(Str(a, 0))));
			Fn("uri_decode", a => DynValue.NewString(Uri.UnescapeDataString(Str(a, 0))));
			Fn("unescape", a => DynValue.NewString(UrlUtility.Unescape(Str(a, 0))));
			Fn("language_id", a => DynValue.NewString(UrlUtility.LanguageId(Str(a, 0))));
			Fn("language_name", a => DynValue.NewString(UrlUtility.LanguageName(Str(a, 0))));
			Fn("sleep", a =>
			{
				Cancel.WaitHandle.WaitOne(Math.Max(0, Math.Min(60000, (int)a[0].Number)));
				return DynValue.Nil;
			});
			Fn("log", a =>
			{
				Log?.Invoke(Str(a, 0));
				return DynValue.Nil;
			});

			// URLs
			Fn("resolve", a => DynValue.NewString(UrlUtility.Resolve(Str(a, 0), Str(a, 1))));
			Fn("url_root", a => DynValue.NewString(UrlUtility.Root(Str(a, 0))));
			Fn("url_host", a => DynValue.NewString(UrlUtility.Host(Str(a, 0))));
			Fn("url_domain", a => DynValue.NewString(UrlUtility.Domain(Str(a, 0))));
			Fn("get_param", a => DynValue.NewString(UrlUtility.GetParameter(Str(a, 0), Str(a, 1))));
			Fn("set_param", a => DynValue.NewString(UrlUtility.SetParameter(Str(a, 0), Str(a, 1), Str(a, 2))));
			Fn("strip_params", a => DynValue.NewString(UrlUtility.StripParameters(Str(a, 0))));

			// Module environment
			Fn("require", a =>
			{
				string file = catalog.FindModuleFile(Str(a, 0));
				return file == null ? DynValue.Nil : DynValue.NewString(File.ReadAllText(file));
			});
			Fn("enqueue", a =>
			{
				Enqueue?.Invoke(Str(a, 0));
				return DynValue.Nil;
			});
			Fn("setting", a =>
			{
				string v = GetSetting?.Invoke(Str(a, 0));
				return v == null ? DynValue.Nil : DynValue.NewString(v);
			});
			return host;
		}

		private DynValue Http(CallbackArguments a)
		{
			string method = Str(a, 0);
			string url = Str(a, 1);
			Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (a[2].Type == DataType.Table)
			{
				foreach (TablePair p in a[2].Table.Pairs)
				{
					headers[p.Key.ToPrintString()] = p.Value.ToPrintString();
				}
			}
			string body = a[3].IsNil() ? null : a[3].ToPrintString();
			string contentType = a[4].IsNil() ? null : a[4].ToPrintString();
			string referer = a[5].IsNil() ? null : a[5].ToPrintString();
			bool allowRedirect = a[6].IsNil() || a[6].CastToBool();
			bool throwOnError = a[7].CastToBool();
			Log?.Invoke($"{method} {url}");
			if (string.IsNullOrWhiteSpace(url))
			{
				throw new ScriptRuntimeException($"{method} request without URL");
			}
			HttpResult r = session.Request(method, url, headers, body, contentType, referer, allowRedirect, Cancel);
			if (throwOnError && !r.IsSuccess)
			{
				throw new ScriptRuntimeException($"HTTP {r.StatusCode} {r.StatusDescription} ({url})");
			}
			Table t = new Table(script);
			t["Body"] = r.Text;
			t["StatusCode"] = r.StatusCode;
			t["Url"] = r.Url;
			Table h = new Table(script);
			foreach (KeyValuePair<string, string> header in r.Headers)
			{
				h[header.Key] = header.Value;
			}
			t["Headers"] = h;
			Table cookies = new Table(script);
			int i = 1;
			foreach (System.Net.Cookie c in r.Cookies)
			{
				Table ct = new Table(script);
				ct["Name"] = c.Name;
				ct["Value"] = c.Value;
				ct["Domain"] = c.Domain;
				cookies[i++] = ct;
			}
			t["Cookies"] = cookies;
			return DynValue.NewTable(t);
		}

		// The page documents (url/doc/dom) are cached so GetInfo and GetPages share one download
		private string GetDocument(string url)
		{
			if (!documentCache.TryGetValue(url, out string text))
			{
				HttpResult r = session.Request("GET", url, null, null, null, null, true, Cancel);
				if (!r.IsSuccess)
				{
					throw new ScriptRuntimeException($"HTTP {r.StatusCode} {r.StatusDescription} ({url})");
				}
				text = r.Text;
				documentCache[url] = text;
			}
			return text;
		}

		private static string PadBase64(string s)
		{
			s = s.Trim().Replace('-', '+').Replace('_', '/');
			return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
		}

		private DynValue Handle(object value)
		{
			return value == null ? DynValue.Nil : UserData.Create(new LuaHandle(value));
		}

		private DynValue Array(IEnumerable<DynValue> values)
		{
			Table t = new Table(script);
			int i = 1;
			foreach (DynValue v in values)
			{
				t[i++] = v;
			}
			return DynValue.NewTable(t);
		}

		private static string Str(CallbackArguments a, int i)
		{
			DynValue v = a[i];
			return v.IsNil() ? string.Empty : (v.Type == DataType.String ? v.String : v.ToPrintString());
		}

		private static object Unwrap(DynValue v)
		{
			return (v.UserData?.Object as LuaHandle)?.Value;
		}

		private static HtmlNode Node(CallbackArguments a, int i)
		{
			return Unwrap(a[i]) as HtmlNode;
		}

		private static JToken Token(CallbackArguments a, int i)
		{
			return Unwrap(a[i]) as JToken;
		}

		#endregion
	}

	public class ModuleException : Exception
	{
		public ModuleException(string message, Exception inner = null)
			: base(message, inner)
		{
		}

		// Script location and Lua call stack, for the job log
		public string Details { get; set; }
	}
}
