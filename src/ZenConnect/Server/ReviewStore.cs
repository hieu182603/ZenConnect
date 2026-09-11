using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ZenConnect.Server;

public static class RoomKey
{
    public const string Error = "Room key must use the format XXXX-XXXX-XXXX-XXXX-XXXX.";
    private static readonly Regex Pattern = new("^[A-HJ-NP-Z2-9]{4}(?:-[A-HJ-NP-Z2-9]{4}){4}$", RegexOptions.Compiled);
    public static bool IsValid(string? value) => Pattern.IsMatch(value ?? string.Empty);
}

public sealed class ReviewStore
{
    private readonly string _storePath;
    private readonly string _backupPath;
    private readonly string _imageDirectory;
    private readonly int _maxScreenshotsPerRoom;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public ReviewStore(string dataDirectory, int maxScreenshotsPerRoom)
    {
        Directory.CreateDirectory(dataDirectory);
        _imageDirectory = Path.Combine(dataDirectory, "images");
        Directory.CreateDirectory(_imageDirectory);
        _storePath = Path.Combine(dataDirectory, "store.json");
        _backupPath = Path.Combine(dataDirectory, "store.backup.json");
        _maxScreenshotsPerRoom = maxScreenshotsPerRoom;
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

            List<ScreenshotRecord> overflow = state.Screenshots
                .Where(item => string.Equals(item.Session, session, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Sequence)
                .Skip(_maxScreenshotsPerRoom)
                .ToList();
            if (overflow.Count > 0)
            {
                HashSet<Guid> expiredIds = overflow.Select(item => item.Id).ToHashSet();
                state.Messages.RemoveAll(item => item.ScreenshotId.HasValue && expiredIds.Contains(item.ScreenshotId.Value));
                state.Screenshots.RemoveAll(item => expiredIds.Contains(item.Id));
            }

            await SaveAsync(state);
            foreach (ScreenshotRecord expired in overflow)
                DeleteImageFile(expired.FileName);
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
                Session = screenshot.Session,
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

    public async Task<MessageView?> AddRoomMessageAsync(string session, string text)
    {
        await _gate.WaitAsync();
        try
        {
            StorageState state = await LoadAsync();
            if (!state.Rooms.Any(item => string.Equals(item.Key, session, StringComparison.OrdinalIgnoreCase)))
                return null;

            long nextId = Math.Max(
                state.NextMessageId,
                state.Messages.Select(item => item.Id).DefaultIfEmpty(0).Max() + 1);
            var message = new MessageRecord
            {
                Id = nextId,
                Session = session,
                ScreenshotId = null,
                Text = text,
                CreatedAt = DateTimeOffset.UtcNow
            };
            state.NextMessageId = nextId + 1;
            state.Messages.Add(message);
            await SaveAsync(state);
            return ToMessageView(message, null);
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

    public async Task<ScreenshotDeleteResult?> DeleteScreenshotAsync(Guid screenshotId, string session)
    {
        await _gate.WaitAsync();
        try
        {
            StorageState state = await LoadAsync();
            ScreenshotRecord? screenshot = state.Screenshots.FirstOrDefault(item =>
                item.Id == screenshotId
                && string.Equals(item.Session, session, StringComparison.OrdinalIgnoreCase));
            if (screenshot == null)
                return null;

            int deletedMessageCount = state.Messages.RemoveAll(item => item.ScreenshotId == screenshotId);
            state.Screenshots.Remove(screenshot);
            await SaveAsync(state);

            return new ScreenshotDeleteResult
            {
                Id = screenshot.Id,
                Sequence = screenshot.Sequence,
                DeletedMessageCount = deletedMessageCount,
                ImageDeleted = DeleteImageFile(screenshot.FileName)
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MessageDeleteResult?> DeleteMessageAsync(long messageId, string session)
    {
        await _gate.WaitAsync();
        try
        {
            StorageState state = await LoadAsync();
            HashSet<Guid> roomScreenshotIds = state.Screenshots
                .Where(item => string.Equals(item.Session, session, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Id)
                .ToHashSet();
            MessageRecord? message = state.Messages.FirstOrDefault(item =>
                item.Id == messageId
                && (string.Equals(item.Session, session, StringComparison.OrdinalIgnoreCase)
                    || (item.ScreenshotId.HasValue && roomScreenshotIds.Contains(item.ScreenshotId.Value))));
            if (message == null)
                return null;

            state.Messages.Remove(message);
            await SaveAsync(state);
            return new MessageDeleteResult
            {
                Id = message.Id,
                ScreenshotId = message.ScreenshotId
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RoomDeleteResult> DeleteRoomScreenshotsAsync(string session)
    {
        await _gate.WaitAsync();
        try
        {
            StorageState state = await LoadAsync();
            var screenshots = state.Screenshots
                .Where(item => string.Equals(item.Session, session, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var ids = screenshots.Select(item => item.Id).ToHashSet();
            int messages = state.Messages.RemoveAll(item => item.ScreenshotId.HasValue && ids.Contains(item.ScreenshotId.Value));
            state.Screenshots.RemoveAll(item => ids.Contains(item.Id));
            // Keep the room registration so clients can continue uploading.
            await SaveAsync(state);
            int failedFiles = screenshots.Count(item => !DeleteImageFile(item.FileName));
            return new RoomDeleteResult(screenshots.Count, messages, failedFiles);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RoomArchive?> CreateRoomArchiveAsync(string session, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        FileStream? output = null;
        try
        {
            StorageState state = await LoadAsync();
            var screenshots = state.Screenshots
                .Where(item => string.Equals(item.Session, session, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Sequence).ThenBy(item => item.CreatedAt).ToList();
            if (screenshots.Count == 0) return null;

            string folder = "de-zenconnect-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            // Build on disk, not in RAM. DeleteOnClose also cleans up aborted downloads.
            output = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (int index = 0; index < screenshots.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var screenshot = screenshots[index];
                    await using var source = File.OpenRead(GetImagePath(screenshot.FileName));
                    if (source.Length == 0) throw new IOException("An image is empty.");
                    var entry = archive.CreateEntry(
                        $"{folder}/{index + 1:D4}-anh-{screenshot.Sequence:D4}.png", CompressionLevel.NoCompression);
                    await using var destination = entry.Open();
                    await source.CopyToAsync(destination, cancellationToken);
                }
            }
            // The gate holds a consistent snapshot until every image has been copied.
            output.Position = 0;
            return new RoomArchive(output, folder + ".zip", screenshots.Count);
        }
        catch
        {
            if (output != null) await output.DisposeAsync();
            throw;
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
                .Where(item => item.Id > after
                    && (string.Equals(item.Session, session, StringComparison.OrdinalIgnoreCase)
                        || (item.ScreenshotId.HasValue && screenshots.ContainsKey(item.ScreenshotId.Value))))
                .OrderBy(item => item.Id)
                .Select(item => ToMessageView(
                    item,
                    item.ScreenshotId.HasValue && screenshots.TryGetValue(item.ScreenshotId.Value, out ScreenshotRecord? screenshot)
                        ? screenshot
                        : null))
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

        try
        {
            return await ReadStoreFileAsync(_storePath);
        }
        catch (Exception exception) when (exception is JsonException || exception is IOException)
        {
            if (!File.Exists(_backupPath))
                throw new InvalidDataException("The review store is unreadable and no backup is available.", exception);

            try
            {
                return await ReadStoreFileAsync(_backupPath);
            }
            catch (Exception backupException) when (backupException is JsonException || backupException is IOException)
            {
                throw new InvalidDataException("The review store and its backup are unreadable.", backupException);
            }
        }
    }

    private async Task SaveAsync(StorageState state)
    {
        string temporaryPath = _storePath + ".tmp";
        string json = JsonSerializer.Serialize(state, _jsonOptions);
        await File.WriteAllTextAsync(temporaryPath, json);
        File.Move(temporaryPath, _storePath, true);
        File.Copy(_storePath, _backupPath, true);
    }

    private async Task<StorageState> ReadStoreFileAsync(string path)
    {
        string json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<StorageState>(json, _jsonOptions) ?? new StorageState();
    }

    private bool DeleteImageFile(string fileName)
    {
        try
        {
            string path = GetImagePath(fileName);
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MessageView ToMessageView(MessageRecord message, ScreenshotRecord? screenshot)
    {
        return new MessageView
        {
            Id = message.Id,
            ScreenshotId = screenshot?.Id,
            Sequence = screenshot?.Sequence,
            Text = message.Text,
            CreatedAt = message.CreatedAt
        };
    }
}

public sealed class StorageState
{
    public List<RoomRecord> Rooms { get; set; } = new();
    public List<ScreenshotRecord> Screenshots { get; set; } = new();
    public List<MessageRecord> Messages { get; set; } = new();
    public long NextMessageId { get; set; } = 1;
}

public sealed class RoomRecord
{
    public string Key { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ScreenshotRecord
{
    public Guid Id { get; set; }
    public string Session { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string FileName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public bool Handled { get; set; }
}

public sealed class MessageRecord
{
    public long Id { get; set; }
    public Guid? ScreenshotId { get; set; }
    public string? Session { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ScreenshotView
{
    public Guid Id { get; set; }
    public int Sequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool Handled { get; set; }
    public string? MessageText { get; set; }
}

public sealed class ScreenshotDeleteResult
{
    public Guid Id { get; set; }
    public int Sequence { get; set; }
    public int DeletedMessageCount { get; set; }
    public bool ImageDeleted { get; set; }
}

public sealed class MessageDeleteResult
{
    public long Id { get; set; }
    public Guid? ScreenshotId { get; set; }
}

public sealed record RoomDeleteResult(int DeletedScreenshotCount, int DeletedMessageCount, int FailedImageCount);
public sealed record RoomArchive(FileStream Stream, string FileName, int ImageCount);

public sealed class MessageView
{
    public long Id { get; set; }
    public Guid? ScreenshotId { get; set; }
    public int? Sequence { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MessageRequest
{
    public string? Text { get; set; }
}

public sealed class HandledRequest
{
    public bool Handled { get; set; }
}
