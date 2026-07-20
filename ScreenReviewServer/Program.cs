using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

string? renderPort = Environment.GetEnvironmentVariable("PORT");
if (int.TryParse(renderPort, out int port) && port > 0 && port <= 65535)
    builder.WebHost.UseUrls("http://0.0.0.0:" + port);

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 12 * 1024 * 1024;
});

string dataDirectory = Environment.GetEnvironmentVariable("SCREEN_REVIEW_DATA_DIR")
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");
builder.Services.AddSingleton(new ReviewStore(dataDirectory));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/rooms/register", async (HttpRequest request, ReviewStore store) =>
{
    string roomKey = GetRoomKey(request);
    if (!RoomKey.IsValid(roomKey))
        return Results.BadRequest(new { error = RoomKey.Error });

    await store.RegisterRoomAsync(roomKey);
    return Results.Ok(new { session = roomKey });
});

app.MapGet("/api/room", async (HttpRequest request, ReviewStore store) =>
{
    string roomKey = GetRoomKey(request);
    if (!RoomKey.IsValid(roomKey) || !await store.RoomExistsAsync(roomKey))
        return InvalidRoomKey();

    return Results.Ok(new { session = roomKey });
});

app.MapGet("/api/sessions/{session}/screenshots", async (string session, HttpRequest request, ReviewStore store) =>
{
    if (!await CanAccessRoomAsync(request, session, store))
        return InvalidRoomKey();

    var screenshots = await store.GetScreenshotsAsync(session);
    return Results.Ok(screenshots.Select(item => new
    {
        item.Id,
        item.Sequence,
        item.CreatedAt,
        item.Handled,
        imageUrl = "/api/screenshots/" + item.Id + "/image",
        message = item.MessageText
    }));
});

app.MapPost("/api/sessions/{session}/screenshots", async (string session, HttpRequest request, ReviewStore store) =>
{
    if (!await CanAccessRoomAsync(request, session, store))
        return InvalidRoomKey();
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Expected multipart form data." });

    IFormCollection form = await request.ReadFormAsync();
    IFormFile? image = form.Files.GetFile("image");
    if (image == null || image.Length == 0)
        return Results.BadRequest(new { error = "Image is required." });
    if (image.Length > 10 * 1024 * 1024)
        return Results.BadRequest(new { error = "Image exceeds the 10 MB limit." });
    if (!string.Equals(image.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Only PNG images are accepted." });

    string fileName = Guid.NewGuid().ToString("N") + ".png";
    string destination = store.GetImagePath(fileName);
    await using (var output = File.Create(destination))
        await image.CopyToAsync(output);

    if (!await IsPngAsync(destination))
    {
        File.Delete(destination);
        return Results.BadRequest(new { error = "The uploaded file is not a valid PNG." });
    }

    ScreenshotRecord created = await store.AddScreenshotAsync(session, fileName);
    return Results.Ok(new
    {
        created.Id,
        created.Sequence,
        created.CreatedAt
    });
});

app.MapGet("/api/screenshots/{id:guid}/image", async (Guid id, HttpRequest request, ReviewStore store) =>
{
    ScreenshotRecord? screenshot = await store.FindScreenshotAsync(id);
    if (screenshot == null)
        return Results.NotFound();
    if (!MatchesRoomKey(request, screenshot.Session))
        return InvalidRoomKey();

    string path = store.GetImagePath(screenshot.FileName);
    return File.Exists(path)
        ? Results.File(path, "image/png", enableRangeProcessing: true)
        : Results.NotFound();
});

app.MapPost("/api/screenshots/{id:guid}/message", async (Guid id, MessageRequest message, HttpRequest request, ReviewStore store) =>
{
    ScreenshotRecord? screenshot = await store.FindScreenshotAsync(id);
    if (screenshot == null)
        return Results.NotFound();
    if (!MatchesRoomKey(request, screenshot.Session))
        return InvalidRoomKey();

    string text = (message.Text ?? string.Empty).Trim();
    if (text.Length == 0)
        return Results.BadRequest(new { error = "Message cannot be empty." });
    if (text.Length > 2000)
        return Results.BadRequest(new { error = "Message exceeds 2000 characters." });

    MessageView? created = await store.AddMessageAsync(id, text);
    return created == null ? Results.NotFound() : Results.Ok(created);
});

app.MapPatch("/api/screenshots/{id:guid}/handled", async (Guid id, HandledRequest handled, HttpRequest request, ReviewStore store) =>
{
    ScreenshotRecord? screenshot = await store.FindScreenshotAsync(id);
    if (screenshot == null)
        return Results.NotFound();
    if (!MatchesRoomKey(request, screenshot.Session))
        return InvalidRoomKey();

    bool updated = await store.SetHandledAsync(id, handled.Handled);
    return updated ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/sessions/{session}/messages", async (string session, long? after, HttpRequest request, ReviewStore store) =>
{
    if (!await CanAccessRoomAsync(request, session, store))
        return InvalidRoomKey();

    return Results.Ok(await store.GetMessagesAsync(session, Math.Max(0, after ?? 0)));
});

app.MapFallbackToFile("index.html");
app.Run();

static string GetRoomKey(HttpRequest request)
{
    return request.Headers["X-Screen-Review-Key"].ToString().Trim().ToUpperInvariant();
}

static bool FixedTimeEquals(string supplied, string expected)
{
    byte[] left = System.Text.Encoding.UTF8.GetBytes(supplied);
    byte[] right = System.Text.Encoding.UTF8.GetBytes(expected);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
}

static bool MatchesRoomKey(HttpRequest request, string roomKey)
{
    string supplied = GetRoomKey(request);
    return RoomKey.IsValid(supplied) && FixedTimeEquals(supplied, roomKey.ToUpperInvariant());
}

static async Task<bool> CanAccessRoomAsync(HttpRequest request, string roomKey, ReviewStore store)
{
    return RoomKey.IsValid(roomKey)
        && MatchesRoomKey(request, roomKey)
        && await store.RoomExistsAsync(roomKey);
}

static IResult InvalidRoomKey()
{
    return Results.Json(new { error = "Room key is invalid or the room is not registered." }, statusCode: 401);
}

static async Task<bool> IsPngAsync(string path)
{
    byte[] expected = { 137, 80, 78, 71, 13, 10, 26, 10 };
    byte[] actual = new byte[expected.Length];
    await using var stream = File.OpenRead(path);
    int read = await stream.ReadAsync(actual);
    return read == expected.Length && actual.SequenceEqual(expected);
}

static class RoomKey
{
    public const string Error = "Room key must use the format XXXX-XXXX-XXXX-XXXX-XXXX.";
    private static readonly Regex Pattern = new("^[A-HJ-NP-Z2-9]{4}(?:-[A-HJ-NP-Z2-9]{4}){4}$", RegexOptions.Compiled);
    public static bool IsValid(string value) => Pattern.IsMatch(value ?? string.Empty);
}

sealed class ReviewStore
{
    private readonly string _storePath;
    private readonly string _imageDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public ReviewStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _imageDirectory = Path.Combine(dataDirectory, "images");
        Directory.CreateDirectory(_imageDirectory);
        _storePath = Path.Combine(dataDirectory, "store.json");
    }

    public string GetImagePath(string fileName) => Path.Combine(_imageDirectory, Path.GetFileName(fileName));

    public async Task RegisterRoomAsync(string roomKey)
    {
        await _gate.WaitAsync();
        try
        {
            StorageState state = await LoadAsync();
            if (state.Rooms.Any(item => string.Equals(item.Key, roomKey, StringComparison.OrdinalIgnoreCase)))
                return;

            state.Rooms.Add(new RoomRecord
            {
                Key = roomKey.ToUpperInvariant(),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await SaveAsync(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RoomExistsAsync(string roomKey)
    {
        await _gate.WaitAsync();
        try
        {
            StorageState state = await LoadAsync();
            return state.Rooms.Any(item => string.Equals(item.Key, roomKey, StringComparison.OrdinalIgnoreCase))
                || state.Screenshots.Any(item => string.Equals(item.Session, roomKey, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScreenshotRecord> AddScreenshotAsync(string session, string fileName)
    {
        await _gate.WaitAsync();
        try
        {
            StorageState state = await LoadAsync();
            int sequence = state.Screenshots
                .Where(item => string.Equals(item.Session, session, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Sequence)
                .DefaultIfEmpty(0)
                .Max() + 1;

            var screenshot = new ScreenshotRecord
            {
                Id = Guid.NewGuid(),
                Session = session,
                Sequence = sequence,
                FileName = fileName,
                CreatedAt = DateTimeOffset.UtcNow
            };
            state.Screenshots.Add(screenshot);
            await SaveAsync(state);
            return screenshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ScreenshotView>> GetScreenshotsAsync(string session)
    {
        await _gate.WaitAsync();
        try
        {
            StorageState state = await LoadAsync();
            return state.Screenshots
                .Where(item => string.Equals(item.Session, session, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Sequence)
                .Select(item => new ScreenshotView
                {
                    Id = item.Id,
                    Sequence = item.Sequence,
                    CreatedAt = item.CreatedAt,
                    Handled = item.Handled,
                    MessageText = state.Messages
                        .Where(message => message.ScreenshotId == item.Id)
                        .OrderByDescending(message => message.Id)
                        .Select(message => message.Text)
                        .FirstOrDefault()
                })
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScreenshotRecord?> FindScreenshotAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            StorageState state = await LoadAsync();
            return state.Screenshots.FirstOrDefault(item => item.Id == id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MessageView?> AddMessageAsync(Guid screenshotId, string text)
    {
        await _gate.WaitAsync();
        try
        {
            StorageState state = await LoadAsync();
            ScreenshotRecord? screenshot = state.Screenshots.FirstOrDefault(item => item.Id == screenshotId);
            if (screenshot == null)
                return null;

            long nextId = Math.Max(
                state.NextMessageId,
                state.Messages.Select(item => item.Id).DefaultIfEmpty(0).Max() + 1);
            var message = new MessageRecord
            {
                Id = nextId,
                ScreenshotId = screenshot.Id,
                Text = text,
                CreatedAt = DateTimeOffset.UtcNow
            };
            state.NextMessageId = nextId + 1;
            state.Messages.Add(message);
            screenshot.Handled = true;
            await SaveAsync(state);
            return ToMessageView(message, screenshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SetHandledAsync(Guid screenshotId, bool handled)
    {
        await _gate.WaitAsync();
        try
        {
            StorageState state = await LoadAsync();
            ScreenshotRecord? screenshot = state.Screenshots.FirstOrDefault(item => item.Id == screenshotId);
            if (screenshot == null)
                return false;
            screenshot.Handled = handled;
            await SaveAsync(state);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MessageView>> GetMessagesAsync(string session, long after)
    {
        await _gate.WaitAsync();
        try
        {
            StorageState state = await LoadAsync();
            var screenshots = state.Screenshots
                .Where(item => string.Equals(item.Session, session, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(item => item.Id);

            return state.Messages
                .Where(item => item.Id > after && screenshots.ContainsKey(item.ScreenshotId))
                .OrderBy(item => item.Id)
                .Select(item => ToMessageView(item, screenshots[item.ScreenshotId]))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StorageState> LoadAsync()
    {
        if (!File.Exists(_storePath))
            return new StorageState();

        string json = await File.ReadAllTextAsync(_storePath);
        return JsonSerializer.Deserialize<StorageState>(json, _jsonOptions) ?? new StorageState();
    }

    private async Task SaveAsync(StorageState state)
    {
        string temporaryPath = _storePath + ".tmp";
        string json = JsonSerializer.Serialize(state, _jsonOptions);
        await File.WriteAllTextAsync(temporaryPath, json);
        File.Move(temporaryPath, _storePath, true);
    }

    private static MessageView ToMessageView(MessageRecord message, ScreenshotRecord screenshot)
    {
        return new MessageView
        {
            Id = message.Id,
            ScreenshotId = screenshot.Id,
            Sequence = screenshot.Sequence,
            Text = message.Text,
            CreatedAt = message.CreatedAt
        };
    }
}

sealed class StorageState
{
    public List<RoomRecord> Rooms { get; set; } = new();
    public List<ScreenshotRecord> Screenshots { get; set; } = new();
    public List<MessageRecord> Messages { get; set; } = new();
    public long NextMessageId { get; set; } = 1;
}

sealed class RoomRecord
{
    public string Key { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

sealed class ScreenshotRecord
{
    public Guid Id { get; set; }
    public string Session { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string FileName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public bool Handled { get; set; }
}

sealed class MessageRecord
{
    public long Id { get; set; }
    public Guid ScreenshotId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

sealed class ScreenshotView
{
    public Guid Id { get; set; }
    public int Sequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool Handled { get; set; }
    public string? MessageText { get; set; }
}

sealed class MessageView
{
    public long Id { get; set; }
    public Guid ScreenshotId { get; set; }
    public int Sequence { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

sealed class MessageRequest
{
    public string? Text { get; set; }
}

sealed class HandledRequest
{
    public bool Handled { get; set; }
}
