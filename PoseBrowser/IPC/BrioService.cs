using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private static readonly string[] FaceBonePrefixes = ["j_f_"];
    private static readonly string[] FaceAnchorBoneNames = ["j_kao", "j_ago"];

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
    public bool ImportPoseTarget(string path, bool includeBody = true, bool includeFace = true)
    {
        var gameObject = GetTargetGameObject();
        if(gameObject == null)
        {
            PoseBrowser.Log.Warning("Pose import failed: no valid target found for Brio pose application.");
            return false;
        }

        // Brio API 3 no longer guarantees this legacy IPC exists.
        // Failing to snapshot the current pose should not block applying a new pose.
        string? currentPoseJson = null;
        try
        {
            currentPoseJson = InvokePoseGetAsJson(gameObject);
            if(!string.IsNullOrEmpty(currentPoseJson))
            {
                LastPoseSaved = currentPoseJson;
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

        bool loaded;
        if (includeBody && includeFace)
        {
            loaded = InvokePoseLoadFromFile(gameObject, path);
        }
        else
        {
            loaded = ImportFilteredPoseTarget(gameObject, path, includeBody, includeFace, currentPoseJson);
        }

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

    private bool ImportFilteredPoseTarget(IGameObject gameObject, string path, bool includeBody, bool includeFace, string? currentPoseJson)
    {
        if (!includeBody && !includeFace)
        {
            PoseBrowser.Log.Warning($"Pose import skipped for '{path}': neither body nor face import was requested.");
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            var filteredJson = FilterPoseJson(json, includeBody, includeFace, currentPoseJson);
            if (filteredJson == null)
            {
                PoseBrowser.Log.Warning($"Pose import failed: could not filter pose file '{path}'.");
                return false;
            }

            var isLegacyCmToolPose = string.Equals(Path.GetExtension(path), ".cmp", StringComparison.OrdinalIgnoreCase);
            return InvokePoseLoadFromJson(gameObject, filteredJson, isLegacyCmToolPose);
        }
        catch (Exception ex)
        {
            PoseBrowser.Log.Warning(ex, $"Pose import failed while filtering '{path}'.");
            return false;
        }
    }

    private static string? FilterPoseJson(string json, bool includeBody, bool includeFace, string? currentPoseJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }

        if (root is not JsonObject rootObject)
            return null;

        JsonObject? currentRootObject = null;
        JsonObject? currentBones = null;
        if (!string.IsNullOrWhiteSpace(currentPoseJson))
        {
            try
            {
                currentRootObject = JsonNode.Parse(currentPoseJson) as JsonObject;
                currentBones = currentRootObject?["Bones"] as JsonObject;
            }
            catch
            {
                currentRootObject = null;
                currentBones = null;
            }
        }

        if (!includeBody)
        {
            if (currentRootObject != null)
            {
                CopyRootTransform(rootObject, currentRootObject);
            }
            else
            {
                rootObject.Remove("Position");
                rootObject.Remove("Rotation");
                rootObject.Remove("Scale");
            }
        }

        if (rootObject["Bones"] is JsonObject bones)
        {
            foreach (var boneName in bones.Select(kvp => kvp.Key).ToList())
            {
                var isFaceBone = IsFaceBone(boneName);
                if ((isFaceBone && !includeFace) || (!isFaceBone && !includeBody))
                {
                    bones.Remove(boneName);
                    continue;
                }

                if (includeFace && !includeBody && FaceAnchorBoneNames.Contains(boneName, StringComparer.OrdinalIgnoreCase) && bones[boneName] is JsonObject anchorBone)
                {
                    if (currentBones?[boneName] is JsonObject currentAnchorBone)
                    {
                        CopyBoneProperty(anchorBone, currentAnchorBone, "Position");
                    }
                    else
                    {
                        anchorBone.Remove("Position");
                    }
                }
            }

            if (includeFace && !includeBody && currentBones != null)
            {
                foreach (var anchorBoneName in FaceAnchorBoneNames)
                {
                    if (bones[anchorBoneName] is not JsonObject sourceAnchorBone || currentBones[anchorBoneName] is not JsonObject currentAnchorBone)
                        continue;

                    CopyBoneProperty(sourceAnchorBone, currentAnchorBone, "Position");
                }
            }
        }

        return rootObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void CopyRootTransform(JsonObject destination, JsonObject source)
    {
        CopyObjectProperty(destination, source, "Position");
        CopyObjectProperty(destination, source, "Rotation");
        CopyObjectProperty(destination, source, "Scale");
    }

    private static void CopyBoneProperty(JsonObject destinationBone, JsonObject sourceBone, string propertyName)
    {
        CopyObjectProperty(destinationBone, sourceBone, propertyName);
    }

    private static void CopyObjectProperty(JsonObject destination, JsonObject source, string propertyName)
    {
        if (source[propertyName] != null)
        {
            destination[propertyName] = source[propertyName]!.DeepClone();
        }
        else
        {
            destination.Remove(propertyName);
        }
    }

    private static bool IsFaceBone(string boneName)
    {
        if (FaceAnchorBoneNames.Contains(boneName, StringComparer.OrdinalIgnoreCase))
            return true;

        return FaceBonePrefixes.Any(prefix => boneName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
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
                var resetFallback = InvokePoseReset(gameObject, false);
                if(!resetFallback)
                    PoseBrowser.Log.Warning($"Undo fallback failed: Brio could not reset pose for {gameObject.Name}.");
                return resetFallback;
            }

            var restored = InvokePoseLoadFromJson(gameObject, LastPoseSaved, false);
            if(!restored)
                PoseBrowser.Log.Warning($"Undo failed: Brio could not restore saved pose for {gameObject.Name}.");
            return restored;
        }
        var reset = InvokePoseReset(gameObject, false);
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

    private string? InvokePoseGetAsJson(IGameObject gameObject)
    {
        try
        {
            var apiResult = InvokeBrioApi<string?>("Brio.API.GetPoseAsJson", "Invoke", gameObject);
            return apiResult.invoked ? apiResult.result : ActorPoseGetFromJsonIPC?.InvokeFunc(gameObject);
        }
        catch
        {
            return ActorPoseGetFromJsonIPC?.InvokeFunc(gameObject);
        }
    }

    private bool InvokePoseLoadFromFile(IGameObject gameObject, string path)
    {
        try
        {
            var apiResult = InvokeBrioApi<bool>("Brio.API.LoadPoseFromFile", "Invoke", gameObject, path);
            return apiResult.invoked ? apiResult.result : (ActorPoseLoadFromFileIPC?.InvokeFunc(gameObject, path) ?? false);
        }
        catch
        {
            return ActorPoseLoadFromFileIPC?.InvokeFunc(gameObject, path) ?? false;
        }
    }

    private bool InvokePoseLoadFromJson(IGameObject gameObject, string json, bool isLegacyCmToolPose)
    {
        try
        {
            var apiResult = InvokeBrioApi<bool>("Brio.API.LoadPoseFromJson", "Invoke", gameObject, json, isLegacyCmToolPose);
            return apiResult.invoked
                ? apiResult.result
                : (ActorPoseLoadFromJsonIPC?.InvokeFunc(gameObject, json, isLegacyCmToolPose) ?? false);
        }
        catch
        {
            return ActorPoseLoadFromJsonIPC?.InvokeFunc(gameObject, json, isLegacyCmToolPose) ?? false;
        }
    }

    private bool InvokePoseReset(IGameObject gameObject, bool clearHistory)
    {
        try
        {
            var apiResult = InvokeBrioApi<bool>("Brio.API.ResetPose", "Invoke", gameObject, clearHistory);
            return apiResult.invoked ? apiResult.result : (ActorPoseResetIPC?.InvokeFunc(gameObject, clearHistory) ?? false);
        }
        catch
        {
            return ActorPoseResetIPC?.InvokeFunc(gameObject, clearHistory) ?? false;
        }
    }

    private (bool invoked, T result) InvokeBrioApi<T>(string typeName, string methodName, params object[] args)
    {
        try
        {
            var assembly = ResolveBrioApiAssembly();
            if(assembly == null)
                return (false, default!);

            var type = assembly.GetType(typeName);
            if(type == null)
                return (false, default!);

            var instance = Activator.CreateInstance(type, _pluginInterface);
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            if(instance == null || method == null)
                return (false, default!);

            var value = method.Invoke(instance, args);
            return value is T typed ? (true, typed) : (true, default!);
        }
        catch(Exception ex)
        {
            PoseBrowser.Log.Debug(ex, $"Failed reflective Brio API call {typeName}.{methodName}");
            return (false, default!);
        }
    }

    private static Assembly? ResolveBrioApiAssembly()
    {
        var loadedAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "Brio.API", StringComparison.OrdinalIgnoreCase));
        if(loadedAssembly != null)
            return loadedAssembly;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var brioPluginDir = Path.Combine(appData, "XIVLauncher", "installedPlugins", "Brio");
        if(!Directory.Exists(brioPluginDir))
            return null;

        var latestBrioDir = new DirectoryInfo(brioPluginDir)
            .EnumerateDirectories()
            .OrderByDescending(dir => dir.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if(latestBrioDir == null)
            return null;

        var apiPath = Path.Combine(latestBrioDir.FullName, "Brio.API.dll");
        return File.Exists(apiPath) ? Assembly.LoadFrom(apiPath) : null;
    }


}
