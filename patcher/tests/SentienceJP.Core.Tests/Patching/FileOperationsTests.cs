using SentienceJP.Core.Patching;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests.Patching;

/// <summary>
/// 適用・復元で使うファイル操作（置き換え・削除・属性の保持・共有違反の再試行）のテスト。
/// </summary>
public sealed class FileOperationsTests : IDisposable
{
    /// <summary>再試行しない設定（失敗の確認を速くするため）。</summary>
    private static readonly FileOperations NoRetry = new(SharingViolationRetry.None);

    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(FileAttributes.Archive)]
    [InlineData(FileAttributes.ReadOnly | FileAttributes.Archive)]
    [InlineData(FileAttributes.ReadOnly | FileAttributes.Hidden)]
    [InlineData(FileAttributes.Normal)]
    public void ReplaceFile_KeepsOriginalAttributes_AndLeavesNoTemporaryFile(FileAttributes attributes)
    {
        // C-02: 読み取り専用でも置き換えられ、置き換え後のファイルは元の属性のまま
        var path = _temp.Combine("plugins.js");
        File.WriteAllText(path, "old");
        File.SetAttributes(path, attributes);
        var expected = File.GetAttributes(path) & FileOperations.PreservedAttributes;

        NoRetry.ReplaceFile(path, "new"u8.ToArray());

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal(expected, File.GetAttributes(path) & FileOperations.PreservedAttributes);
        Assert.False(File.Exists(FileOperations.TemporaryPathFor(path)));
    }

    [Fact]
    public void ReplaceFile_MissingTarget_CreatesFile()
    {
        var path = _temp.Combine("state.json");

        NoRetry.ReplaceFile(path, "{}"u8.ToArray());

        Assert.Equal("{}", File.ReadAllText(path));
        Assert.False(File.Exists(FileOperations.TemporaryPathFor(path)));
    }

    [Fact]
    public void ReplaceFile_LeftoverReadOnlyTemporaryFile_IsOverwritten()
    {
        // C-06: 前回の中断で残った一時ファイル（読み取り専用でも）は、次の置き換えの前に片付ける
        var path = _temp.Combine("plugins.js");
        File.WriteAllText(path, "old");
        var temporary = FileOperations.TemporaryPathFor(path);
        File.WriteAllText(temporary, "partial");
        File.SetAttributes(temporary, FileAttributes.ReadOnly);

        NoRetry.ReplaceFile(path, "new"u8.ToArray());

        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void ReplaceFile_TargetLockedByOtherProgram_FailsAndKeepsOriginal()
    {
        // 置き換えに失敗しても、元のファイルの内容と属性（外した読み取り専用属性）が元どおりで、一時ファイルも残らない
        var path = _temp.Combine("plugins.js");
        File.WriteAllText(path, "old");
        File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Archive);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => NoRetry.ReplaceFile(path, "new"u8.ToArray()));
        }

        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Archive, File.GetAttributes(path) & FileOperations.PreservedAttributes);
        Assert.False(File.Exists(FileOperations.TemporaryPathFor(path)));
    }

    [Fact]
    public void ReplaceFile_TargetBrieflyOpenedByAnotherProgram_SucceedsWithDefaultRetry()
    {
        // CA-03: 置き換え先を他のプログラム（ウイルス対策ソフトのスキャン等）が 150ms ほど開いていても、
        // アクセス拒否になる置き換えの段を既定の待ち時間で再試行して成功する
        var path = _temp.Combine("plugins.js");
        File.WriteAllText(path, "old");
        using var opened = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            opened.Set();
            Thread.Sleep(150);
        });
        holder.Start();
        opened.Wait();

        FileOperations.Default.ReplaceFile(path, "new"u8.ToArray());
        holder.Join();

        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(FileOperations.TemporaryPathFor(path)));
    }

    [Fact]
    public void ReplaceFile_TargetKeptOpenByAnotherProgram_FailsWithAccessDeniedAfterRetries()
    {
        // CA-03: ずっと開かれたままなら、待ち時間の後に従来どおり失敗し、元の内容・属性のまま一時ファイルも残らない
        var path = _temp.Combine("plugins.js");
        File.WriteAllText(path, "old");
        File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Archive);
        var retry = new FileOperations(new SharingViolationRetry([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)]));

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.Throws<UnauthorizedAccessException>(() => retry.ReplaceFile(path, "new"u8.ToArray()));
        }

        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Archive, File.GetAttributes(path) & FileOperations.PreservedAttributes);
        Assert.False(File.Exists(FileOperations.TemporaryPathFor(path)));
    }

    [Fact]
    public void RetryingAccessDenied_RetriesUnauthorizedAccessUntilSuccess_ButPlainRunDoesNot()
    {
        // CA-03: アクセス拒否の再試行は置き換えの段（RunRetryingAccessDenied）に限り、通常の Run では従来どおり再試行しない
        var retry = new SharingViolationRetry([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        var attempts = 0;

        retry.RunRetryingAccessDenied(() =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new UnauthorizedAccessException("拒否");
            }
        });

        Assert.Equal(3, attempts);
        var plainAttempts = 0;
        Assert.Throws<UnauthorizedAccessException>(() => retry.Run(() =>
        {
            plainAttempts++;
            throw new UnauthorizedAccessException("拒否");
        }));
        Assert.Equal(1, plainAttempts);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), false, true)]
    [InlineData(unchecked((int)0x80070070), false, false)]
    [InlineData(unchecked((int)0x80070070), true, false)]
    public void ShouldRetry_IOException_OnlySharingViolations(int hresult, bool alsoRetryAccessDenied, bool expected)
    {
        Assert.Equal(expected, SharingViolationRetry.ShouldRetry(new IOException("x", hresult), alsoRetryAccessDenied));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ShouldRetry_AccessDenied_OnlyWhenRequested(bool alsoRetryAccessDenied, bool expected)
    {
        Assert.Equal(expected, SharingViolationRetry.ShouldRetry(new UnauthorizedAccessException("拒否"), alsoRetryAccessDenied));
        Assert.False(SharingViolationRetry.ShouldRetry(new InvalidOperationException("その他"), alsoRetryAccessDenied));
    }

    [Fact]
    public void WriteNewFile_WritesContent_AndRefusesToOverwrite()
    {
        var path = _temp.Combine("a.json");

        NoRetry.WriteNewFile(path, "1"u8.ToArray());

        Assert.Equal("1", File.ReadAllText(path));
        Assert.Throws<IOException>(() => NoRetry.WriteNewFile(path, "2"u8.ToArray()));
        Assert.Equal("1", File.ReadAllText(path));
    }

    [Fact]
    public void DeleteFile_ReadOnlyOrMissing_Succeeds()
    {
        var path = _temp.Combine("a.json");
        File.WriteAllText(path, "x");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        NoRetry.DeleteFile(path);
        NoRetry.DeleteFile(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DeleteDirectoryIfEmpty_ReadOnlyEmptyFolder_IsDeleted_ButNonEmptyIsKept()
    {
        // C-02: 読み取り専用属性の付いた空フォルダも削除できる（E8）。中身があれば削除しない。
        var empty = _temp.Combine("empty");
        Directory.CreateDirectory(empty);
        new DirectoryInfo(empty).Attributes |= FileAttributes.ReadOnly;
        var nonEmpty = _temp.Combine("nonempty");
        FakeGame.WriteText(nonEmpty, "keep.txt", "keep");

        Assert.True(NoRetry.DeleteDirectoryIfEmpty(empty));
        Assert.False(NoRetry.DeleteDirectoryIfEmpty(nonEmpty));
        Assert.False(NoRetry.DeleteDirectoryIfEmpty(_temp.Combine("missing")));

        Assert.False(Directory.Exists(empty));
        Assert.True(File.Exists(Path.Combine(nonEmpty, "keep.txt")));
    }

    [Fact]
    public void DeleteDirectoryRecursive_ReadOnlyFilesAndFolders_AreDeleted()
    {
        var root = _temp.Combine("backup");
        FakeGame.WriteText(root, "original/www/js/plugins.js", "x");
        FakeGame.WriteText(root, "state.json", "{}");
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.ReadOnly);
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root))
        {
            new DirectoryInfo(directory).Attributes |= FileAttributes.ReadOnly;
        }

        NoRetry.DeleteDirectoryRecursive(root);

        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true)]
    [InlineData(unchecked((int)0x80070021), true)]
    [InlineData(unchecked((int)0x80070005), false)]
    [InlineData(unchecked((int)0x80070050), false)]
    [InlineData(32, false)]
    public void IsSharingViolation_OnlyForCodes32And33(int hresult, bool expected)
    {
        Assert.Equal(expected, SharingViolationRetry.IsSharingViolation(hresult));
    }

    [Fact]
    public void Retry_SharingViolation_RetriesUntilSuccess()
    {
        // C-13: 一時的な共有違反（ERROR_SHARING_VIOLATION）は、決められた回数まで待って再試行する
        var retry = new SharingViolationRetry([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        var attempts = 0;

        var result = retry.Run(() =>
        {
            attempts++;
            return attempts < 3 ? throw new IOException("使用中", unchecked((int)0x80070020)) : "ok";
        });

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void Retry_GivesUpAfterConfiguredAttempts()
    {
        var retry = new SharingViolationRetry([TimeSpan.Zero, TimeSpan.Zero]);
        var attempts = 0;

        var ex = Assert.Throws<IOException>(() => retry.Run(() =>
        {
            attempts++;
            throw new IOException("ロック違反", unchecked((int)0x80070021));
        }));

        Assert.Equal("ロック違反", ex.Message);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void Retry_OtherErrors_AreNotRetried()
    {
        var retry = new SharingViolationRetry([TimeSpan.Zero, TimeSpan.Zero]);
        var attempts = 0;

        Assert.Throws<IOException>(() => retry.Run(() =>
        {
            attempts++;
            throw new IOException("ディスクがいっぱいです", unchecked((int)0x80070070));
        }));
        Assert.Throws<UnauthorizedAccessException>(() => retry.Run<bool>(() => throw new UnauthorizedAccessException("拒否")));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Read_WhileBrieflyLockedByOtherProgram_SucceedsByRetrying()
    {
        // ウイルス対策ソフト等が一瞬だけファイルを排他で開いている状況（実際の共有違反）: 再試行で読み取れる。
        // 負荷の高い環境でも安定するよう、待ち時間に余裕を持たせた再試行の方針で確かめる。
        var retrying = new FileOperations(new SharingViolationRetry(Enumerable.Repeat(TimeSpan.FromMilliseconds(100), 30).ToList()));
        var path = _temp.Combine("plugins.js");
        File.WriteAllText(path, "content");
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            using var release = new Timer(_ => holder.Dispose(), null, TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);

            var bytes = retrying.ReadAllBytes(path);

            Assert.Equal("content", System.Text.Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            holder.Dispose();
        }
    }

    [Fact]
    public void PreservedPart_KeepsOnlyPreservedAttributes()
    {
        Assert.Equal(FileAttributes.Normal, FileOperations.PreservedPart(FileAttributes.Directory));
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Hidden, FileOperations.PreservedPart(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Compressed));
    }
}
