using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CookieSheet.Refunds;
using CookieSheetServer.Data;
using CookieSheetServer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CookieSheetServer.Services;

public sealed record BackupInfo(Guid Id, DateTimeOffset CreatedAt, int Users, int Orders, int Refunds);
public sealed record RestoreBackupRequest(Guid BackupId);

public sealed class DataBackupService(string directory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "latest.json");

    public sealed record Snapshot(int Version, Guid Id, DateTimeOffset CreatedAt, string Database,
        List<user> Users, List<order> Orders, List<RefundRecord> Refunds)
    {
        public BackupInfo Info => new(Id, CreatedAt, Users.Count, Orders.Count, Refunds.Count);
    }
    private sealed record Envelope(byte[] Payload, string Sha256);

    public async Task<BackupInfo?> GetLatestAsync()
    {
        await _gate.WaitAsync();
        try { return File.Exists(_path) ? (await ReadAsync()).Info : null; }
        finally { _gate.Release(); }
    }

    public async Task<BackupInfo> CreateAsync(AppDbContext db)
    {
        await _gate.WaitAsync();
        var temporary = _path + ".tmp";
        try
        {
            // All tables are read from one PostgreSQL snapshot, including concurrent imports.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            var snapshot = new Snapshot(1, Guid.NewGuid(), DateTimeOffset.UtcNow,
                db.Database.GetDbConnection().Database,
                await db.user.AsNoTracking().ToListAsync(),
                await db.order.AsNoTracking().ToListAsync(),
                await db.refunds.AsNoTracking().ToListAsync());
            await transaction.CommitAsync();
            Validate(snapshot);
            var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            var envelope = new Envelope(payload, Convert.ToHexString(SHA256.HashData(payload)));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, envelope);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory replacement: a failed write leaves the previous backup intact.
            File.Move(temporary, _path, overwrite: true);
            return snapshot.Info;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }

    public async Task<BackupInfo> RestoreAsync(AppDbContext db, Guid expectedId)
    {
        await _gate.WaitAsync();
        try
        {
            var snapshot = await ReadAsync();
            if (snapshot.Id != expectedId)
                throw new InvalidOperationException("最后一份备份已变化，请重新确认后恢复。");
            if (snapshot.Database != db.Database.GetDbConnection().Database)
                throw new InvalidOperationException("备份不属于当前数据库，无法恢复。");

            await using var transaction = await db.Database.BeginTransactionAsync();
            // Prevent imports/deletions from interleaving with replacement. Readers see a committed state.
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"user\", \"order\", refunds IN EXCLUSIVE MODE");
            await db.refunds.ExecuteDeleteAsync();
            await db.order.ExecuteDeleteAsync();
            await db.user.ExecuteDeleteAsync();
            db.ChangeTracker.Clear();
            db.user.AddRange(snapshot.Users);
            db.order.AddRange(snapshot.Orders);
            db.refunds.AddRange(snapshot.Refunds);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""
                SELECT setval(pg_get_serial_sequence('"user"', 'userId'),
                    GREATEST(COALESCE((SELECT MAX("userId")::bigint FROM "user"), 0) + 1, 1), false)
                """);
            await transaction.CommitAsync();
            return snapshot.Info;
        }
        finally { _gate.Release(); }
    }

    private async Task<Snapshot> ReadAsync()
    {
        if (!File.Exists(_path)) throw new InvalidOperationException("暂无备份，请先手动备份数据。");
        try
        {
            await using var stream = File.OpenRead(_path);
            var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream);
            if (envelope?.Payload == null || envelope.Sha256 != Convert.ToHexString(SHA256.HashData(envelope.Payload)))
                throw new InvalidOperationException("备份文件校验失败，当前数据未恢复。");
            var snapshot = JsonSerializer.Deserialize<Snapshot>(envelope.Payload);
            Validate(snapshot);
            return snapshot!;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("备份文件无法读取，当前数据未恢复。");
        }
    }

    private static void Validate(Snapshot? snapshot)
    {
        if (snapshot is not { Version: 1, Users: not null, Orders: not null, Refunds: not null }
            || snapshot.Id == Guid.Empty || snapshot.CreatedAt == default
            || snapshot.Users.Any(u => u == null) || snapshot.Orders.Any(o => o == null)
            || snapshot.Refunds.Any(r => r == null))
            throw new InvalidOperationException("备份内容不完整或版本不兼容。");
        var users = snapshot.Users.Select(u => u.userId).ToHashSet();
        if (users.Count != snapshot.Users.Count
            || snapshot.Orders.Select(o => o.orderId).Distinct().Count() != snapshot.Orders.Count
            || snapshot.Refunds.Select(r => r.refundId).Distinct().Count() != snapshot.Refunds.Count
            || snapshot.Orders.Any(o => string.IsNullOrWhiteSpace(o.orderId) || !users.Contains(o.userId))
            || snapshot.Refunds.Any(r => string.IsNullOrWhiteSpace(r.refundId) || r.status != "退款成功"))
            throw new InvalidOperationException("备份记录校验失败。");
    }
}
