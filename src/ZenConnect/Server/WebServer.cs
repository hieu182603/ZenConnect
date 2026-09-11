using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZenConnect.Server;

public static class ZenWebServer
{
    public static WebApplication BuildApp(string[]? args = null, string? customUrl = null)
    {
        string? webRootPath = null;
        string baseWwwRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(baseWwwRoot))
        {
            webRootPath = baseWwwRoot;
        }

        WebApplicationOptions options = new()
        {
            Args = args ?? Array.Empty<string>(),
            WebRootPath = webRootPath
        };

        WebApplicationBuilder builder = WebApplication.CreateBuilder(options);
        // The desktop app runs without administrator rights. The Windows Event Log
        // provider can throw AccessDenied while writing a warning and abort the
        // active HTTP request, so keep local/server logging on stdout only.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        // URL binding resolution
        string? renderPort = Environment.GetEnvironmentVariable("PORT");
        string? aspnetUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");

        if (int.TryParse(renderPort, out int port) && port > 0 && port <= 65535)
        {
            builder.WebHost.UseUrls("http://0.0.0.0:" + port);
        }
        else if (!string.IsNullOrWhiteSpace(customUrl))
        {
            builder.WebHost.UseUrls(customUrl);
        }
        else if (!string.IsNullOrWhiteSpace(aspnetUrls))
        {
            builder.WebHost.UseUrls(aspnetUrls);
        }
        else
        {
            string? cliUrl = null;
            if (args != null)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], "--urls", StringComparison.OrdinalIgnoreCase))
                    {
                        cliUrl = args[i + 1];
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(cliUrl))
            {
                builder.WebHost.UseUrls(cliUrl);
            }
            else
            {
                builder.WebHost.UseUrls("http://0.0.0.0:5080");
            }
        }

        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = 12 * 1024 * 1024;
        });

        string dataDirectory = Environment.GetEnvironmentVariable("SCREEN_REVIEW_DATA_DIR")
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenConnect", "server-data")
                : Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
        int maxScreenshotsPerRoom = ReadPositiveInt("SCREEN_REVIEW_MAX_SCREENSHOTS_PER_ROOM", 200, 10, 2000);
        builder.Services.AddSingleton(new ReviewStore(dataDirectory, maxScreenshotsPerRoom));

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 180,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Too many requests. Please wait before trying again." },
                    cancellationToken);
            };
        });

        WebApplication app = builder.Build();

        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' blob: data:; style-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
            if (context.Request.Path.StartsWithSegments("/api")
                || context.Request.Path == "/"
                || context.Request.Path.Value?.EndsWith(".html", StringComparison.OrdinalIgnoreCase) == true
                || context.Request.Path.Value?.EndsWith(".js", StringComparison.OrdinalIgnoreCase) == true
                || context.Request.Path.Value?.EndsWith(".css", StringComparison.OrdinalIgnoreCase) == true)
            {
                context.Response.Headers.CacheControl = "no-store";
            }
            await next();
        });

        app.UseRateLimiter();
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

        app.MapGet("/api/screenshots", async (HttpRequest request, ReviewStore store) =>
            await ListScreenshotsAsync(GetRoomKey(request), store));

        app.MapDelete("/api/screenshots", async (HttpRequest request, ReviewStore store) =>
        {
            string roomKey = GetRoomKey(request);
            if (!RoomKey.IsValid(roomKey) || !await store.RoomExistsAsync(roomKey))
                return InvalidRoomKey();

            RoomDeleteResult deleted = await store.DeleteRoomScreenshotsAsync(roomKey);
            if (deleted.FailedImageCount > 0)
                app.Logger.LogWarning("Bulk delete left {Count} image files on disk.", deleted.FailedImageCount);
            return Results.Ok(deleted);
        });

        app.MapGet("/api/screenshots/export", async (HttpContext context, ReviewStore store) =>
        {
            string roomKey = GetRoomKey(context.Request);
            if (!RoomKey.IsValid(roomKey) || !await store.RoomExistsAsync(roomKey))
                return InvalidRoomKey();

            try
            {
                RoomArchive? archive = await store.CreateRoomArchiveAsync(roomKey, context.RequestAborted);
                if (archive == null)
                    return Results.BadRequest(new { error = "Phòng chưa có ảnh để xuất." });
                context.Response.Headers["X-Screenshot-Count"] = archive.ImageCount.ToString();
                return Results.File(archive.Stream, "application/zip", archive.FileName);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                app.Logger.LogWarning("Room export failed: {ErrorType}.", exception.GetType().Name);
                return Results.Conflict(new { error = "Không thể xuất đầy đủ ảnh. Có ảnh bị thiếu hoặc không đọc được, hoặc server không đủ dung lượng. Hãy kiểm tra và thử lại." });
            }
        });

        app.MapGet("/api/sessions/{session}/screenshots", async (string session, HttpRequest request, ReviewStore store) =>
        {
            if (!await CanAccessRoomAsync(request, session, store))
                return InvalidRoomKey();
            return await ListScreenshotsAsync(session, store);
        });

        app.MapPost("/api/screenshots", async (HttpRequest request, ReviewStore store) =>
            await UploadScreenshotAsync(GetRoomKey(request), request, store));

        app.MapPost("/api/sessions/{session}/screenshots", async (string session, HttpRequest request, ReviewStore store) =>
        {
            if (!await CanAccessRoomAsync(request, session, store))
                return InvalidRoomKey();
            return await UploadScreenshotAsync(session, request, store);
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

        app.MapDelete("/api/screenshots/{id:guid}", async (Guid id, HttpRequest request, ReviewStore store) =>
        {
            ScreenshotRecord? screenshot = await store.FindScreenshotAsync(id);
            if (screenshot == null)
                return Results.NotFound();
            if (!MatchesRoomKey(request, screenshot.Session))
                return InvalidRoomKey();

            ScreenshotDeleteResult? deleted = await store.DeleteScreenshotAsync(id, screenshot.Session);
            if (deleted == null)
                return Results.NotFound();

            if (!deleted.ImageDeleted)
                app.Logger.LogWarning("Screenshot {ScreenshotId} metadata was deleted, but its image file could not be removed.", deleted.Id);
            else
                app.Logger.LogInformation("Deleted screenshot {ScreenshotId} (sequence {Sequence}) and {MessageCount} related messages.", deleted.Id, deleted.Sequence, deleted.DeletedMessageCount);

            return Results.NoContent();
        });

        app.MapGet("/api/messages", async (long? after, HttpRequest request, ReviewStore store) =>
            await ListMessagesAsync(GetRoomKey(request), Math.Max(0, after ?? 0), store));

        app.MapPost("/api/messages", async (MessageRequest message, HttpRequest request, ReviewStore store) =>
        {
            string roomKey = GetRoomKey(request);
            if (!RoomKey.IsValid(roomKey) || !await store.RoomExistsAsync(roomKey))
                return InvalidRoomKey();

            string text = (message.Text ?? string.Empty).Trim();
            if (text.Length == 0)
                return Results.BadRequest(new { error = "Message cannot be empty." });
            if (text.Length > 2000)
                return Results.BadRequest(new { error = "Message exceeds 2000 characters." });

            MessageView? created = await store.AddRoomMessageAsync(roomKey, text);
            return created == null ? Results.NotFound() : Results.Ok(created);
        });

        app.MapGet("/api/sessions/{session}/messages", async (string session, long? after, HttpRequest request, ReviewStore store) =>
        {
            if (!await CanAccessRoomAsync(request, session, store))
                return InvalidRoomKey();
            return await ListMessagesAsync(session, Math.Max(0, after ?? 0), store);
        });

        app.MapDelete("/api/messages/{id:long}", async (long id, HttpRequest request, ReviewStore store) =>
        {
            string roomKey = GetRoomKey(request);
            if (!RoomKey.IsValid(roomKey) || !await store.RoomExistsAsync(roomKey))
                return InvalidRoomKey();

            MessageDeleteResult? deleted = await store.DeleteMessageAsync(id, roomKey);
            if (deleted == null)
                return Results.NotFound();

            app.Logger.LogInformation("Deleted message {MessageId}; screenshot {ScreenshotId}.", deleted.Id, deleted.ScreenshotId);
            return Results.NoContent();
        });

        app.MapFallbackToFile("index.html");

        return app;
    }

    public static async Task RunAsync(string[] args)
    {
        WebApplication app = BuildApp(args);
        await app.RunAsync();
    }

    public static async Task<WebApplication> StartBackgroundAsync(string url, CancellationToken cancellationToken = default)
    {
        WebApplication app = BuildApp(null, url);
        await app.StartAsync(cancellationToken);
        return app;
    }

    private static int ReadPositiveInt(string name, int fallback, int minimum, int maximum)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int value) && value >= minimum && value <= maximum
            ? value
            : fallback;
    }

    private static async Task<IResult> ListScreenshotsAsync(string roomKey, ReviewStore store)
    {
        if (!RoomKey.IsValid(roomKey) || !await store.RoomExistsAsync(roomKey))
            return InvalidRoomKey();

        var screenshots = await store.GetScreenshotsAsync(roomKey);
        return Results.Ok(screenshots.Select(item => new
        {
            item.Id,
            item.Sequence,
            item.CreatedAt,
            item.Handled,
            imageUrl = "/api/screenshots/" + item.Id + "/image",
            message = item.MessageText
        }));
    }

    private static async Task<IResult> UploadScreenshotAsync(string roomKey, HttpRequest request, ReviewStore store)
    {
        if (!RoomKey.IsValid(roomKey) || !await store.RoomExistsAsync(roomKey))
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
        try
        {
            await using (var output = File.Create(destination))
                await image.CopyToAsync(output);

            if (!await IsPngAsync(destination))
            {
                TryDeleteFile(destination);
                return Results.BadRequest(new { error = "The uploaded file is not a valid PNG." });
            }

            ScreenshotRecord created = await store.AddScreenshotAsync(roomKey, fileName);
            return Results.Ok(new
            {
                created.Id,
                created.Sequence,
                created.CreatedAt
            });
        }
        catch
        {
            TryDeleteFile(destination);
            throw;
        }
    }

    private static async Task<IResult> ListMessagesAsync(string roomKey, long after, ReviewStore store)
    {
        if (!RoomKey.IsValid(roomKey) || !await store.RoomExistsAsync(roomKey))
            return InvalidRoomKey();
        return Results.Ok(await store.GetMessagesAsync(roomKey, after));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static string GetRoomKey(HttpRequest request)
    {
        return request.Headers["X-Screen-Review-Key"].ToString().Trim().ToUpperInvariant();
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        byte[] left = Encoding.UTF8.GetBytes(supplied);
        byte[] right = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static bool MatchesRoomKey(HttpRequest request, string roomKey)
    {
        string supplied = GetRoomKey(request);
        return RoomKey.IsValid(supplied) && FixedTimeEquals(supplied, roomKey.ToUpperInvariant());
    }

    private static async Task<bool> CanAccessRoomAsync(HttpRequest request, string roomKey, ReviewStore store)
    {
        return RoomKey.IsValid(roomKey)
            && MatchesRoomKey(request, roomKey)
            && await store.RoomExistsAsync(roomKey);
    }

    private static IResult InvalidRoomKey()
    {
        return Results.Json(new { error = "Room key is invalid or the room is not registered." }, statusCode: 401);
    }

    private static async Task<bool> IsPngAsync(string path)
    {
        byte[] expected = { 137, 80, 78, 71, 13, 10, 26, 10 };
        byte[] actual = new byte[expected.Length];
        await using var stream = File.OpenRead(path);
        int read = await stream.ReadAsync(actual);
        return read == expected.Length && actual.SequenceEqual(expected);
    }
}
