using System.IO;
using Visor.Models;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class ActivityHistoryStoreTests
{
    [Fact]
    public void Add_persists_after_Flush_and_Recent_returns_newest_first()
    {
        using var dir = new TempDir();
        using var store = new ActivityHistoryStore(dir.Path);
        store.Add("profileApplied", profileId: "a", profileName: "Alpha");
        store.Add("sessionRestored", profileName: "Alpha");

        var recent = store.Recent(10);
        Assert.Equal(2, recent.Count);
        Assert.Equal("sessionRestored", recent[0].Kind);
        Assert.Equal("profileApplied", recent[1].Kind);

        store.Flush();
        using var reloaded = new ActivityHistoryStore(dir.Path);
        Assert.Equal(2, reloaded.Snapshot().Count);
        Assert.Equal("Alpha", reloaded.Recent(1)[0].ProfileName);
    }

    [Fact]
    public void Add_is_debounced_and_written_off_the_caller()
    {
        using var dir = new TempDir();
        using var store = new ActivityHistoryStore(dir.Path);
        var file = Path.Combine(dir.Path, ActivityHistoryStore.FileName);
        store.Add("paused");
        Assert.False(File.Exists(file)); // nothing written synchronously

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(file) && DateTime.UtcNow < deadline)
            Thread.Sleep(25);
        Assert.True(File.Exists(file));
        using var reloaded = new ActivityHistoryStore(dir.Path);
        Assert.Single(reloaded.Snapshot());
    }

    [Fact]
    public void Dispose_flushes_pending_entries()
    {
        using var dir = new TempDir();
        var store = new ActivityHistoryStore(dir.Path);
        store.Add("emergency");
        store.Dispose();
        Assert.True(File.Exists(Path.Combine(dir.Path, ActivityHistoryStore.FileName)));
        using var reloaded = new ActivityHistoryStore(dir.Path);
        Assert.Equal("emergency", Assert.Single(reloaded.Snapshot()).Kind);
    }

    [Fact]
    public void Add_caps_at_MaxEntries_across_kinds()
    {
        using var dir = new TempDir();
        using var store = new ActivityHistoryStore(dir.Path);
        string[] kinds = { "k0", "k1", "k2", "k3", "k4" };
        for (int i = 0; i < ActivityHistoryStore.MaxEntries + 25; i++)
            store.Add(kinds[i % kinds.Length], profileName: "G" + i);

        Assert.Equal(ActivityHistoryStore.MaxEntries, store.Snapshot().Count);
        Assert.Equal("G" + (ActivityHistoryStore.MaxEntries + 24), store.Recent(1)[0].ProfileName);
    }

    [Fact]
    public void Hotkey_spam_of_one_kind_cannot_evict_other_kinds()
    {
        using var dir = new TempDir();
        using var store = new ActivityHistoryStore(dir.Path);
        store.Add("profileApplied", profileName: "Game");
        store.Add("sessionRestored", profileName: "Game");
        for (int i = 0; i < 200; i++)
            store.Add("presetApplied", profileName: "P" + i);

        var snap = store.Snapshot();
        Assert.Equal(ActivityHistoryStore.MaxEntriesPerKind, snap.Count(e => e.Kind == "presetApplied"));
        Assert.Contains(snap, e => e.Kind == "profileApplied");
        Assert.Contains(snap, e => e.Kind == "sessionRestored");
        Assert.Equal("P199", store.Recent(1)[0].ProfileName);
    }

    [Fact]
    public void Trim_keeps_newest_of_the_capped_kind()
    {
        var list = new List<ActivityHistoryEntry>();
        for (int i = 0; i < ActivityHistoryStore.MaxEntriesPerKind + 5; i++)
            list.Add(new ActivityHistoryEntry { Kind = "presetApplied", ProfileName = "P" + i });
        ActivityHistoryStore.Trim(list);
        Assert.Equal(ActivityHistoryStore.MaxEntriesPerKind, list.Count);
        Assert.Equal("P5", list[0].ProfileName);
    }

    [Fact]
    public void Load_recovers_from_corrupt_file_and_keeps_bak()
    {
        using var dir = new TempDir();
        dir.Write(ActivityHistoryStore.FileName, "{ not json");
        using var store = new ActivityHistoryStore(dir.Path);
        Assert.Empty(store.Snapshot());
        var bak = Path.Combine(dir.Path, ActivityHistoryStore.FileName + ActivityHistoryStore.BackupSuffix);
        Assert.True(File.Exists(bak));
        Assert.Equal("{ not json", File.ReadAllText(bak));
        store.Add("paused");
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public void Flush_leaves_no_tmp_file_after_replace()
    {
        using var dir = new TempDir();
        using var store = new ActivityHistoryStore(dir.Path);
        store.Add("paused");
        store.Flush();
        store.Add("resumed");
        store.Flush();
        Assert.False(File.Exists(Path.Combine(dir.Path, ActivityHistoryStore.FileName + ".tmp")));
        Assert.True(File.Exists(Path.Combine(dir.Path, ActivityHistoryStore.FileName)));
        using var reloaded = new ActivityHistoryStore(dir.Path);
        Assert.Equal(2, reloaded.Snapshot().Count);
    }
}
