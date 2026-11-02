-- Runtime for HDoujin Downloader site modules (https://github.com/HDoujinDownloader/HDoujinDownloader).
-- Re-implements the scripting API the modules expect on top of the C# functions in the "host" table.
-- Module code calls methods both as obj.Method(x) and obj:Method(x), so every method accepts both forms.

local host = host
local _tostring = tostring

-- Modules check this to pick code paths for newer HDoujin versions
API_VERSION = 20260601
-- User agent that modules send to APIs requiring clients to identify themselves (MangaDex, Danbooru)
API_CLIENT = "ComicRackCE-Downloader/1.0"

function tostring(v)
    if v == nil then
        return ''
    end
    return _tostring(v)
end

-- Classes ----------------------------------------------------------------------------------------

local function class(def)
    local methods = def.methods or {}
    local props = def.props or {}
    local mt = {}
    mt.__index = function(self, k)
        -- Internal fields: absent on null objects (NullJson, NullNode), and looking them up
        -- through def.index would recurse forever
        if k == '_h' or k == '_items' or k == '__iter' then
            return nil
        end
        local m = methods[k]
        if m then
            local f = function(...)
                if select('#', ...) > 0 and rawequal((...), self) then
                    return m(...)
                end
                return m(self, ...)
            end
            rawset(self, k, f)
            return f
        end
        local p = props[k]
        if p then
            return p(self)
        end
        if def.index then
            return def.index(self, k)
        end
        return nil
    end
    mt.__newindex = def.newindex
    mt.__tostring = def.tostring
    mt.__concat = function(a, b)
        return tostring(a) .. tostring(b)
    end
    mt.__len = def.len
    mt.__isempty = def.isempty
    if def.iterate then
        -- "for x in obj do" (MoonSharp uses __iterator, standard Lua calls the object)
        mt.__iterator = function(self)
            -- MoonSharp needs the full (function, state, control) triple
            return def.iterate(self), self, nil
        end
        mt.__call = function(self, _, control)
            if control == nil or self.__iter == nil then
                rawset(self, '__iter', def.iterate(self))
            end
            local v = self.__iter()
            if v == nil then
                rawset(self, '__iter', nil)
            end
            return v
        end
    end
    mt.methods = methods
    return mt
end

local function ismt(v, mt)
    return type(v) == 'table' and getmetatable(v) == mt
end

-- List -------------------------------------------------------------------------------------------

local ListMT

local function newlist(items)
    return setmetatable({ _items = items or {} }, ListMT)
end

-- Turns lists, Lua arrays and single values into a Lua array
local function toarray(v)
    if v == nil then
        return {}
    end
    if type(v) == 'table' then
        local items = rawget(v, '_items')
        if items then
            return items
        end
        local mt = getmetatable(v)
        if mt and mt.__iterator then
            local r = {}
            for x in mt.__iterator(v) do
                r[#r + 1] = x
            end
            return r
        end
        if mt == nil or mt.methods == nil then
            if #v > 0 then
                return v
            end
            local r = {}
            for _, x in pairs(v) do
                r[#r + 1] = x
            end
            return r
        end
    end
    return { v }
end

local function listeq(a, b)
    return a == b or tostring(a) == tostring(b)
end

local listMethods = {
    Add = function(self, v)
        table.insert(self._items, v)
    end,
    AddRange = function(self, v)
        for _, x in ipairs(toarray(v)) do
            self.Add(x)
        end
    end,
    Count = function(self)
        return #self._items
    end,
    Clear = function(self)
        self._items = {}
    end,
    Reverse = function(self)
        local items, n = self._items, #self._items
        for i = 1, math.floor(n / 2) do
            items[i], items[n - i + 1] = items[n - i + 1], items[i]
        end
        return self
    end,
    Contains = function(self, v)
        for _, x in ipairs(self._items) do
            if listeq(x, v) then
                return true
            end
        end
        return false
    end,
    IndexOf = function(self, v)
        for i, x in ipairs(self._items) do
            if listeq(x, v) then
                return i - 1
            end
        end
        return -1
    end,
    Remove = function(self, v)
        for i, x in ipairs(self._items) do
            if listeq(x, v) then
                table.remove(self._items, i)
                return true
            end
        end
        return false
    end,
    RemoveAt = function(self, i)
        table.remove(self._items, i + 1)
    end,
    Insert = function(self, i, v)
        table.insert(self._items, i + 1, v)
    end,
    Get = function(self, i)
        return self._items[i + 1]
    end,
    First = function(self)
        return self._items[1]
    end,
    Last = function(self)
        return self._items[#self._items]
    end,
    Sort = function(self, cmp)
        table.sort(self._items, cmp or function(a, b)
            return tostring(a) < tostring(b)
        end)
        return self
    end,
    Distinct = function(self)
        local seen, r = {}, {}
        for _, x in ipairs(self._items) do
            local k = tostring(x)
            if not seen[k] then
                seen[k] = true
                r[#r + 1] = x
            end
        end
        self._items = r
        return self
    end,
    Take = function(self, n)
        local r = {}
        for i = 1, math.min(n, #self._items) do
            r[i] = self._items[i]
        end
        return newlist(r)
    end,
    Skip = function(self, n)
        local r = {}
        for i = n + 1, #self._items do
            r[#r + 1] = self._items[i]
        end
        return newlist(r)
    end,
    ToTable = function(self)
        return self._items
    end,
    join = function(self, sep)
        local parts = {}
        for i, x in ipairs(self._items) do
            parts[i] = tostring(x)
        end
        return table.concat(parts, sep or ', ')
    end,
}
listMethods.Join = listMethods.join
listMethods.ToString = function(self)
    return self.join(', ')
end

local function listdef(extra)
    local m = {}
    for k, v in pairs(listMethods) do
        m[k] = v
    end
    for k, v in pairs(extra or {}) do
        m[k] = v
    end
    return {
        methods = m,
        index = function(self, k)
            if type(k) == 'number' then
                return self._items[k + 1]
            end
        end,
        -- Lists are 0-based like the .NET lists the modules were written against
        newindex = function(self, k, v)
            if type(k) == 'number' then
                self._items[k + 1] = v
            else
                rawset(self, k, v)
            end
        end,
        tostring = function(self)
            return self.join(', ')
        end,
        len = function(self)
            return #self._items
        end,
        isempty = function(self)
            return #self._items == 0
        end,
        iterate = function(self)
            local i, items = 0, self._items
            return function()
                i = i + 1
                return items[i]
            end
        end,
    }
end

ListMT = class(listdef())

List = {
    New = function(...)
        local l = newlist()
        for _, x in ipairs({ ... }) do
            l.Add(x)
        end
        return l
    end,
}
StringList = List

-- Dict -------------------------------------------------------------------------------------------

local DictMT = class({
    methods = {
        Add = function(self, k, v)
            self._d[k] = v
        end,
        Set = function(self, k, v)
            self._d[k] = v
        end,
        Get = function(self, k)
            return self._d[k]
        end,
        ContainsKey = function(self, k)
            return self._d[k] ~= nil
        end,
        Remove = function(self, k)
            self._d[k] = nil
        end,
        Count = function(self)
            local n = 0
            for _ in pairs(self._d) do
                n = n + 1
            end
            return n
        end,
    },
    props = {
        Keys = function(self)
            local r = {}
            for k in pairs(self._d) do
                r[#r + 1] = k
            end
            return newlist(r)
        end,
        Values = function(self)
            local r = {}
            for _, v in pairs(self._d) do
                r[#r + 1] = v
            end
            return newlist(r)
        end,
    },
    index = function(self, k)
        return self._d[k]
    end,
    newindex = function(self, k, v)
        self._d[k] = v
    end,
    isempty = function(self)
        return next(self._d) == nil
    end,
    iterate = function(self)
        local k
        return function()
            local v
            k, v = next(self._d, k)
            if k == nil then
                return nil
            end
            return { Key = k, Value = v }
        end
    end,
})

Dict = {
    New = function()
        return setmetatable({ _d = {} }, DictMT)
    end,
}
Dictionary = Dict

-- Strings ----------------------------------------------------------------------------------------

local function plainfind(s, x, init)
    return string.find(s, x, init or 1, true)
end

local function lastfind(s, x)
    local last
    local i = 1
    while true do
        local a = plainfind(s, x, i)
        if not a then
            break
        end
        last = a
        i = a + 1
    end
    return last
end

function string.contains(s, x)
    return plainfind(s, tostring(x)) ~= nil
end

function string.startswith(s, x)
    x = tostring(x)
    return s:sub(1, #x) == x
end

function string.endswith(s, x)
    x = tostring(x)
    return x == '' or s:sub(-#x) == x
end

function string.trim(s, chars)
    if chars == nil then
        return (s:gsub('^%s+', ''):gsub('%s+$', ''))
    end
    chars = tostring(chars)
    if chars == '' then
        return s
    end
    while s:startswith(chars) do
        s = s:sub(#chars + 1)
    end
    while #s > 0 and s:endswith(chars) do
        s = s:sub(1, #s - #chars)
    end
    return s
end

function string.after(s, x)
    x = tostring(x)
    local i = plainfind(s, x)
    if not i then
        return ''
    end
    return s:sub(i + #x)
end

function string.afterlast(s, x)
    x = tostring(x)
    local i = lastfind(s, x)
    if not i then
        return ''
    end
    return s:sub(i + #x)
end

function string.before(s, x)
    local i = plainfind(s, tostring(x))
    if not i then
        return s
    end
    return s:sub(1, i - 1)
end

function string.beforelast(s, x)
    local i = lastfind(s, tostring(x))
    if not i then
        return s
    end
    return s:sub(1, i - 1)
end

function string.between(s, a, b)
    return s:after(a):before(b)
end

function string.replace(s, old, new)
    old = tostring(old)
    if old == '' then
        return s
    end
    local parts = {}
    local i = 1
    while true do
        local a = plainfind(s, old, i)
        if not a then
            break
        end
        parts[#parts + 1] = s:sub(i, a - 1)
        i = a + #old
    end
    parts[#parts + 1] = s:sub(i)
    return table.concat(parts, tostring(new))
end

function string.split(s, sep)
    local r = {}
    if sep == nil then
        for w in s:gmatch('%S+') do
            r[#r + 1] = w
        end
        return newlist(r)
    end
    sep = tostring(sep)
    local i = 1
    while true do
        local a = plainfind(s, sep, i)
        if not a then
            break
        end
        r[#r + 1] = s:sub(i, a - 1)
        i = a + #sep
    end
    r[#r + 1] = s:sub(i)
    return newlist(r)
end

function string.title(s)
    return (s:gsub("(%w)([%w']*)", function(a, b)
        return a:upper() .. b:lower()
    end))
end

function string.regex(s, pattern, group)
    return host.regex(s, tostring(pattern), group or 0)
end

function string.regexmany(s, pattern, group)
    return newlist(host.regex_many(s, tostring(pattern), group or 0))
end

function string.join(s)
    return s
end

function string.first(s)
    return s
end

function string.last(s)
    return s
end

function string.count(s)
    return #s
end

-- Helpers ----------------------------------------------------------------------------------------

function isempty(v)
    if v == nil then
        return true
    end
    local t = type(v)
    if t == 'string' then
        return v:match('^%s*$') ~= nil
    end
    if t == 'boolean' then
        return not v
    end
    if t == 'table' then
        local mt = getmetatable(v)
        if mt and mt.__isempty then
            return mt.__isempty(v)
        end
        if mt and mt.methods then
            return false
        end
        return next(v) == nil
    end
    return false
end

function toboolean(v)
    if v == true then
        return true
    end
    local s = tostring(v):lower():trim()
    return s == 'true' or s == '1' or s == 'yes' or s == 'on'
end

function isnumber(v)
    return tonumber(v) ~= nil
end

local _tonumber = tonumber
function tonumber(v, base)
    if type(v) == 'string' then
        v = v:trim()
    elseif type(v) == 'table' then
        v = tostring(v):trim()
    end
    return _tonumber(v, base)
end

-- Resolve a URL against the current page URL
local function resolve(u)
    u = tostring(u)
    if u == '' then
        return u
    end
    return host.resolve(u, tostring(rawget(_G, 'url')))
end

function GetRooted(u, base)
    return host.resolve(tostring(u), tostring(base or rawget(_G, 'url')))
end

function GetRoot(u)
    return host.url_root(tostring(u or rawget(_G, 'url')))
end

function GetHost(u)
    return host.url_host(tostring(u or rawget(_G, 'url')))
end

function GetDomain(u)
    return host.url_domain(tostring(u or rawget(_G, 'url')))
end

function GetParameter(u, name)
    return host.get_param(tostring(u), tostring(name))
end

function SetParameter(u, name, value)
    return host.set_param(tostring(u), tostring(name), tostring(value))
end

function StripParameters(u)
    return host.strip_params(tostring(u))
end

function RegexReplace(input, pattern, replacement)
    return host.regex_replace(tostring(input), tostring(pattern), tostring(replacement))
end

function RegexMatches(input, pattern)
    return newlist(host.regex_matches(tostring(input), tostring(pattern)))
end

Regex = {
    IsMatch = function(input, pattern)
        return host.regex_ismatch(tostring(input), tostring(pattern))
    end,
    Replace = RegexReplace,
    Matches = RegexMatches,
}

function EscapeRegexString(s)
    return host.regex_escape(tostring(s))
end

function FormatString(fmt, ...)
    local args = {}
    for i = 1, select('#', ...) do
        args[i] = tostring((select(i, ...)))
    end
    return host.format(tostring(fmt), args)
end

function DecodeBase64(s)
    return host.b64decode(tostring(s))
end

function EncodeBase64(s)
    return host.b64encode(tostring(s))
end

function EncodeUriComponent(s)
    return host.uri_encode(tostring(s))
end

function DecodeUriComponent(s)
    return host.uri_decode(tostring(s))
end

function Unescape(s)
    return host.unescape(tostring(s))
end

function GetLanguageId(s)
    return host.language_id(tostring(s))
end

function GetLanguageName(s)
    return host.language_name(tostring(s))
end

function sleep(ms)
    host.sleep(tonumber(ms) or 0)
end
Sleep = sleep

function Log(...)
    local parts = {}
    for i = 1, select('#', ...) do
        parts[i] = tostring((select(i, ...)))
    end
    host.log(table.concat(parts, ' '))
end
print = Log

function DoEncryptedString()
    error('This module uses encrypted code from HDoujin Downloader and is not supported', 0)
end

Error = setmetatable({
    New = function(msg)
        return tostring(msg)
    end,
}, {
    __index = function(_, k)
        return k
    end,
})

function Fail(err, message)
    local msg = tostring(err)
    if message ~= nil and tostring(message) ~= '' then
        msg = msg .. ': ' .. tostring(message)
    end
    error(msg, 0)
end

username = ''
password = ''

-- DOM (HTML + XPath) -----------------------------------------------------------------------------

local DomMT
local NullNode

local function newnode(h)
    if h == nil then
        return NullNode
    end
    return setmetatable({ _h = h }, DomMT)
end

local function nodelist(handles)
    local r = {}
    for i, h in ipairs(handles) do
        r[i] = newnode(h)
    end
    return newlist(r)
end

DomMT = class({
    methods = {
        SelectValue = function(self, xpath)
            if self._h == nil then
                return ''
            end
            return host.xpath_value(self._h, tostring(xpath))
        end,
        SelectValues = function(self, xpath)
            if self._h == nil then
                return newlist()
            end
            return newlist(host.xpath_values(self._h, tostring(xpath)))
        end,
        SelectElements = function(self, xpath)
            if self._h == nil then
                return newlist()
            end
            return nodelist(host.xpath_nodes(self._h, tostring(xpath)))
        end,
        SelectElement = function(self, xpath)
            if self._h == nil then
                return NullNode
            end
            return newnode(host.xpath_nodes(self._h, tostring(xpath))[1])
        end,
        GetAttribute = function(self, name)
            if self._h == nil then
                return ''
            end
            return host.node_attr(self._h, tostring(name))
        end,
        ToString = function(self)
            return tostring(self)
        end,
    },
    props = {
        Title = function(self)
            return self.SelectValue('//title')
        end,
        InnerText = function(self)
            return self._h and host.node_text(self._h) or ''
        end,
        Text = function(self)
            return self._h and host.node_text(self._h) or ''
        end,
        InnerHtml = function(self)
            return self._h and host.node_html(self._h, false) or ''
        end,
        OuterHtml = function(self)
            return self._h and host.node_html(self._h, true) or ''
        end,
    },
    index = function(self, k)
        if type(k) == 'string' then
            -- node['attr'] / node.href style attribute access
            local v = self._h and host.node_attr(self._h, k) or ''
            if v ~= '' then
                return v
            end
        end
    end,
    tostring = function(self)
        return self._h and host.node_html(self._h, true) or ''
    end,
    isempty = function(self)
        return self._h == nil
    end,
})
DomMT.methods.SelectNodes = DomMT.methods.SelectElements
DomMT.methods.New = function(_, html)
    return Dom.New(html)
end
DomMT.methods.SelectNode = DomMT.methods.SelectElement
DomMT.methods.SelectNodeValues = DomMT.methods.SelectValues
NullNode = setmetatable({}, DomMT)

Dom = {
    New = function(html)
        if ismt(html, DomMT) then
            return html
        end
        return newnode(host.html_parse(tostring(html)))
    end,
}

-- JSON (JSONPath) --------------------------------------------------------------------------------

local JsonMT
local NullJson

local function newjson(h)
    if h == nil then
        return NullJson
    end
    return setmetatable({ _h = h }, JsonMT)
end

-- Scalars come back as strings, objects/arrays as Json objects
local function jsonchild(h)
    if h == nil then
        return nil
    end
    if host.json_kind(h) == 'value' then
        return host.json_string(h)
    end
    return newjson(h)
end

JsonMT = class({
    methods = {
        SelectValue = function(self, path)
            if self._h == nil then
                return ''
            end
            return host.json_values(self._h, tostring(path), true)[1] or ''
        end,
        SelectValues = function(self, path)
            if self._h == nil then
                return newlist()
            end
            return newlist(host.json_values(self._h, tostring(path), false))
        end,
        SelectToken = function(self, path)
            if self._h == nil then
                return NullJson
            end
            return newjson(host.json_tokens(self._h, tostring(path))[1])
        end,
        SelectTokens = function(self, path)
            local r = {}
            if self._h ~= nil then
                for i, h in ipairs(host.json_tokens(self._h, tostring(path))) do
                    r[i] = newjson(h)
                end
            end
            return newlist(r)
        end,
        Count = function(self)
            return self._h and host.json_count(self._h) or 0
        end,
        ToJson = function(self)
            return self
        end,
        ToString = function(self)
            return tostring(self)
        end,
    },
    props = {
        Keys = function(self)
            return newlist(self._h and host.json_keys(self._h) or {})
        end,
        Values = function(self)
            local r = {}
            if self._h ~= nil then
                for i, h in ipairs(host.json_children(self._h)) do
                    r[i] = jsonchild(h)
                end
            end
            return newlist(r)
        end,
        Key = function(self)
            return self._h and host.json_key(self._h) or ''
        end,
        Value = function(self)
            return self
        end,
    },
    index = function(self, k)
        if self._h ~= nil and (type(k) == 'string' or type(k) == 'number') then
            return jsonchild(host.json_index(self._h, k))
        end
    end,
    tostring = function(self)
        return self._h and host.json_string(self._h) or ''
    end,
    isempty = function(self)
        return self._h == nil or host.json_count(self._h) == 0
    end,
    iterate = function(self)
        local children = self._h and host.json_children(self._h) or {}
        local i = 0
        return function()
            i = i + 1
            local h = children[i]
            if h == nil then
                return nil
            end
            return newjson(h)
        end
    end,
})
JsonMT.methods.SelectNodes = JsonMT.methods.SelectTokens
JsonMT.methods.New = function(_, text)
    return Json.New(text)
end
JsonMT.methods.SelectNode = JsonMT.methods.SelectToken
NullJson = setmetatable({}, JsonMT)

Json = {
    New = function(text)
        if ismt(text, JsonMT) then
            return text
        end
        return newjson(host.json_parse(tostring(text)))
    end,
}

-- JavaScript ------------------------------------------------------------------------------------

local JsValueMT = class({
    methods = {
        ToString = function(self)
            return host.js_tostring(self._v)
        end,
        ToJson = function(self)
            return Json.New(host.js_tojson(self._v))
        end,
    },
    tostring = function(self)
        return host.js_tostring(self._v)
    end,
    isempty = function(self)
        return host.js_tostring(self._v) == ''
    end,
})

local function jsvalue(v)
    return setmetatable({ _v = v }, JsValueMT)
end

local JsMT = class({
    methods = {
        Execute = function(self, code)
            return jsvalue(host.js_exec(self._e, tostring(code)))
        end,
        GetObject = function(self, name)
            return jsvalue(host.js_exec(self._e, tostring(name)))
        end,
    },
})
JsMT.methods.Eval = JsMT.methods.Execute
JsMT.methods.Evaluate = JsMT.methods.Execute

JavaScript = {
    New = function()
        return setmetatable({ _e = host.js_new() }, JsMT)
    end,
    Deobfuscate = function(code)
        return host.js_deobfuscate(tostring(code))
    end,
}
JS = setmetatable({}, {
    __index = function(_, k)
        error('JS.' .. tostring(k) .. ' is not supported', 0)
    end,
})

-- HTTP -------------------------------------------------------------------------------------------

local CookiesMT = class({
    methods = {
        GetCookie = function(self, name)
            for _, c in ipairs(self._list()) do
                if c.Name == name then
                    return c.Value
                end
            end
            return ''
        end,
        Contains = function(self, name)
            return self.GetCookie(name) ~= ''
        end,
        Count = function(self)
            return #self._list()
        end,
        ToString = function(self)
            return tostring(self)
        end,
    },
    index = function(self, k)
        if type(k) == 'string' then
            local v = self.GetCookie(k)
            if v ~= '' then
                return v
            end
        end
    end,
    newindex = function(self, k, v)
        host.set_cookie(host.url_host(tostring(rawget(_G, 'url'))), tostring(k), tostring(v))
    end,
    tostring = function(self)
        local parts = {}
        for i, c in ipairs(self._list()) do
            parts[i] = c.Name .. '=' .. c.Value
        end
        return table.concat(parts, '; ')
    end,
    isempty = function(self)
        return #self._list() == 0
    end,
})
CookiesMT.methods.Get = CookiesMT.methods.GetCookie

local function jarcookies()
    return setmetatable({
        _list = function()
            return host.cookies(tostring(rawget(_G, 'url')))
        end,
    }, CookiesMT)
end

local function fixedcookies(list)
    return setmetatable({
        _list = function()
            return list
        end,
    }, CookiesMT)
end

local PostDataMT = class({
    methods = {
        Add = function(self, k, v)
            table.insert(self._pairs, { tostring(k), tostring(v) })
        end,
        Clear = function(self)
            self._pairs = {}
        end,
        Count = function(self)
            return #self._pairs
        end,
    },
    newindex = function(self, k, v)
        table.insert(self._pairs, { tostring(k), tostring(v) })
    end,
    isempty = function(self)
        return #self._pairs == 0
    end,
})

local ResponseMT = class({
    methods = {
        ToString = function(self)
            return self.Body
        end,
    },
    tostring = function(self)
        return self.Body
    end,
})

local function encodeform(pairsList)
    local parts = {}
    for i, p in ipairs(pairsList) do
        parts[i] = host.form_encode(p[1]) .. '=' .. host.form_encode(p[2])
    end
    return table.concat(parts, '&')
end

local function newhttp()
    local h = {
        Headers = {},
        Referer = nil,
        AllowAutoRedirect = true,
        PostData = setmetatable({ _pairs = {} }, PostDataMT),
    }
    h.Cookies = jarcookies()

    local function request(method, u, data, throw, explicitType)
        local body, contentType
        if data ~= nil then
            if type(data) == 'table' and rawget(data, '_pairs') then
                body = encodeform(data._pairs)
                contentType = 'application/x-www-form-urlencoded'
            elseif type(data) == 'table' and getmetatable(data) == nil then
                local ps = {}
                for k, v in pairs(data) do
                    ps[#ps + 1] = { tostring(k), tostring(v) }
                end
                body = encodeform(ps)
                contentType = 'application/x-www-form-urlencoded'
            else
                body = tostring(data)
                local first = body:match('^%s*(.)')
                if first == '{' or first == '[' then
                    contentType = 'application/json'
                else
                    contentType = 'application/x-www-form-urlencoded'
                end
            end
        elseif method == 'POST' then
            body = encodeform(h.PostData._pairs)
            contentType = 'application/x-www-form-urlencoded'
        end
        if method == 'POST' then
            h.PostData.Clear()
        end
        if explicitType ~= nil then
            contentType = tostring(explicitType)
        end
        local r = host.http(method, resolve(u), h.Headers, body, contentType, h.Referer and tostring(h.Referer) or nil, h.AllowAutoRedirect ~= false, throw)
        r.Cookies = fixedcookies(r.Cookies)
        return setmetatable(r, ResponseMT)
    end

    h.Get = function(...)
        local args = { ... }
        if rawequal(args[1], h) then
            table.remove(args, 1)
        end
        return request('GET', args[1], nil, true).Body
    end
    h.Post = function(...)
        local args = { ... }
        if rawequal(args[1], h) then
            table.remove(args, 1)
        end
        return request('POST', args[1], args[2], true, args[3]).Body
    end
    h.GetResponse = function(...)
        local args = { ... }
        if rawequal(args[1], h) then
            table.remove(args, 1)
        end
        return request('GET', args[1], nil, false)
    end
    h.PostResponse = function(...)
        local args = { ... }
        if rawequal(args[1], h) then
            table.remove(args, 1)
        end
        return request('POST', args[1], args[2], false, args[3])
    end
    h.Head = function(...)
        local args = { ... }
        if rawequal(args[1], h) then
            table.remove(args, 1)
        end
        return request('HEAD', args[1], nil, false)
    end
    h.HeadResponse = h.Head
    h.New = newhttp
    return h
end

http = newhttp()

global = {
    GetSetting = function(name)
        return host.setting(tostring(name))
    end,
    SetCookies = function()
        -- responses already store their cookies in the shared cookie jar
    end,
    SetCookie = function(domain, name, value)
        host.set_cookie(tostring(domain), tostring(name), tostring(value))
    end,
}

Captcha = {
    New = function()
        return {
            Solve = function()
                Fail(Error.CaptchaRequired, 'captcha solving is not supported')
            end,
        }
    end,
    Solve = function()
        Fail(Error.CaptchaRequired, 'captcha solving is not supported')
    end,
}

-- Module definition ------------------------------------------------------------------------------

local DomainListMT = class(listdef({
    Add = function(self, domain, name)
        domain = tostring(domain)
        table.insert(self._items, domain)
        if name ~= nil then
            self._names[domain] = tostring(name)
        end
    end,
    AddRange = function(self, v)
        for _, x in ipairs(toarray(v)) do
            self.Add(x)
        end
    end,
}))

local SettingBuilderMT = class({
    methods = {
        WithToolTip = function(self)
            return self
        end,
        WithDescription = function(self)
            return self
        end,
        WithLabel = function(self)
            return self
        end,
        WithDefault = function(self, v)
            self._s._values[self._name] = v
            return self
        end,
    },
    props = {
        Options = function(self)
            return self._options
        end,
    },
    index = function(self, k)
        -- Any other builder option (WithMultiSelect, WithMinimum, ...) is accepted and ignored
        if type(k) == 'string' and k:startswith('With') then
            return function()
                return self
            end
        end
    end,
})

local function addsetting(self, name, default, options)
    name = tostring(name)
    local override = host.setting('Module.' .. name)
    if override ~= nil then
        default = override
    end
    self._values[name] = default
    table.insert(self._order, name)
    local opts = newlist()
    opts.AddRange(options)
    return setmetatable({ _s = self, _name = name, _options = opts }, SettingBuilderMT)
end

local SettingsMT = class({
    methods = {
        AddCheck = addsetting,
        AddText = addsetting,
        AddPassword = addsetting,
        AddChoice = addsetting,
        AddSlider = addsetting,
        AddInteger = addsetting,
        AddNumber = addsetting,
        Add = addsetting,
        Get = function(self, name)
            return self._values[tostring(name)]
        end,
    },
    index = function(self, k)
        return self._values[k]
    end,
    newindex = function(self, k, v)
        self._values[k] = v
    end,
})

local function newsettings(copy)
    local s = setmetatable({ _values = {}, _order = {} }, SettingsMT)
    if copy then
        for k, v in pairs(copy._values) do
            s._values[k] = v
        end
        for i, k in ipairs(copy._order) do
            s._order[i] = k
        end
    end
    return s
end

local ModuleMT

local function newmodule(base)
    local m = {
        Name = base and base.Name or '',
        Language = base and base.Language or '',
        Adult = base and base.Adult or false,
        Type = base and base.Type or '',
        Strict = true,
        Domain = '',
        Data = {},
        DeferHttpRequests = false,
        IsGeneric = false,
        Domains = setmetatable({ _items = {}, _names = {} }, DomainListMT),
        Settings = newsettings(base and base.Settings),
        RateLimits = { Add = function() end },
    }
    return setmetatable(m, ModuleMT)
end

ModuleMT = class({
    methods = {
        New = function(self)
            return newmodule(self)
        end,
        GetName = function(self, u)
            local d = self.Domain
            if u ~= nil and tostring(u) ~= '' then
                d = host.url_host(tostring(u))
            end
            for domain, name in pairs(self.Domains._names) do
                if d == domain or d:endswith('.' .. domain) then
                    return name
                end
            end
            return self.Name
        end,
    },
})

Module = {
    New = function()
        return newmodule(nil)
    end,
}

module = newmodule(nil)
__registered = {}

function RegisterModule(m)
    table.insert(__registered, m or module)
end

-- Gallery / chapters / pages ---------------------------------------------------------------------

local function newpage(u)
    return {
        Url = u ~= nil and tostring(u) or '',
        Referer = nil,
        Headers = {},
        BackupUrls = newlist(),
        Data = {},
        FilenameHint = nil,
        FileExtensionHint = nil,
    }
end

PageInfo = {
    New = function(u)
        return newpage(u)
    end,
}

local function topage(v)
    if type(v) == 'string' then
        return newpage(v)
    end
    if type(v) == 'table' and getmetatable(v) == DomMT then
        local src = v.SelectValue('@src')
        if src == '' then
            src = v.SelectValue('.//img/@src')
        end
        return newpage(src)
    end
    if type(v) == 'table' and rawget(v, 'Url') ~= nil then
        if v.BackupUrls == nil then
            v.BackupUrls = newlist()
        end
        if v.Data == nil then
            v.Data = {}
        end
        if v.Headers == nil then
            v.Headers = {}
        end
        return v
    end
    return newpage(tostring(v))
end

local PageListMT = class(listdef({
    Add = function(self, v)
        table.insert(self._items, topage(v))
    end,
    AddRange = function(self, v)
        for _, x in ipairs(toarray(v)) do
            table.insert(self._items, topage(x))
        end
    end,
}))

local function newpagelist()
    return setmetatable({ _items = {}, Headers = {} }, PageListMT)
end

PageList = {
    New = newpagelist,
}

local function newchapter(u, title)
    return {
        Url = u ~= nil and tostring(u) or '',
        Title = title ~= nil and tostring(title) or '',
        Volume = '',
        Chapter = '',
        Language = '',
        ScanlationGroup = '',
        Translator = '',
        Scanlator = '',
        Version = '',
    }
end

ChapterInfo = {
    New = function(u, title)
        return newchapter(u, title)
    end,
}

local function tochapter(v, title)
    if type(v) == 'table' and getmetatable(v) == DomMT then
        local href = v.SelectValue('@href')
        if href == '' then
            href = v.SelectValue('.//a/@href')
        end
        local text = title or v.SelectValue('.')
        return newchapter(href, text)
    end
    if type(v) == 'table' then
        local c = newchapter(nil, nil)
        for k, x in pairs(v) do
            c[k] = x
        end
        return c
    end
    return newchapter(v, title or v)
end

local ChapterListMT = class(listdef({
    Add = function(self, v, title)
        table.insert(self._items, tochapter(v, title))
    end,
    AddRange = function(self, v)
        for _, x in ipairs(toarray(v)) do
            table.insert(self._items, tochapter(x))
        end
    end,
}))

local function newchapterlist()
    return setmetatable({ _items = {} }, ChapterListMT)
end

ChapterList = {
    New = newchapterlist,
}

-- Follows "next page" links: yields the current document and then each following page
Paginator = {
    New = function(h, d, nextXPath)
        local p = {}
        local iterate = function()
            local current = d
            local visited = {}
            local first = true
            return function()
                if first then
                    first = false
                    return current
                end
                local nextUrl = current.SelectValue(nextXPath)
                if isempty(nextUrl) then
                    return nil
                end
                nextUrl = resolve(nextUrl)
                if visited[nextUrl] then
                    return nil
                end
                visited[nextUrl] = true
                current = Dom.New(h.Get(nextUrl))
                return current
            end
        end
        return setmetatable(p, {
            __iterator = function(self)
                return iterate(), self, nil
            end,
            __call = function(self, _, control)
                if control == nil then
                    rawset(self, '__iter', iterate())
                end
                return self.__iter()
            end,
        })
    end,
}

function Enqueue(u)
    host.enqueue(resolve(u))
end

function require(name)
    local src = host.require(tostring(name))
    if src == nil then
        error('module not found: ' .. tostring(name), 0)
    end
    local f, err = load(src, '=' .. tostring(name))
    if not f then
        error(err, 0)
    end
    f()
    return true
end

-- Lazily fetched globals (doc, dom, js) ----------------------------------------------------------

__lazy = {}
setmetatable(_G, {
    __index = function(t, k)
        local f = __lazy[k]
        if f then
            local v = f()
            rawset(t, k, v)
            return v
        end
        return nil
    end,
})

__lazy.js = function()
    return JavaScript.New()
end

-- Points url/doc/dom at a new page; the document is only downloaded when the module uses it
function __begin(u)
    rawset(_G, 'url', u)
    rawset(_G, 'doc', nil)
    rawset(_G, 'dom', nil)
    __lazy.doc = function()
        return host.document(u)
    end
    __lazy.dom = function()
        return Dom.New(rawget(_G, 'doc') or _G.doc)
    end
end

-- Host side entry points -------------------------------------------------------------------------

function __has(name)
    return type(rawget(_G, name)) == 'function'
end

function __select_module(hostName)
    local candidates = {}
    for _, m in ipairs(__registered) do
        candidates[#candidates + 1] = m
    end
    candidates[#candidates + 1] = module
    local function strip(d)
        return (d:gsub('^www%.', ''))
    end
    hostName = strip(hostName:lower())
    for _, m in ipairs(candidates) do
        for _, d in ipairs(m.Domains._items) do
            local sd = strip(d:lower())
            if hostName == sd or hostName:endswith('.' .. sd) then
                module = m
                m.Domain = d
                return m.Name
            end
        end
    end
    return nil
end

function __domains()
    local r = {}
    local function addall(m)
        for _, d in ipairs(m.Domains._items) do
            r[#r + 1] = d
        end
    end
    for _, m in ipairs(__registered) do
        addall(m)
    end
    addall(module)
    return r
end

function __reset_lists()
    info = {}
    chapters = newchapterlist()
    pages = newpagelist()
end

function __reset_pages()
    pages = newpagelist()
end

local infoFields = { 'Title', 'OriginalTitle', 'AlternativeTitle', 'Author', 'Artist', 'Circle', 'Parody', 'Characters',
    'Tags', 'Genres', 'Type', 'Language', 'Series', 'Summary', 'Description', 'Status', 'DateReleased', 'Publisher',
    'Scanlator', 'Translator', 'Magazine', 'PageCount', 'ChapterCount', 'Adult', 'ReadingDirection', 'Url', 'Ignore',
    'Volume', 'Chapter', 'Event' }

function __export_info()
    local r = {}
    for _, k in ipairs(infoFields) do
        local v = rawget(info, k)
        if v ~= nil then
            r[k] = tostring(v)
        end
    end
    return r
end

function __export_chapters()
    local r = {}
    for i, c in ipairs(chapters._items) do
        r[i] = {
            Url = resolve(c.Url),
            Title = tostring(c.Title),
            Volume = tostring(c.Volume),
            Chapter = tostring(c.Chapter),
            Language = tostring(c.Language),
        }
    end
    return r
end

local function exportpage(p)
    local backups = {}
    for i, b in ipairs(toarray(p.BackupUrls)) do
        backups[i] = resolve(b)
    end
    local headers = {}
    for k, v in pairs(pages.Headers or {}) do
        headers[tostring(k)] = tostring(v)
    end
    for k, v in pairs(p.Headers or {}) do
        headers[tostring(k)] = tostring(v)
    end
    local referer = p.Referer or pages.Referer
    return {
        Url = resolve(p.Url),
        Referer = referer and resolve(referer) or nil,
        Headers = headers,
        BackupUrls = backups,
        FilenameHint = p.FilenameHint and tostring(p.FilenameHint) or nil,
        FileExtensionHint = p.FileExtensionHint and tostring(p.FileExtensionHint) or nil,
    }
end

function __page_count()
    return #pages._items
end

-- Runs BeforeDownloadPage for page i (1-based) with dom/doc pointing at the page URL
function __prepare_page(i)
    local p = pages._items[i]
    page = p
    if __has('BeforeDownloadPage') then
        local saved = rawget(_G, 'url')
        __begin(resolve(p.Url))
        BeforeDownloadPage()
        rawset(_G, 'url', saved)
    end
    return exportpage(p)
end
