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
        if(gameObject == null) return false;

        // save current pose
        var savingJson = ActorPoseGetFromJsonIPC?.InvokeFunc(gameObject);
        if(savingJson == null) return false;
        LastPoseSaved = savingJson;

        // apply pose
        return ActorPoseLoadFromFileIPC?.InvokeFunc(gameObject, path) ?? false;
    }
    private IGameObject? GetTargetGameObject()
    {
        if (_targetManager.GPoseTarget != null && _targetManager.GPoseTarget.ObjectKind == ObjectKind.Player) {
            var obj = _targetManager.GPoseTarget;
            PoseBrowser.Log.Debug($"object found: {obj.Name}");
            return obj;

        }
        return null;

    }


    private string? LastPoseSaved = null;
    public bool UndoTarget()
    {
        // verify if there is any pose to restore
        if(LastPoseSaved == null) return false;

        var gameObject = GetTargetGameObject();
        if(gameObject == null) return false;

        if (_configurationService.Configuration.IPC.SaveAndResporePoseInsteadOfReset) {
            return ActorPoseLoadFromJsonIPC?.InvokeFunc(gameObject, LastPoseSaved, false) ?? false;
        }
        return ActorPoseResetIPC?.InvokeFunc(gameObject, false) ?? false;

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
