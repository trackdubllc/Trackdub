using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trackdub.Contracts;
using Trackdub.Contracts.Projects;

namespace Trackdub.Infrastructure.FileSystem;

public sealed class FileSystemVoiceCloneAuditLog(ITranscriptWorkspaceContext workspaceContext) : IVoiceCloneAuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly ITranscriptWorkspaceContext workspaceContext = workspaceContext ?? throw new ArgumentNullException(nameof(workspaceContext));
    private readonly SemaphoreSlim appendGate = new(1, 1);

    // Cached last-entry hash; null means the file has not yet been read this session.
    // Only accessed under appendGate, so no additional synchronization is needed.
    private string? cachedLastHash;

    public async Task AppendAsync(VoiceCloneAuditEntry entry, CancellationToken cancellationToken)
    {
        await appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetAuditPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Load the last hash from disk on the first append of this session; subsequent
            // appends use the in-memory cache so we avoid re-scanning the file every time.
            if (cachedLastHash is null)
            {
                cachedLastHash = await ReadLastEntryHashAsync(path, cancellationToken).ConfigureAwait(false);
            }

            var storedEntry = StoredVoiceCloneAuditEntry.From(entry, cachedLastHash);
            storedEntry = storedEntry with
            {
                EntryHash = ComputeHash(storedEntry)
            };

            await using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true);
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteLineAsync(JsonSerializer.Serialize(storedEntry, JsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);

            cachedLastHash = storedEntry.EntryHash;
        }
        finally
        {
            appendGate.Release();
        }
    }

    public async Task<VoiceCloneAuditVerificationResult> VerifyAsync(CancellationToken cancellationToken)
    {
        string path = GetAuditPath();
        if (!File.Exists(path))
        {
            return new VoiceCloneAuditVerificationResult(IsValid: true, EntryCount: 0);
        }

        string previousHash = string.Empty;
        int entryCount = 0;
        await foreach (StoredVoiceCloneAuditEntry storedEntry in ReadEntriesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            entryCount++;
            if (!string.Equals(storedEntry.PreviousHash, previousHash, StringComparison.Ordinal))
            {
                return new VoiceCloneAuditVerificationResult(
                    IsValid: false,
                    entryCount,
                    $"Entry {entryCount} previous hash does not match the prior entry.");
            }

            string expectedHash = ComputeHash(storedEntry);
            if (!string.Equals(storedEntry.EntryHash, expectedHash, StringComparison.Ordinal))
            {
                return new VoiceCloneAuditVerificationResult(
                    IsValid: false,
                    entryCount,
                    $"Entry {entryCount} hash does not match its contents.");
            }

            previousHash = storedEntry.EntryHash;
        }

        return new VoiceCloneAuditVerificationResult(IsValid: true, entryCount);
    }

    private string GetAuditPath() =>
        Path.Combine(workspaceContext.ProjectRootPath, ProjectArtifactPaths.VoiceCloneAuditRelativePath);

    private static async Task<string> ReadLastEntryHashAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        string lastHash = string.Empty;
        await foreach (StoredVoiceCloneAuditEntry entry in ReadEntriesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            lastHash = entry.EntryHash;
        }

        return lastHash;
    }

    private static async IAsyncEnumerable<StoredVoiceCloneAuditEntry> ReadEntriesAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return JsonSerializer.Deserialize<StoredVoiceCloneAuditEntry>(line, JsonOptions)
                         ?? throw new InvalidOperationException("Voice clone audit log contains an unreadable JSONL entry.");
        }
    }

    private static string ComputeHash(StoredVoiceCloneAuditEntry entry)
    {
        string canonical = string.Join(
            '\n',
            entry.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            entry.SessionId.ToString("D"),
            entry.SpeakerId.ToString("D"),
            entry.ReferenceClipArtifactId.ToString("D"),
            entry.PreviousHash);
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record StoredVoiceCloneAuditEntry(
        DateTimeOffset TimestampUtc,
        Guid SessionId,
        Guid SpeakerId,
        Guid ReferenceClipArtifactId,
        string PreviousHash,
        string EntryHash)
    {
        public static StoredVoiceCloneAuditEntry From(VoiceCloneAuditEntry entry, string previousHash) =>
            new(
                entry.TimestampUtc.ToUniversalTime(),
                entry.SessionId,
                entry.SpeakerId,
                entry.ReferenceClipArtifactId,
                previousHash,
                string.Empty);
    }
}
