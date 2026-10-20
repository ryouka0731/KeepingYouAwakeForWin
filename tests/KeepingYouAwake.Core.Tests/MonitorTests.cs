namespace KeepingYouAwake.Core.Tests;

public class MonitorTests
{
    [Fact]
    public void PollingMonitorFiresOnTransitionsOnly()
    {
        var scheduler = new FakeScheduler();
        var value = true;
        var monitor = new PollingConditionMonitor(scheduler, TimeSpan.FromSeconds(1), () => value);
        var events = new List<bool>();
        monitor.BecameTrue += (_, _) => events.Add(true);
        monitor.BecameFalse += (_, _) => events.Add(false);

        monitor.Start();
        scheduler.Advance(TimeSpan.FromSeconds(3));
        value = false;
        scheduler.Advance(TimeSpan.FromSeconds(3));
        monitor.Stop();
        value = true;
        scheduler.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal([true, false], events);
        Assert.Equal(0, scheduler.ActiveTimers);
    }

    [Fact]
    public void PollingMonitorSurvivesProbeErrors()
    {
        var scheduler = new FakeScheduler();
        var monitor = new PollingConditionMonitor(scheduler, TimeSpan.FromSeconds(1), () => throw new IOException());
        monitor.Start();
        scheduler.Advance(TimeSpan.FromSeconds(2));
        Assert.False(monitor.State);
    }

    [Fact]
    public void DownloadMonitorRecognisesPartialFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kya-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "done.zip"), "x");
            Assert.False(DownloadActivityMonitor.DirectoryHasInProgressFile(dir));
            File.WriteAllText(Path.Combine(dir, "big.iso.PART"), "x");
            Assert.True(DownloadActivityMonitor.DirectoryHasInProgressFile(dir));
            Assert.False(DownloadActivityMonitor.DirectoryHasInProgressFile(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CpuMonitorIgnoresShortSpikes()
    {
        var scheduler = new FakeScheduler();
        var sampler = new FakeCpuSampler { BusyPercent = 95 };
        var monitor = new CpuLoadMonitor(scheduler, sampler) { ThresholdPercent = 50 };
        var crossed = 0;
        monitor.CrossedAbove += (_, _) => crossed++;
        monitor.Start();

        scheduler.Advance(TimeSpan.FromSeconds(20));
        sampler.BusyPercent = 5;
        scheduler.Advance(TimeSpan.FromSeconds(5));
        sampler.BusyPercent = 95;
        scheduler.Advance(TimeSpan.FromSeconds(25));
        Assert.Equal(0, crossed);
        scheduler.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, crossed);
        Assert.True(monitor.CurrentBusyPercent > 90);
    }
}

public class DriveAliveTimerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kya-drive-" + Guid.NewGuid().ToString("N"));

    public DriveAliveTimerTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "system"));
        Directory.CreateDirectory(Path.Combine(root, "external"));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string ExternalPing => Path.Combine(root, "external", DriveAliveTimer.PingFileName);

    [Fact]
    public void WritesAndRemovesPingFiles()
    {
        var scheduler = new FakeScheduler();
        var drives = new FakeDriveEnumerator();
        drives.Roots.Add(Path.Combine(root, "external"));
        var timer = new DriveAliveTimer(scheduler, drives, Path.Combine(root, "system"), runInline: true);

        timer.Start();
        Assert.StartsWith(DriveAliveTimer.ContentPrefix, File.ReadAllText(ExternalPing));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "system")));

        timer.Stop();
        Assert.False(File.Exists(ExternalPing));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "system")));
    }

    [Fact]
    public void NeverTouchesSomeoneElsesFile()
    {
        File.WriteAllText(ExternalPing, "user data");
        var drives = new FakeDriveEnumerator();
        drives.Roots.Add(Path.Combine(root, "external"));
        var timer = new DriveAliveTimer(new FakeScheduler(), drives, Path.Combine(root, "system"), runInline: true);

        timer.Start();
        timer.Stop();

        Assert.Equal("user data", File.ReadAllText(ExternalPing));
    }

    [Fact]
    public void ReusesEmptyLeftoverAndSkipsDirectories()
    {
        File.WriteAllText(ExternalPing, string.Empty);
        Assert.True(DriveAliveTimer.WritePing(ExternalPing, "keepingyouawake x\n"u8.ToArray(), hidden: false));

        var dirPath = Path.Combine(root, "dir-ping");
        Directory.CreateDirectory(dirPath);
        Assert.False(DriveAliveTimer.WritePing(dirPath, "keepingyouawake x\n"u8.ToArray(), hidden: false));
    }

    [Fact]
    public void SystemOnlyModeSkipsExternalDrives()
    {
        var timer = new DriveAliveTimer(new FakeScheduler(), null, Path.Combine(root, "system"), runInline: true);
        timer.Start();
        Assert.False(File.Exists(ExternalPing));
        Assert.False(timer.IncludesExternalDrives);
        timer.Stop();
    }
}
