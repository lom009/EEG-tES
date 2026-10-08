using System;
using System.IO;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class SqliteMigrationLockRecoveryTests
{
    [Fact]
    public async Task RecoverAsync_RemovesStaleMigrationLockTableWithoutTouchingApplicationTables()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"eggtcs-migration-lock-{Guid.NewGuid():N}.db"
        );
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using var db = new AppDbContext(options);
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TABLE \"ApplicationData\" (\"Id\" INTEGER PRIMARY KEY);"
            );
            await db.Database.ExecuteSqlRawAsync(
                $"CREATE TABLE \"{SqliteMigrationLockRecovery.LockTableName}\" "
                    + "(\"Id\" INTEGER NOT NULL CONSTRAINT \"PK___EFMigrationsLock\" PRIMARY KEY, "
                    + "\"Timestamp\" TEXT NOT NULL);"
            );

            await SqliteMigrationLockRecovery.RecoverAsync(db);

            Assert.False(await TableExistsAsync(db, SqliteMigrationLockRecovery.LockTableName));
            Assert.True(await TableExistsAsync(db, "ApplicationData"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task RecoverAsync_SucceedsWhenMigrationLockTableDoesNotExist()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.OpenConnectionAsync();

        await SqliteMigrationLockRecovery.RecoverAsync(db);

        Assert.False(await TableExistsAsync(db, SqliteMigrationLockRecovery.LockTableName));
    }

    private static async Task<bool> TableExistsAsync(AppDbContext db, string tableName)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }
}
