using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

public enum UiWorkResult
{
    Done,
    Empty,
    Cancel
}

/// <summary>
/// Mixkit UI SFX (Assets/Sounds). One-shots + looping work bed for long ops.
/// </summary>
internal static class UiSound
{
    private static readonly object Gate = new();
    private static MediaPlayer? _player;
    private static MediaPlayer? _loopPlayer;
    private static bool _enabled = true;
    private static double _volume = 0.55;
    private static bool _playing;
    private static bool _workActive;
    private static DateTime _lastPlayUtc = DateTime.MinValue;
    private static string? _dir;
    private static bool _welcomePlayed;

    private static string PathOpen => Resolve("ui_open.wav");
    private static string PathClick => Resolve("ui_click.wav");
    private static string PathSave => Resolve("ui_save.wav");
    private static string PathLaunch => Resolve("ui_launch.wav");
    private static string PathWork => Resolve("ui_work_loop.wav");
    private static string PathDone => Resolve("ui_done.wav");
    private static string PathEmpty => Resolve("ui_empty.wav");

    public static bool IsWorking
    {
        get { lock (Gate) return _workActive; }
    }

    public static void ApplyFromConfig(UiPreferences? ui)
    {
        ui ??= new UiPreferences();
        _enabled = ui.UiSoundsEnabled;
        double t = Math.Clamp(ui.UiSoundVolume / 100.0, 0, 1);
        _volume = t * 0.62;
        lock (Gate)
        {
            if (_loopPlayer != null && _workActive)
                _loopPlayer.Volume = LoopVolume();
        }
    }

    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
            EndWork(UiWorkResult.Cancel);
    }

    public static void PlayWelcome(int delayMs = 280)
    {
        if (_welcomePlayed) return;
        _welcomePlayed = true;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            Open();
            return;
        }
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(0, delayMs)) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Open();
        };
        t.Start();
    }

    public static void Click()
    {
        if (IsWorking) return;
        Play(PathClick, minGapMs: 160, interrupt: false);
    }

    public static void Open() => Play(PathOpen, minGapMs: 800, interrupt: true);
    public static void Save() => Play(PathSave, minGapMs: 400, interrupt: true);
    public static void Launch() => Play(PathLaunch, minGapMs: 500, interrupt: true);
    public static void Done() => Play(PathDone, minGapMs: 400, interrupt: true);
    public static void Empty() => Play(PathEmpty, minGapMs: 400, interrupt: true);

    /// <summary>Soft looping bed until <see cref="EndWork"/> — for scan and other long ops.</summary>
    public static void BeginWork()
    {
        if (!_enabled || _volume < 0.03) return;
        var path = PathWork;
        if (!File.Exists(path)) return;

        void Go()
        {
            try
            {
                lock (Gate)
                {
                    if (_workActive) return;
                    SoftStopOneShot();
                    _loopPlayer ??= CreateLoopPlayer();
                    _workActive = true;
                    _loopPlayer.Volume = LoopVolume();
                    _loopPlayer.Open(new Uri(path, UriKind.Absolute));
                }
            }
            catch
            {
                lock (Gate) _workActive = false;
            }
        }

        RunOnUi(Go);
    }

    /// <summary>Stops the work loop, then plays done / empty (or silence on cancel).</summary>
    public static void EndWork(UiWorkResult result = UiWorkResult.Done)
    {
        void Go()
        {
            lock (Gate)
            {
                if (!_workActive && _loopPlayer == null)
                {
                    // still allow result chime if BeginWork never started
                }
                else
                {
                    _workActive = false;
                    try
                    {
                        if (_loopPlayer != null)
                        {
                            _loopPlayer.Volume = 0;
                            _loopPlayer.Stop();
                            _loopPlayer.Close();
                        }
                    }
                    catch { /* ignore */ }
                }
            }

            if (!_enabled) return;
            switch (result)
            {
                case UiWorkResult.Done:
                    Play(PathDone, minGapMs: 0, interrupt: true);
                    break;
                case UiWorkResult.Empty:
                    Play(PathEmpty, minGapMs: 0, interrupt: true);
                    break;
            }
        }

        RunOnUi(Go);
    }

    private static double LoopVolume() => Math.Clamp(_volume * 0.78, 0, 0.48);

    private static string Resolve(string file)
    {
        _dir ??= FindSoundsDir();
        return Path.Combine(_dir, file);
    }

    private static string FindSoundsDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var nextToExe = Path.Combine(baseDir, "Assets", "Sounds");
        if (File.Exists(Path.Combine(nextToExe, "ui_open.wav")))
            return nextToExe;

        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Assets", "Sounds");
            if (File.Exists(Path.Combine(candidate, "ui_open.wav")))
                return candidate;
        }

        return nextToExe;
    }

    private static void Play(string path, int minGapMs, bool interrupt)
    {
        if (!_enabled || _volume < 0.03) return;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        void Go()
        {
            try
            {
                lock (Gate)
                {
                    var now = DateTime.UtcNow;
                    if (minGapMs > 0 && (now - _lastPlayUtc).TotalMilliseconds < minGapMs)
                        return;
                    if (_playing && !interrupt)
                        return;

                    _player ??= CreateOneShotPlayer();
                    SoftStopOneShot();

                    _lastPlayUtc = now;
                    _playing = true;
                    _player.Volume = Math.Clamp(_volume, 0, 0.62);
                    _player.Open(new Uri(path, UriKind.Absolute));
                }
            }
            catch
            {
                _playing = false;
            }
        }

        RunOnUi(Go);
    }

    private static void SoftStopOneShot()
    {
        if (_player == null || !_playing) return;
        try
        {
            _player.Volume = 0;
            _player.Stop();
            _player.Close();
        }
        catch { /* ignore */ }
        _playing = false;
    }

    private static void RunOnUi(Action go)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            go();
        else
            dispatcher.BeginInvoke(go);
    }

    private static MediaPlayer CreateOneShotPlayer()
    {
        var p = new MediaPlayer();
        p.MediaOpened += (_, _) =>
        {
            try
            {
                lock (Gate)
                {
                    p.Volume = Math.Clamp(_volume, 0, 0.62);
                    p.Play();
                }
            }
            catch { _playing = false; }
        };
        p.MediaEnded += (_, _) =>
        {
            lock (Gate)
            {
                _playing = false;
                try { p.Stop(); p.Close(); } catch { }
            }
        };
        p.MediaFailed += (_, _) =>
        {
            lock (Gate) { _playing = false; }
        };
        return p;
    }

    private static MediaPlayer CreateLoopPlayer()
    {
        var p = new MediaPlayer();
        p.MediaOpened += (_, _) =>
        {
            try
            {
                lock (Gate)
                {
                    if (!_workActive) return;
                    p.Volume = LoopVolume();
                    p.Play();
                }
            }
            catch
            {
                lock (Gate) _workActive = false;
            }
        };
        p.MediaEnded += (_, _) =>
        {
            try
            {
                lock (Gate)
                {
                    if (!_workActive) return;
                    // Seamless-ish restart of the soft work bed
                    p.Position = TimeSpan.Zero;
                    p.Volume = LoopVolume();
                    p.Play();
                }
            }
            catch
            {
                lock (Gate) _workActive = false;
            }
        };
        p.MediaFailed += (_, _) =>
        {
            lock (Gate) _workActive = false;
        };
        return p;
    }
}
