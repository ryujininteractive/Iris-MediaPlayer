using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Services.Jellyfin;

/// <summary>What a server says about itself before signing in.</summary>
public sealed record JellyfinServerInfo(string Address, string Id, string Name, string Version);

/// <summary>A signed-in user on a server.</summary>
public sealed record JellyfinSignIn(string ServerId, string UserId, string UserName, string AccessToken);

/// <summary>A Quick Connect request: show <see cref="Code"/> to the person, keep <see cref="Secret"/>.</summary>
public sealed record QuickConnectRequest(string Code, string Secret);

/// <summary>Something went wrong talking to the server, with a message that can be shown as is.</summary>
public sealed class JellyfinException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>How a library page is narrowed down.</summary>
public enum JellyfinFilter { All, Unwatched, InProgress, Favorites }

/// <summary>How a library page is ordered.</summary>
public enum JellyfinSort { Name, DateAdded, ReleaseDate, Rating }

/// <summary>Kinds of picture an item can have.</summary>
public enum JellyfinImage { Poster, Wide, Backdrop, Logo }

/// <summary>
/// A small client for the parts of the Jellyfin HTTP API Iris uses: checking a server, signing in
/// (password or Quick Connect), home rows, libraries, item details, seasons and episodes, favorites
/// and watched marks, and stream and image links. Responses are read with JsonDocument, so nothing
/// here needs reflection.
/// </summary>
public sealed class JellyfinClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The fields every list asks for, so cards and the hero have what they show.</summary>
    private const string ListFields =
        "&fields=Overview,Genres,DateCreated,PremiereDate,ProductionYear,RunTimeTicks,Container,SeriesName,"
        + "ParentIndexNumber,IndexNumber,ChildCount,RecursiveItemCount,Status,EndDate,OfficialRating,CommunityRating,"
        + "CriticRating,Taglines&enableImageTypes=Primary,Backdrop,Thumb,Logo&imageTypeLimit=1&enableUserData=true";

    public JellyfinClient(string address, string? accessToken = null, string? userId = null)
    {
        Address = NormalizeAddress(address);
        AccessToken = accessToken;
        UserId = userId;
    }

    public string Address { get; }
    public string? AccessToken { get; private set; }
    public string? UserId { get; private set; }

    /// <summary>"jelly.example.com" → "http://jelly.example.com"; drops a trailing slash or "/web".</summary>
    public static string NormalizeAddress(string address)
    {
        var text = address.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        text = text.TrimEnd('/');
        foreach (var suffix in new[] { "/web/index.html", "/web" })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                text = text[..^suffix.Length];
        }
        return text.TrimEnd('/');
    }

    // ---------- Server and sign-in ----------

    /// <summary>Checks that <see cref="Address"/> is a Jellyfin server and returns its name.</summary>
    public async Task<JellyfinServerInfo> GetServerInfoAsync(CancellationToken cancel = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "/System/Info/Public", null, cancel, "find a Jellyfin server at this address");
        var root = json.RootElement;
        var id = GetString(root, "Id");
        if (string.IsNullOrEmpty(id))
            throw new JellyfinException("That address answered, but it doesn't look like a Jellyfin server.");
        return new JellyfinServerInfo(Address, id, GetString(root, "ServerName") ?? "Jellyfin", GetString(root, "Version") ?? string.Empty);
    }

    public async Task<JellyfinSignIn> SignInAsync(string userName, string password, CancellationToken cancel = default)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["Username"] = userName, ["Pw"] = password },
            IrisJsonContext.Default.DictionaryStringString);
        using var json = await SendAsync(HttpMethod.Post, "/Users/AuthenticateByName", body, cancel, "sign in");
        return ReadSignIn(json.RootElement);
    }

    public async Task<bool> IsQuickConnectEnabledAsync(CancellationToken cancel = default)
    {
        try
        {
            using var json = await SendAsync(HttpMethod.Get, "/QuickConnect/Enabled", null, cancel, "check Quick Connect");
            return json.RootElement.ValueKind == JsonValueKind.True;
        }
        catch (JellyfinException)
        {
            return false;
        }
    }

    public async Task<QuickConnectRequest> StartQuickConnectAsync(CancellationToken cancel = default)
    {
        using var json = await SendAsync(HttpMethod.Post, "/QuickConnect/Initiate", null, cancel, "start Quick Connect");
        var code = GetString(json.RootElement, "Code");
        var secret = GetString(json.RootElement, "Secret");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(secret))
            throw new JellyfinException("The server didn't give a Quick Connect code.");
        return new QuickConnectRequest(code, secret);
    }

    /// <summary>True once the code has been approved on another signed-in device.</summary>
    public async Task<bool> IsQuickConnectApprovedAsync(QuickConnectRequest request, CancellationToken cancel = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "/QuickConnect/Connect?secret=" + Uri.EscapeDataString(request.Secret),
            null, cancel, "check Quick Connect");
        return json.RootElement.TryGetProperty("Authenticated", out var value) && value.ValueKind == JsonValueKind.True;
    }

    public async Task<JellyfinSignIn> FinishQuickConnectAsync(QuickConnectRequest request, CancellationToken cancel = default)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["Secret"] = request.Secret },
            IrisJsonContext.Default.DictionaryStringString);
        using var json = await SendAsync(HttpMethod.Post, "/Users/AuthenticateWithQuickConnect", body, cancel, "sign in");
        return ReadSignIn(json.RootElement);
    }

    /// <summary>Tells the server to forget this sign-in. Failures are ignored; the token is dropped locally anyway.</summary>
    public async Task SignOutAsync()
    {
        try
        {
            using var _ = await SendAsync(HttpMethod.Post, "/Sessions/Logout", null, CancellationToken.None, "sign out");
        }
        catch (JellyfinException)
        {
        }
    }

    private JellyfinSignIn ReadSignIn(JsonElement root)
    {
        var token = GetString(root, "AccessToken");
        if (string.IsNullOrEmpty(token) || !root.TryGetProperty("User", out var user))
            throw new JellyfinException("The server didn't accept the sign-in.");
        var userId = GetString(user, "Id") ?? throw new JellyfinException("The server didn't say who signed in.");
        AccessToken = token;
        UserId = userId;
        return new JellyfinSignIn(GetString(root, "ServerId") ?? string.Empty, userId, GetString(user, "Name") ?? string.Empty, token);
    }

    // ---------- Home rows ----------

    /// <summary>The user's video libraries (music, books and photos are left out).</summary>
    public async Task<IReadOnlyList<JellyfinItem>> GetLibrariesAsync(CancellationToken cancel = default)
    {
        var userId = RequireUser();
        var items = await GetItemsWithFallbackAsync("/UserViews?userId=" + userId, $"/Users/{userId}/Views", cancel, "load your libraries");
        return items.Where(item => item.CollectionType is null or "movies" or "tvshows" or "homevideos" or "musicvideos" or "boxsets" or "mixed")
            .ToList();
    }

    /// <summary>Films and episodes started but not finished, most recent first.</summary>
    public Task<IReadOnlyList<JellyfinItem>> GetResumeAsync(int limit, CancellationToken cancel = default)
    {
        var userId = RequireUser();
        var query = $"?userId={userId}&mediaTypes=Video&limit={limit}" + ListFields;
        return GetItemsWithFallbackAsync("/UserItems/Resume" + query, $"/Users/{userId}/Items/Resume" + query, cancel, "load Continue watching");
    }

    /// <summary>The next episode to watch in each show (or in one show, with <paramref name="seriesId"/>).</summary>
    public async Task<IReadOnlyList<JellyfinItem>> GetNextUpAsync(int limit, string? seriesId = null, CancellationToken cancel = default)
    {
        var query = $"/Shows/NextUp?userId={RequireUser()}&limit={limit}" + ListFields
                    + (seriesId is null ? string.Empty : "&seriesId=" + seriesId);
        using var json = await SendAsync(HttpMethod.Get, query, null, cancel, "load Next up");
        return ReadItems(json.RootElement).ToList();
    }

    /// <summary>The newest films or shows in a library.</summary>
    public async Task<IReadOnlyList<JellyfinItem>> GetLatestAsync(string libraryId, int limit, CancellationToken cancel = default)
    {
        var userId = RequireUser();
        var query = $"?userId={userId}&parentId={libraryId}&limit={limit}&groupItems=true" + ListFields;
        JsonDocument json;
        try
        {
            json = await SendAsync(HttpMethod.Get, "/Items/Latest" + query, null, cancel, "load what's new");
        }
        catch (JellyfinException)
        {
            json = await SendAsync(HttpMethod.Get, $"/Users/{userId}/Items/Latest" + query, null, cancel, "load what's new");
        }
        using (json)
            return ReadItemArray(json.RootElement).ToList();
    }

    // ---------- Libraries and folders ----------

    /// <summary>
    /// What's inside <paramref name="parent"/>. Movie and show libraries list every film or series in
    /// them, whatever folders they sit in; everything else lists its direct children. A null parent
    /// lists films and shows from every library (used for Favorites).
    /// </summary>
    public async Task<IReadOnlyList<JellyfinItem>> GetLibraryItemsAsync(JellyfinItem? parent, JellyfinFilter filter, JellyfinSort sort,
        CancellationToken cancel = default)
    {
        var query = new StringBuilder($"/Items?userId={RequireUser()}").Append(ListFields).Append("&enableTotalRecordCount=false");
        if (parent is not null)
            query.Append("&parentId=").Append(parent.Id);

        var recursiveTypes = parent is null ? "Movie,Series"
            : parent.CollectionType switch
            {
                "movies" => "Movie",
                "tvshows" => "Series",
                _ => null
            };
        if (recursiveTypes is not null)
            query.Append("&recursive=true&includeItemTypes=").Append(recursiveTypes);

        query.Append(filter switch
        {
            JellyfinFilter.Unwatched => "&filters=IsUnplayed",
            JellyfinFilter.InProgress => "&filters=IsResumable",
            JellyfinFilter.Favorites => "&filters=IsFavorite",
            _ => string.Empty
        });
        query.Append(sort switch
        {
            JellyfinSort.DateAdded => "&sortBy=DateCreated,SortName&sortOrder=Descending,Ascending",
            JellyfinSort.ReleaseDate => "&sortBy=PremiereDate,ProductionYear,SortName&sortOrder=Descending,Descending,Ascending",
            JellyfinSort.Rating => "&sortBy=CommunityRating,SortName&sortOrder=Descending,Ascending",
            _ when recursiveTypes is null => "&sortBy=IsFolder,SortName&sortOrder=Descending,Ascending",
            _ => "&sortBy=SortName&sortOrder=Ascending"
        });

        using var json = await SendAsync(HttpMethod.Get, query.ToString(), null, cancel, "load this library");
        return ReadItems(json.RootElement).Where(item => item.IsFolder || item.IsPlayable).ToList();
    }

    // ---------- Details ----------

    /// <summary>Everything about one item: overview, credits, ratings, versions and tracks.</summary>
    public async Task<JellyfinItem> GetItemAsync(string itemId, CancellationToken cancel = default)
    {
        var userId = RequireUser();
        const string fields = "?fields=Overview,Genres,People,Studios,Taglines,MediaSources,RemoteTrailers,ChildCount,Status,EndDate";
        JsonDocument json;
        try
        {
            json = await SendAsync(HttpMethod.Get, $"/Items/{itemId}{fields}&userId={userId}", null, cancel, "load this title");
        }
        catch (JellyfinException)
        {
            json = await SendAsync(HttpMethod.Get, $"/Users/{userId}/Items/{itemId}{fields}", null, cancel, "load this title");
        }
        using (json)
            return ReadItem(json.RootElement) ?? throw new JellyfinException("The server sent an empty answer for this title.");
    }

    /// <summary>
    /// Intros, recaps, credits and previews the server has marked for an item (Jellyfin 10.10's media
    /// segments, filled by plugins such as Intro Skipper). Empty on older servers or when none are marked.
    /// </summary>
    public async Task<IReadOnlyList<Models.SkipSegment>> GetSkipSegmentsAsync(string itemId, CancellationToken cancel = default)
    {
        JsonDocument json;
        try
        {
            json = await SendAsync(HttpMethod.Get, $"/MediaSegments/{itemId}", null, cancel, "load skip segments");
        }
        catch (JellyfinException)
        {
            return [];
        }

        using (json)
        {
            if (!json.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
                return [];

            var segments = new List<Models.SkipSegment>();
            foreach (var element in items.EnumerateArray())
            {
                Models.SkipKind? kind = element.TryGetProperty("Type", out var type) ? type.GetString() switch
                {
                    "Intro" => Models.SkipKind.Intro,
                    "Recap" => Models.SkipKind.Recap,
                    "Outro" => Models.SkipKind.Credits,
                    "Preview" => Models.SkipKind.Preview,
                    _ => null
                } : null;
                if (kind is null
                    || !element.TryGetProperty("StartTicks", out var start) || !start.TryGetInt64(out var startTicks)
                    || !element.TryGetProperty("EndTicks", out var end) || !end.TryGetInt64(out var endTicks))
                    continue;

                var segment = new Models.SkipSegment(kind.Value, startTicks / 10_000_000d, endTicks / 10_000_000d);
                if (segment.IsUsable)
                    segments.Add(segment);
            }
            return segments;
        }
    }

    public async Task<IReadOnlyList<JellyfinItem>> GetSeasonsAsync(string seriesId, CancellationToken cancel = default)
    {
        using var json = await SendAsync(HttpMethod.Get, $"/Shows/{seriesId}/Seasons?userId={RequireUser()}" + ListFields, null, cancel,
            "load the seasons");
        return ReadItems(json.RootElement).ToList();
    }

    public async Task<IReadOnlyList<JellyfinItem>> GetEpisodesAsync(string seriesId, string seasonId, CancellationToken cancel = default)
    {
        using var json = await SendAsync(HttpMethod.Get, $"/Shows/{seriesId}/Episodes?userId={RequireUser()}&seasonId={seasonId}" + ListFields,
            null, cancel, "load the episodes");
        return ReadItems(json.RootElement).ToList();
    }

    // ---------- Marks ----------

    public Task SetFavoriteAsync(string itemId, bool favorite)
        => SendMarkAsync(favorite, $"/UserFavoriteItems/{itemId}?userId={RequireUser()}", $"/Users/{RequireUser()}/FavoriteItems/{itemId}",
            "change the favorite");

    public Task SetPlayedAsync(string itemId, bool played)
        => SendMarkAsync(played, $"/UserPlayedItems/{itemId}?userId={RequireUser()}", $"/Users/{RequireUser()}/PlayedItems/{itemId}",
            "change watched");

    private async Task SendMarkAsync(bool on, string path, string olderPath, string action)
    {
        var method = on ? HttpMethod.Post : HttpMethod.Delete;
        try
        {
            using var _ = await SendAsync(method, path, null, CancellationToken.None, action);
        }
        catch (JellyfinException)
        {
            using var _ = await SendAsync(method, olderPath, null, CancellationToken.None, action);
        }
    }

    /// <summary>Posts a JSON body and ignores the answer (playback reports).</summary>
    public async Task PostAsync(string path, string jsonBody)
    {
        using var _ = await SendAsync(HttpMethod.Post, path, jsonBody, CancellationToken.None, "report playback");
    }

    // ---------- Links ----------

    /// <summary>
    /// A picture for <paramref name="item"/>, falling back to its show's when it has none of its own.
    /// Posters and stills are sized by height; backdrops and logos by width.
    /// </summary>
    public string? GetImageUrl(JellyfinItem item, JellyfinImage kind, int size)
    {
        (string Id, string Type, string Tag)? pick = kind switch
        {
            JellyfinImage.Poster => item.PrimaryTag is { } primary && item.Type != "Episode" ? (item.Id, "Primary", primary)
                : item.SeriesPrimaryTag is { } series && item.SeriesId is { } seriesId ? (seriesId, "Primary", series)
                : item.PrimaryTag is { } any ? (item.Id, "Primary", any) : null,
            JellyfinImage.Wide => item.Type == "Episode" && item.PrimaryTag is { } still ? (item.Id, "Primary", still)
                : item.ThumbTag is { } thumb ? (item.Id, "Thumb", thumb)
                : item.ParentThumbTag is { } parentThumb && item.ParentThumbItemId is { } thumbOwner ? (thumbOwner, "Thumb", parentThumb)
                : item.BackdropTag is { } backdrop ? (item.Id, "Backdrop/0", backdrop)
                : item.ParentBackdropTag is { } parentBackdrop && item.ParentBackdropItemId is { } backdropOwner ? (backdropOwner, "Backdrop/0", parentBackdrop)
                : item.PrimaryTag is { } primaryWide ? (item.Id, "Primary", primaryWide) : null,
            JellyfinImage.Backdrop => item.BackdropTag is { } own ? (item.Id, "Backdrop/0", own)
                : item.ParentBackdropTag is { } parent && item.ParentBackdropItemId is { } owner ? (owner, "Backdrop/0", parent) : null,
            JellyfinImage.Logo => item.LogoTag is { } logo ? (item.Id, "Logo", logo)
                : item.ParentLogoTag is { } parentLogo && item.ParentLogoItemId is { } logoOwner ? (logoOwner, "Logo", parentLogo) : null,
            _ => null
        };
        if (pick is not { } image)
            return null;
        var sizing = kind is JellyfinImage.Poster || (kind == JellyfinImage.Wide && image.Type == "Primary") ? $"fillHeight={size}" : $"maxWidth={size}";
        return $"{Address}/Items/{image.Id}/Images/{image.Type}?{sizing}&quality=90&tag={Uri.EscapeDataString(image.Tag)}";
    }

    public string? GetPersonImageUrl(JellyfinPerson person, int height)
        => person.ImageTag is null ? null
            : $"{Address}/Items/{person.Id}/Images/Primary?fillHeight={height}&quality=90&tag={Uri.EscapeDataString(person.ImageTag)}";

    /// <summary>
    /// The original file, streamed as is ("direct play"). mpv seeks in it with HTTP range requests.
    /// The token is in the link because mpv can't send Jellyfin's sign-in header.
    /// </summary>
    public string GetStreamUrl(JellyfinItem item, string? mediaSourceId = null)
        => $"{Address}/Videos/{item.Id}/stream?static=true&mediaSourceId={mediaSourceId ?? item.Id}&api_key={Uri.EscapeDataString(AccessToken ?? string.Empty)}";

    // ---------- Reading answers ----------

    private string RequireUser() => UserId ?? throw new JellyfinException("Sign in to your Jellyfin server first.");

    private async Task<IReadOnlyList<JellyfinItem>> GetItemsWithFallbackAsync(string path, string olderPath, CancellationToken cancel, string action)
    {
        JsonDocument json;
        try
        {
            json = await SendAsync(HttpMethod.Get, path, null, cancel, action);
        }
        catch (JellyfinException)
        {
            // Servers before 10.9 only have the older routes.
            json = await SendAsync(HttpMethod.Get, olderPath, null, cancel, action);
        }
        using (json)
            return ReadItems(json.RootElement).ToList();
    }

    private static IEnumerable<JellyfinItem> ReadItems(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Items", out var items) ? ReadItemArray(items) : [];

    private static IEnumerable<JellyfinItem> ReadItemArray(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var element in array.EnumerateArray())
        {
            if (ReadItem(element) is { } item)
                yield return item;
        }
    }

    private static JellyfinItem? ReadItem(JsonElement element)
    {
        var id = GetString(element, "Id");
        if (string.IsNullOrEmpty(id))
            return null;

        string? primary = null, thumb = null, logo = null;
        if (element.TryGetProperty("ImageTags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            primary = GetString(tags, "Primary");
            thumb = GetString(tags, "Thumb");
            logo = GetString(tags, "Logo");
        }

        double resume = 0;
        bool favorite = false, played = false;
        int? unplayed = null;
        if (element.TryGetProperty("UserData", out var userData) && userData.ValueKind == JsonValueKind.Object)
        {
            if (userData.TryGetProperty("PlaybackPositionTicks", out var ticks) && ticks.TryGetInt64(out var position))
                resume = position / 10_000_000d;
            favorite = GetBool(userData, "IsFavorite");
            played = GetBool(userData, "Played");
            unplayed = GetInt(userData, "UnplayedItemCount");
        }

        return new JellyfinItem
        {
            Id = id,
            Name = GetString(element, "Name") ?? "Untitled",
            Type = GetString(element, "Type") ?? string.Empty,
            CollectionType = GetString(element, "CollectionType"),
            IsFolder = GetBool(element, "IsFolder"),
            Year = GetInt(element, "ProductionYear"),
            EndYear = GetDate(element, "EndDate")?.Year,
            Status = GetString(element, "Status"),
            SeriesId = GetString(element, "SeriesId"),
            SeriesName = GetString(element, "SeriesName"),
            SeasonId = GetString(element, "SeasonId"),
            SeasonNumber = GetInt(element, "ParentIndexNumber"),
            EpisodeNumber = GetInt(element, "IndexNumber"),
            RunTimeSeconds = element.TryGetProperty("RunTimeTicks", out var runTime) && runTime.TryGetInt64(out var runTicks) ? runTicks / 10_000_000d : 0,
            Container = GetString(element, "Container"),
            DateCreated = GetDate(element, "DateCreated") ?? DateTime.MinValue,
            PremiereDate = GetDate(element, "PremiereDate"),
            Overview = GetString(element, "Overview"),
            Tagline = GetStrings(element, "Taglines").FirstOrDefault(),
            Genres = GetStrings(element, "Genres"),
            Studios = element.TryGetProperty("Studios", out var studios) && studios.ValueKind == JsonValueKind.Array
                ? studios.EnumerateArray().Select(studio => GetString(studio, "Name")).OfType<string>().ToList() : [],
            People = ReadPeople(element),
            CommunityRating = GetDouble(element, "CommunityRating"),
            CriticRating = GetDouble(element, "CriticRating"),
            OfficialRating = GetString(element, "OfficialRating"),
            TrailerUrl = element.TryGetProperty("RemoteTrailers", out var trailers) && trailers.ValueKind == JsonValueKind.Array && trailers.GetArrayLength() > 0
                ? GetString(trailers[0], "Url") : null,
            ChildCount = GetInt(element, "ChildCount"),
            UnplayedCount = unplayed,
            MediaSources = ReadMediaSources(element),
            PrimaryTag = primary,
            ThumbTag = thumb,
            LogoTag = logo,
            BackdropTag = GetStrings(element, "BackdropImageTags").FirstOrDefault(),
            SeriesPrimaryTag = GetString(element, "SeriesPrimaryImageTag"),
            ParentThumbItemId = GetString(element, "ParentThumbItemId"),
            ParentThumbTag = GetString(element, "ParentThumbImageTag"),
            ParentLogoItemId = GetString(element, "ParentLogoItemId"),
            ParentLogoTag = GetString(element, "ParentLogoImageTag"),
            ParentBackdropItemId = GetString(element, "ParentBackdropItemId"),
            ParentBackdropTag = GetStrings(element, "ParentBackdropImageTags").FirstOrDefault(),
            ResumeSeconds = resume,
            IsFavorite = favorite,
            IsPlayed = played
        };
    }

    private static IReadOnlyList<JellyfinPerson> ReadPeople(JsonElement element)
    {
        if (!element.TryGetProperty("People", out var people) || people.ValueKind != JsonValueKind.Array)
            return [];
        return people.EnumerateArray()
            .Select(person => GetString(person, "Id") is { } id && GetString(person, "Name") is { } name
                ? new JellyfinPerson
                {
                    Id = id, Name = name, Role = GetString(person, "Role"), Type = GetString(person, "Type") ?? string.Empty,
                    ImageTag = GetString(person, "PrimaryImageTag")
                }
                : null)
            .OfType<JellyfinPerson>()
            .ToList();
    }

    private static IReadOnlyList<JellyfinMediaSource> ReadMediaSources(JsonElement element)
    {
        if (!element.TryGetProperty("MediaSources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            return [];
        return sources.EnumerateArray()
            .Select(source => GetString(source, "Id") is { } id
                ? new JellyfinMediaSource
                {
                    Id = id,
                    Name = GetString(source, "Name"),
                    Size = source.TryGetProperty("Size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                    Container = GetString(source, "Container"),
                    DefaultAudioIndex = GetInt(source, "DefaultAudioStreamIndex"),
                    DefaultSubtitleIndex = GetInt(source, "DefaultSubtitleStreamIndex"),
                    Streams = source.TryGetProperty("MediaStreams", out var streams) && streams.ValueKind == JsonValueKind.Array
                        ? streams.EnumerateArray().Select(stream => new JellyfinStream
                        {
                            Index = GetInt(stream, "Index") ?? 0,
                            Type = GetString(stream, "Type") ?? string.Empty,
                            Language = GetString(stream, "Language"),
                            DisplayTitle = GetString(stream, "DisplayTitle"),
                            Codec = GetString(stream, "Codec"),
                            IsExternal = GetBool(stream, "IsExternal"),
                            IsDefault = GetBool(stream, "IsDefault"),
                            IsForced = GetBool(stream, "IsForced"),
                            Width = GetInt(stream, "Width") ?? 0,
                            Height = GetInt(stream, "Height") ?? 0,
                            VideoRangeType = GetString(stream, "VideoRangeType")
                        }).ToList()
                        : []
                }
                : null)
            .OfType<JellyfinMediaSource>()
            .ToList();
    }

    // ---------- HTTP ----------

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? jsonBody, CancellationToken cancel, string action)
    {
        using var request = new HttpRequestMessage(method, Address + path);
        request.Headers.TryAddWithoutValidation("Authorization", BuildAuthorizationHeader());
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (jsonBody is not null || method == HttpMethod.Post)
            request.Content = new StringContent(jsonBody ?? string.Empty, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancel);
        }
        catch (TaskCanceledException exception) when (!cancel.IsCancellationRequested)
        {
            throw new JellyfinException($"Couldn't {action}: the server took too long to answer.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new JellyfinException($"Couldn't {action}: {exception.Message}", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new JellyfinException((int)response.StatusCode switch
                {
                    401 => action == "sign in" ? "Wrong username or password." : "The server signed you out. Sign in again.",
                    403 => $"Couldn't {action}: the server refused.",
                    404 => $"Couldn't {action}: this server doesn't support it.",
                    _ => $"Couldn't {action} (the server answered {(int)response.StatusCode})."
                });
            }

            var text = await response.Content.ReadAsStringAsync(cancel);
            try
            {
                return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            }
            catch (JsonException exception)
            {
                throw new JellyfinException($"Couldn't {action}: the answer wasn't from Jellyfin.", exception);
            }
        }
    }

    private string BuildAuthorizationHeader()
    {
        var header = $"MediaBrowser Client=\"Iris\", Device=\"{Escape(Environment.MachineName)}\", DeviceId=\"{JellyfinSession.DeviceId}\", Version=\"{AppVersion}\"";
        return AccessToken is null ? header : header + $", Token=\"{AccessToken}\"";

        static string Escape(string value) => value.Replace("\"", string.Empty, StringComparison.Ordinal);
    }

    private static readonly string AppVersion =
        typeof(JellyfinClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static double? GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTime? GetDate(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out var date) ? date : null;

    private static IReadOnlyList<string> GetStrings(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String).Select(entry => entry.GetString()!).ToList()
            : [];
}
