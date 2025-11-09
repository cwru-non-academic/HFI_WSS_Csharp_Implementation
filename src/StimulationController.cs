using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HFI.Wss;

/// <summary>
/// Host-agnostic wrapper around the full stimulation stack (core → params → model).
/// Provides the same API surface as the Unity <c>Stimulation</c> MonoBehaviour but manages
/// initialization, ticking, and shutdown inside a plain .NET application.
/// </summary>
public sealed class StimulationController : IAsyncDisposable, IDisposable
{
    private readonly StimulationOptions _options;
    private readonly object _gate = new();

    private IModelParamsCore? _wss;
    private IBasicStimulation? _basicWss;
    private bool _basicSupported;
    private CancellationTokenSource? _tickCts;
    private Task? _tickTask;

    /// <summary>True after <see cref="StartStimulation"/> succeeds.</summary>
    public bool started { get; private set; }

    /// <summary>True if the underlying core exposes basic-stimulation APIs.</summary>
    public bool BasicSupported => _basicSupported;

    public StimulationController(StimulationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        Directory.CreateDirectory(_options.ConfigPath);
    }

    /// <summary>
    /// Builds the full stimulation stack and initializes the hardware connection.
    /// Automatically starts the background tick loop.
    /// </summary>
    public void Initialize()
    {
        lock (_gate)
        {
            if (_wss != null) return;

            IStimulationCore core = !string.IsNullOrWhiteSpace(_options.SerialPort)
                ? new WssStimulationCore(_options.SerialPort!, _options.ConfigPath, _options.TestMode, _options.MaxSetupTries)
                : new WssStimulationCore(_options.ConfigPath, _options.TestMode, _options.MaxSetupTries);

            IStimParamsCore paramsLayer = new StimParamsLayer(core, _options.ConfigPath);
            var modelLayer = new ModelParamsLayer(paramsLayer, _options.ConfigPath);

            _wss = modelLayer;
            _wss.TryGetBasic(out _basicWss);
            _basicSupported = _basicWss != null;

            _wss.Initialize();
            EnsureTickLoop();
        }
    }

    /// <summary>Stops ticking and tears down the connection.</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            StopTickLoop();
            if (_wss != null)
            {
                try { _wss.Shutdown(); } catch (Exception ex) { Log.Error(ex, "Error during Shutdown"); }
                try { _wss.Dispose(); } catch (Exception ex) { Log.Error(ex, "Error disposing stimulation core"); }
            }

            _wss = null;
            _basicWss = null;
            _basicSupported = false;
            started = false;
        }
    }

    /// <summary>Explicitly releases the radio connection.</summary>
    public void releaseRadio() => Shutdown();

    /// <summary>Performs a radio reset by shutting down and re-initializing the connection.</summary>
    public void resetRadio()
    {
        lock (_gate)
        {
            if (_wss == null) return;
            StopTickLoop();
            _wss.Shutdown();
            _wss.Initialize();
            EnsureTickLoop();
        }
    }

    #region ==== Background ticking ====

    private void EnsureTickLoop()
    {
        if (_wss == null) return;
        if (_tickTask != null && !_tickTask.IsCompleted) return;

        StopTickLoop();

        _tickCts = new CancellationTokenSource();
        var token = _tickCts.Token;
        int interval = Math.Max(1, _options.TickIntervalMs);

        _tickTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { _wss?.Tick(); }
                catch (Exception ex) { Log.Error(ex, "Tick loop failure"); }

                try { await Task.Delay(interval, token).ConfigureAwait(false); }
                catch (TaskCanceledException) { break; }
            }
        }, CancellationToken.None);
    }

    private void StopTickLoop()
    {
        var cts = _tickCts;
        var task = _tickTask;
        if (cts == null && task == null) return;

        _tickCts = null;
        _tickTask = null;

        try { cts?.Cancel(); } catch { }
        if (task != null)
        {
            try { task.Wait(); }
            catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is TaskCanceledException)) { }
            catch (TaskCanceledException) { }
        }
        cts?.Dispose();
    }

    #endregion

    #region ==== Stimulation methods: basic and core ====

    public void StimulateAnalog(string finger, int PW, int amp = 3, int IPI = 10)
    {
        var wss = EnsureWss();
        int channel = FingerToChannel(finger);
        wss.StimulateAnalog(channel, PW, amp, IPI);
    }

    public void StartStimulation()
    {
        var wss = EnsureWss();
        wss.StartStim(WssTarget.Broadcast);
        started = true;
    }

    public void StopStimulation()
    {
        var wss = EnsureWss();
        wss.StopStim(WssTarget.Broadcast);
        started = false;
    }

    public void Save(int targetWSS)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.Save(IntToWssTarget(targetWSS));
    }

    public void Save()
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.Save(WssTarget.Broadcast);
    }

    public void load(int targetWSS)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.Load(IntToWssTarget(targetWSS));
    }

    public void load()
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.Load(WssTarget.Broadcast);
    }

    public void request_Configs(int targetWSS, int command, int id)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.Request_Configs(command, id, IntToWssTarget(targetWSS));
    }

    public void updateWaveform(int[] waveform, int eventID)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.UpdateWaveform(waveform, eventID, WssTarget.Broadcast);
    }

    public void updateWaveform(int targetWSS, int[] waveform, int eventID)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.UpdateWaveform(waveform, eventID, IntToWssTarget(targetWSS));
    }

    public void updateWaveform(int cathodicWaveform, int anodicWaveform, int eventID)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.UpdateEventShape(cathodicWaveform, anodicWaveform, eventID, WssTarget.Broadcast);
    }

    public void updateWaveform(int targetWSS, int cathodicWaveform, int anodicWaveform, int eventID)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.UpdateEventShape(cathodicWaveform, anodicWaveform, eventID, IntToWssTarget(targetWSS));
    }

    public void updateWaveform(WaveformBuilder waveform, int eventID)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.UpdateWaveform(waveform, eventID, WssTarget.Broadcast);
    }

    public void updateWaveform(int targetWSS, WaveformBuilder waveform, int eventID)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.UpdateWaveform(waveform, eventID, IntToWssTarget(targetWSS));
    }

    public void loadWaveform(string fileName, int eventID)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.LoadWaveform(fileName, eventID);
    }

    public void WaveformSetup(WaveformBuilder wave, int eventID)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.WaveformSetup(wave, eventID, WssTarget.Broadcast);
    }

    public void WaveformSetup(int targetWSS, WaveformBuilder wave, int eventID)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.WaveformSetup(wave, eventID, IntToWssTarget(targetWSS));
    }

    public void UpdateIPD(int ipd, int eventID)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.UpdateIPD(ipd, eventID, WssTarget.Broadcast);
    }

    public void UpdateIPD(int targetWSS, int ipd, int eventID)
    {
        if (!TryGetBasic(out var basic)) { Log.Error("Basic stimulation not supported."); return; }
        basic.UpdateIPD(ipd, eventID, IntToWssTarget(targetWSS));
    }

    #endregion

    #region ==== Stimulation methods: params and model layers ====

    public void StimulateNormalized(string finger, float magnitude)
    {
        var wss = EnsureWss();
        int ch = FingerToChannel(finger);
        wss.StimulateNormalized(ch, magnitude);
    }

    public int GetStimIntensity(string finger)
    {
        var wss = EnsureWss();
        int ch = FingerToChannel(finger);
        return (int)wss.GetStimIntensity(ch);
    }

    public void SaveParamsJson() => EnsureWss().SaveParamsJson();
    public void LoadParamsJson() => EnsureWss().LoadParamsJson();
    public void LoadParamsJson(string pathOrDir) => EnsureWss().LoadParamsJson(pathOrDir);
    public void AddOrUpdateStimParam(string key, float value) => EnsureWss().AddOrUpdateStimParam(key, value);
    public float GetStimParam(string key) => EnsureWss().GetStimParam(key);
    public bool TryGetStimParam(string key, out float v) => EnsureWss().TryGetStimParam(key, out v);
    public Dictionary<string, float> GetAllStimParams() => EnsureWss().GetAllStimParams();

    public void SetChannelAmp(string finger, float mA)
    {
        var wss = EnsureWss();
        int ch = FingerToChannel(finger);
        wss.SetChannelAmp(ch, mA);
    }

    public void SetChannelPWMin(string finger, int us)
    {
        var wss = EnsureWss();
        int ch = FingerToChannel(finger);
        wss.SetChannelPWMin(ch, us);
    }

    public void SetChannelPWMax(string finger, int us)
    {
        var wss = EnsureWss();
        int ch = FingerToChannel(finger);
        wss.SetChannelPWMax(ch, us);
    }

    public void SetChannelIPI(string finger, int ms)
    {
        var wss = EnsureWss();
        int ch = FingerToChannel(finger);
        wss.SetChannelIPI(ch, ms);
    }

    public float GetChannelAmp(string finger)
    {
        var wss = EnsureWss();
        int ch = FingerToChannel(finger);
        return wss.GetChannelAmp(ch);
    }

    public int GetChannelPWMin(string finger)
    {
        var wss = EnsureWss();
        int ch = FingerToChannel(finger);
        return wss.GetChannelPWMin(ch);
    }

    public int GetChannelPWMax(string finger)
    {
        var wss = EnsureWss();
        int ch = FingerToChannel(finger);
        return wss.GetChannelPWMax(ch);
    }

    public int GetChannelIPI(string finger)
    {
        var wss = EnsureWss();
        int ch = FingerToChannel(finger);
        return wss.GetChannelIPI(ch);
    }

    public bool IsFingerValid(string finger)
    {
        var wss = EnsureWss();
        int ch = FingerToChannel(finger);
        return wss.IsChannelInRange(ch);
    }

    public void StimWithMode(string finger, float magnitude)
    {
        var wss = EnsureWss();
        int ch = FingerToChannel(finger);
        wss.StimWithMode(ch, magnitude);
    }

    public void UpdateChannelParams(string finger, int max, int min, int amp)
    {
        var wss = EnsureWss();
        int ch = FingerToChannel(finger);
        if (!wss.IsChannelInRange(ch))
            throw new ArgumentOutOfRangeException(nameof(finger), $"Channel {ch} is not valid for current config.");

        string baseKey = $"stim.ch.{ch}";
        wss.AddOrUpdateStimParam($"{baseKey}.maxPW", max);
        wss.AddOrUpdateStimParam($"{baseKey}.minPW", min);
        wss.AddOrUpdateStimParam($"{baseKey}.amp", amp);
    }

    #endregion

    #region ==== Config and state ====

    public bool isModeValid() => EnsureWss().IsModeValid();
    public bool Ready() => _wss?.Ready() ?? false;
    public bool Started() => _wss?.Started() ?? false;
    public ModelConfigController GetModelConfigCTRL() => EnsureWss().GetModelConfigController();
    public CoreConfigController GetCoreConfigCTRL() => EnsureWss().GetCoreConfigController();
    public void LoadCoreConfigFile() => EnsureWss().LoadConfigFile();

    #endregion

    #region ==== Utility ====

    private IModelParamsCore EnsureWss()
    {
        if (_wss == null)
            throw new InvalidOperationException("Call Initialize() before using the stimulation controller.");
        return _wss;
    }

    private bool TryGetBasic(out IBasicStimulation basic)
    {
        basic = _basicWss!;
        return _basicSupported && basic != null;
    }

    private static WssTarget IntToWssTarget(int i) =>
        i switch
        {
            0 => WssTarget.Broadcast,
            1 => WssTarget.Wss1,
            2 => WssTarget.Wss2,
            3 => WssTarget.Wss3,
            _ => WssTarget.Wss1
        };

    private static int FingerToChannel(string fingerOrAlias)
    {
        if (string.IsNullOrWhiteSpace(fingerOrAlias)) return 0;

        if (fingerOrAlias.StartsWith("ch", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(fingerOrAlias.AsSpan(2), out var n))
            return n;

        return fingerOrAlias.ToLowerInvariant() switch
        {
            "thumb" => 1,
            "index" => 2,
            "middle" => 3,
            "ring" => 4,
            "pinky" or "little" => 5,
            _ => 0
        };
    }

    #endregion

    public void Dispose()
    {
        Shutdown();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Shutdown();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Configuration record for <see cref="StimulationController"/>.</summary>
public sealed class StimulationOptions
{
    private string _configPath = Path.Combine(Environment.CurrentDirectory, "Config");

    /// <summary>Optional serial device name (e.g., "COM3" or "/dev/ttyUSB0"). Uses auto-detect when null.</summary>
    public string? SerialPort { get; init; }

    /// <summary>Enables simulated mode without real hardware communication.</summary>
    public bool TestMode { get; init; }

    /// <summary>Maximum number of setup retries before failing initialization.</summary>
    public int MaxSetupTries { get; init; } = 5;

    /// <summary>Directory that holds the JSON configs used by the WSS stack.</summary>
    public string ConfigPath
    {
        get => _configPath;
        init
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Config path cannot be empty.", nameof(ConfigPath));
            _configPath = value;
        }
    }

    /// <summary>Delay in milliseconds between background tick invocations.</summary>
    public int TickIntervalMs { get; init; } = 10;

    internal void Validate()
    {
        if (TickIntervalMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(TickIntervalMs), "Tick interval must be positive.");

        Directory.CreateDirectory(ConfigPath);
    }

    /// <summary>
    /// Creates default options pointing at the executable directory.
    /// </summary>
    public static StimulationOptions CreateDefault() => new();
}
