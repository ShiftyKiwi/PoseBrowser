using System;
using System.Linq;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using PoseBrowser.Config;

namespace PoseBrowser.IPC;

internal class BrioService : IDisposable
{
    private const int MinimumSupportedBrioApiMajor = 2;

    public bool IsBrioAvailable { get; private set; } = false;
    public (int Major, int Minor) LastDetectedApiVersion { get; private set; } = default;
    public string StatusMessage { get; private set; } = "Brio status not checked yet.";
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ConfigurationService _configurationService;
    private readonly ITargetManager _targetManager;

    public const string ApiVersionIpcName = "Brio.ApiVersion";
    private readonly ICallGateSubscriber<(int, int)>? ApiVersionIpc;

    public const string ActorPoseLoadFromFileIPCName = "Brio.Actor.Pose.LoadFromFile";
    private readonly ICallGateSubscriber<IGameObject, string, bool>? ActorPoseLoadFromFileIPC;

    public const string ActorPoseGetAsJsonIPCName = "Brio.Actor.Pose.GetPoseAsJson";
    private readonly ICallGateSubscriber<IGameObject, string?>? ActorPoseGetFromJsonIPC;

    public const string ActorPoseLoadFromJsonIPCName = "Brio.Actor.Pose.LoadFromJson";
    private readonly ICallGateSubscriber<IGameObject, string, bool, bool>? ActorPoseLoadFromJsonIPC;

    public const string ActorPoseResetIPCName = "Brio.Actor.Pose.Reset";
    private readonly ICallGateSubscriber<IGameObject, bool, bool>? ActorPoseResetIPC;

    public BrioService(IDalamudPluginInterface pluginInterface, ConfigurationService configurationService, ITargetManager targetManager)
    {
        _pluginInterface = pluginInterface;
        _configurationService = configurationService;
        _targetManager = targetManager;

        ApiVersionIpc = pluginInterface.GetIpcSubscriber<(int,int)>(ApiVersionIpcName);
        ActorPoseLoadFromFileIPC = pluginInterface.GetIpcSubscriber<IGameObject, string, bool>(ActorPoseLoadFromFileIPCName);
        ActorPoseGetFromJsonIPC = pluginInterface.GetIpcSubscriber<IGameObject, string?>(ActorPoseGetAsJsonIPCName);
        ActorPoseLoadFromJsonIPC = pluginInterface.GetIpcSubscriber<IGameObject, string, bool, bool>(ActorPoseLoadFromJsonIPCName);
        ActorPoseResetIPC = pluginInterface.GetIpcSubscriber<IGameObject, bool, bool>(ActorPoseResetIPCName);
        RefreshBrioStatus();

        _configurationService.OnConfigurationChanged += RefreshBrioStatus;
    }

    public (int, int) ApiVersion()
    {
        try
        {
            return ApiVersionIpc?.InvokeFunc() ?? default;
        }
        catch(Exception ex)
        {
            PoseBrowser.Log.Debug(ex, "Failed to query Brio API version");
            return default;
        }
    }
    public bool ImportPoseTarget(string path)
    {
        var gameObject = GetTargetGameObject();
        if(gameObject == null)
        {
            PoseBrowser.Log.Warning("Pose import failed: no valid target found for Brio pose application.");
            return false;
        }

        // Brio API 3 no longer guarantees this legacy IPC exists.
        // Failing to snapshot the current pose should not block applying a new pose.
        try
        {
            var savingJson = ActorPoseGetFromJsonIPC?.InvokeFunc(gameObject);
            if(!string.IsNullOrEmpty(savingJson))
            {
                LastPoseSaved = savingJson;
            }
            else
            {
                LastPoseSaved = null;
                PoseBrowser.Log.Warning($"Brio did not return current pose JSON for target {gameObject.Name}. Apply will continue, but undo may be limited.");
            }
        }
        catch(Exception ex)
        {
            LastPoseSaved = null;
            PoseBrowser.Log.Warning(ex, $"Brio pose snapshot IPC is unavailable for target {gameObject.Name}. Apply will continue without saved undo state.");
        }

        // apply pose
        var loaded = ActorPoseLoadFromFileIPC?.InvokeFunc(gameObject, path) ?? false;
        if(!loaded)
        {
            PoseBrowser.Log.Warning($"Pose import failed: Brio rejected file '{path}' for target {gameObject.Name}.");
        }
        else
        {
            PoseBrowser.Log.Info($"Applied pose '{path}' to target {gameObject.Name}.");
        }

        return loaded;
    }
    private IGameObject? GetTargetGameObject()
    {
        var candidates = new IGameObject?[]
        {
            _targetManager.GPoseTarget,
            _targetManager.Target,
            _targetManager.FocusTarget,
        };

        foreach (var candidate in candidates)
        {
            if (candidate == null)
                continue;

            PoseBrowser.Log.Debug($"Evaluating pose target candidate: {candidate.Name} ({candidate.ObjectKind})");
            if (candidate.ObjectKind == ObjectKind.Player)
            {
                return candidate;
            }
        }

        PoseBrowser.Log.Warning("No player target found in GPoseTarget, Target, or FocusTarget.");
        return null;
    }


    private string? LastPoseSaved = null;
    public bool UndoTarget()
    {
        // verify if there is any pose to restore
        if(LastPoseSaved == null)
        {
            PoseBrowser.Log.Warning("Undo failed: no previously saved pose exists.");
            return false;
        }

        var gameObject = GetTargetGameObject();
        if(gameObject == null)
        {
            PoseBrowser.Log.Warning("Undo failed: no valid target found.");
            return false;
        }

        if (_configurationService.Configuration.IPC.SaveAndResporePoseInsteadOfReset) {
            if(LastPoseSaved == null)
            {
                PoseBrowser.Log.Warning("Undo requested, but no saved pose snapshot is available. Falling back to reset.");
                var resetFallback = ActorPoseResetIPC?.InvokeFunc(gameObject, false) ?? false;
                if(!resetFallback)
                    PoseBrowser.Log.Warning($"Undo fallback failed: Brio could not reset pose for {gameObject.Name}.");
                return resetFallback;
            }

            var restored = ActorPoseLoadFromJsonIPC?.InvokeFunc(gameObject, LastPoseSaved, false) ?? false;
            if(!restored)
                PoseBrowser.Log.Warning($"Undo failed: Brio could not restore saved pose for {gameObject.Name}.");
            return restored;
        }
        var reset = ActorPoseResetIPC?.InvokeFunc(gameObject, false) ?? false;
        if(!reset)
            PoseBrowser.Log.Warning($"Undo failed: Brio could not reset pose for {gameObject.Name}.");
        return reset;

    }


    public void RefreshBrioStatus()
    {
        if(_configurationService.Configuration.IPC.AllowBrioIntegration)
        {
            IsBrioAvailable = ConnectToBrio();
        }
        else
        {
            LastDetectedApiVersion = default;
            IsBrioAvailable = false;
            StatusMessage = "Brio integration disabled in PoseBrowser settings.";
        }
    }

    private bool ConnectToBrio()
    {
        try
        {
            bool brioInstalled = _pluginInterface.InstalledPlugins.Any(x => x.Name == "Brio" && x.IsLoaded);

            if(!brioInstalled)
            {
                LastDetectedApiVersion = default;
                StatusMessage = "Brio is not installed or not currently loaded.";
                PoseBrowser.Log.Debug("Brio not present");
                return false;
            }

            var apiVersion = ApiVersion();
            LastDetectedApiVersion = apiVersion;
            if(apiVersion.Item1 < MinimumSupportedBrioApiMajor)
            {
                StatusMessage = $"Detected Brio API {apiVersion}, but PoseBrowser requires {MinimumSupportedBrioApiMajor}.x or newer.";
                PoseBrowser.Log.Warning($"Brio detected but API {apiVersion} is unsupported. Expected >= {MinimumSupportedBrioApiMajor}.x.");
                return false;
            }

            StatusMessage = $"Connected to Brio API {apiVersion}.";
            PoseBrowser.Log.Debug("Brio integration initialized");

            return true;
        }
        catch(Exception ex)
        {
            LastDetectedApiVersion = default;
            StatusMessage = "Failed to initialize Brio IPC. Check plugin log for details.";
            PoseBrowser.Log.Debug(ex, "Brio initialize error");
            return false;
        }
    }
    public void Dispose()
    {
        _configurationService.OnConfigurationChanged -= RefreshBrioStatus;
    }


}
