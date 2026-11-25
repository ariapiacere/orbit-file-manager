using OrbitFileManager.Services.FileSystem;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class DriveWatcherTests
{
    [Fact]
    public void ANewOrRemovedDriveLetterRaisesOnce()
    {
        string[] letters = [@"C:\", @"D:\"];
        using var watcher = new DriveWatcher(TimeSpan.FromHours(1), () => letters);
        int raised = 0;
        watcher.DrivesChanged += () => raised++;
        watcher.Start();

        Assert.False(watcher.CheckNow()); // drives present at start are not news
        letters = [@"C:\", @"D:\", @"E:\"];
        Assert.True(watcher.CheckNow());
        Assert.False(watcher.CheckNow()); // same change is reported once
        letters = [@"C:\", @"D:\"];
        Assert.True(watcher.CheckNow());
        Assert.Equal(2, raised);
    }

    [Fact]
    public void OrderAndCaseOfLettersDoNotMatter()
    {
        string[] letters = [@"C:\", @"e:\", @"D:\"];
        using var watcher = new DriveWatcher(TimeSpan.FromHours(1), () => letters);
        watcher.Start();
        letters = [@"D:\", @"E:\", @"C:\"];
        Assert.False(watcher.CheckNow());
    }

    [Fact]
    public async Task DeviceMessagesRaiseOnceAfterTheyStop()
    {
        string[] letters = [@"C:\"];
        using var watcher = new DriveWatcher(TimeSpan.FromHours(1), () => letters);
        int raised = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.DrivesChanged += () => { Interlocked.Increment(ref raised); first.TrySetResult(); };
        watcher.Start();

        // A card goes into a reader that already has a letter: Windows sends a burst of messages, letters unchanged.
        for (int i = 0; i < 5; i++)
            watcher.NotifyDeviceChange();

        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(DriveWatcher.DeviceSettleDelay + TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task APollAfterADeviceMessageDoesNotReportTheSameDriveAgain()
    {
        string[] letters = [@"C:\"];
        using var watcher = new DriveWatcher(TimeSpan.FromHours(1), () => letters);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.DrivesChanged += () => raised.TrySetResult();
        watcher.Start();

        letters = [@"C:\", @"F:\"];
        watcher.NotifyDeviceChange();
        await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(watcher.CheckNow());
    }

    [Fact]
    public void AFailingReadIsNoChange()
    {
        bool fail = false;
        using var watcher = new DriveWatcher(TimeSpan.FromHours(1), () => fail ? throw new IOException("no") : [@"C:\"]);
        watcher.Start();
        fail = true;
        Assert.False(watcher.CheckNow());
    }
}
