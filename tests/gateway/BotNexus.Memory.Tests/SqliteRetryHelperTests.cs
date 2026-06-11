using Microsoft.Data.Sqlite;

namespace BotNexus.Memory.Tests;

public sealed class SqliteRetryHelperTests
{
    [Fact]
    public async Task ExecuteWithRetryAsync_SucceedsOnFirstAttempt_ReturnsResult()
    {
        var callCount = 0;
        var result = await SqliteRetryHelper.ExecuteWithRetryAsync(async _ =>
        {
            callCount++;
            await Task.CompletedTask;
            return 42;
        }, CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_TransientFailureThenSuccess_Retries()
    {
        var callCount = 0;
        var result = await SqliteRetryHelper.ExecuteWithRetryAsync(async _ =>
        {
            callCount++;
            if (callCount == 1)
                throw CreateTransientException(5); // SQLITE_BUSY
            await Task.CompletedTask;
            return "ok";
        }, CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_AllAttemptsTransient_ThrowsAfterMaxAttempts()
    {
        var callCount = 0;
        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await SqliteRetryHelper.ExecuteWithRetryAsync<int>(async _ =>
            {
                callCount++;
                await Task.CompletedTask;
                throw CreateTransientException(5);
            }, CancellationToken.None, maxAttempts: 3);
        });

        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_NonTransientException_DoesNotRetry()
    {
        var callCount = 0;
        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await SqliteRetryHelper.ExecuteWithRetryAsync<int>(async _ =>
            {
                callCount++;
                await Task.CompletedTask;
                throw CreateTransientException(11); // SQLITE_CORRUPT — not transient
            }, CancellationToken.None);
        });

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_CancellationRespected_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await SqliteRetryHelper.ExecuteWithRetryAsync(async token =>
            {
                await Task.Delay(1000, token);
                return 0;
            }, cts.Token);
        });
    }

    [Theory]
    [InlineData(5, true)]   // SQLITE_BUSY
    [InlineData(6, true)]   // SQLITE_LOCKED
    [InlineData(10, true)]  // SQLITE_IOERR
    [InlineData(1, false)]  // SQLITE_ERROR
    [InlineData(11, false)] // SQLITE_CORRUPT
    [InlineData(14, false)] // SQLITE_CANTOPEN
    public void IsTransient_ClassifiesErrorCodesCorrectly(int errorCode, bool expected)
    {
        var ex = CreateTransientException(errorCode);
        Assert.Equal(expected, SqliteRetryHelper.IsTransient(ex));
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_TwoTransientThenSuccess_RetriesMultipleTimes()
    {
        var callCount = 0;
        var result = await SqliteRetryHelper.ExecuteWithRetryAsync(async _ =>
        {
            callCount++;
            if (callCount <= 2)
                throw CreateTransientException(6); // SQLITE_LOCKED
            await Task.CompletedTask;
            return "recovered";
        }, CancellationToken.None);

        Assert.Equal("recovered", result);
        Assert.Equal(3, callCount);
    }

    private static SqliteException CreateTransientException(int errorCode)
    {
        // SqliteException requires the error code via constructor
        // Use reflection or just create a real one via invalid operation
        try
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var cmd = connection.CreateCommand();
            // This won't produce arbitrary error codes easily, so use the constructor
            throw new SqliteException($"Test error code {errorCode}", errorCode);
        }
        catch (SqliteException)
        {
            // Rethrow won't work for arbitrary codes; use the direct constructor
        }

        return new SqliteException($"Test error code {errorCode}", errorCode);
    }
}
