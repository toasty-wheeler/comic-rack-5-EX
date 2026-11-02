-- Fallback for sites without a dedicated module: collects the large images of a page.
-- Works for simple galleries/readers that put all images into the HTML.

function Register()
    module.Name = 'Generic image gallery'
end

local function looksLikeContent(u)
    u = u:lower()
    if u:startswith('data:') or u:endswith('.svg') or u:contains('.svg?') then
        return false
    end
    for _, word in ipairs({ 'logo', 'icon', 'avatar', 'sprite', 'banner', 'emoji', 'button', 'thumb', 'pixel', 'ads', '/ad/', 'gravatar', 'blank.', 'loading', 'spinner' }) do
        if u:contains(word) then
            return false
        end
    end
    return true
end

function GetInfo()
    info.Title = dom.SelectValue('//meta[@property="og:title"]/@content')
    if isempty(info.Title) then
        info.Title = dom.SelectValue('//h1')
    end
    if isempty(info.Title) then
        info.Title = dom.SelectValue('//title')
    end
    info.Summary = dom.SelectValue('//meta[@name="description"]/@content')
end

function GetPages()
    local seen = {}
    local function add(u)
        u = tostring(u):trim()
        if isempty(u) then
            return
        end
        u = GetRooted(u, url)
        if not seen[u] and looksLikeContent(u) then
            seen[u] = true
            pages.Add(u)
        end
    end

    -- Links that point straight at full-size images (thumbnail galleries)
    for href in dom.SelectValues('//a[contains(@href,".jpg") or contains(@href,".jpeg") or contains(@href,".png") or contains(@href,".webp") or contains(@href,".gif") or contains(@href,".avif")]/@href') do
        if href:regex('\\.(jpe?g|png|webp|gif|avif)(\\?.*)?$') ~= '' then
            add(href)
        end
    end

    if pages.Count() == 0 then
        -- Reader pages: lazy-loaded images first, then plain src
        for img in dom.SelectElements('//img') do
            local src = img.GetAttribute('data-src')
            if isempty(src) then src = img.GetAttribute('data-lazy-src') end
            if isempty(src) then src = img.GetAttribute('data-original') end
            if isempty(src) then src = img.GetAttribute('data-url') end
            if isempty(src) then src = img.GetAttribute('src') end
            add(src)
        end
    end

    pages.Referer = url
end
