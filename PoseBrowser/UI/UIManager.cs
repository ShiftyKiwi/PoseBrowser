
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Numerics;
using PoseBrowser.Config;
using PoseBrowser.IPC;
using PoseBrowser.UI.Windows;

namespace PoseBrowser.UI;

internal class UIManager : IDisposable
{
    private const string MainCommand = "/posebrowser";
    private const string AliasCommand = "/pb";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ConfigurationService _configurationService;
    private readonly ICommandManager _commandManager;

    private readonly MainWindow _mainWindow;
    private readonly SettingsWindow _settingsWindow;


    private readonly ITextureProvider _textureProvider;
    private readonly IFramework _framework;
    private readonly BrioService _brioService;
    private readonly WindowSystem _windowSystem;

    public readonly FileDialogManager FileDialogManager = new()
    {
        AddedWindowFlags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking
    };

    private readonly List<Window> _hiddenWindows = [];


    public static UIManager Instance { get; private set; } = null!;


    public UIManager
        (
            IDalamudPluginInterface pluginInterface,
            ConfigurationService configurationService,
            ICommandManager commandManager,
            IFramework framework,
            ITextureProvider textureProvider,
            MainWindow mainWindow,
            SettingsWindow settingsWindow,
            BrioService brioService
        )
    {
        Instance = this;

        _pluginInterface = pluginInterface;
        _configurationService = configurationService;
        _commandManager = commandManager;
        _textureProvider = textureProvider;

        _mainWindow = mainWindow;
        _settingsWindow = settingsWindow;

        _framework = framework;
        _brioService = brioService;

        _windowSystem = new(PoseBrowser.Name);
        _windowSystem.AddWindow(_settingsWindow);

        _configurationService.OnConfigurationChanged += ApplySettings;

        _pluginInterface.UiBuilder.DisableGposeUiHide = true;
        _pluginInterface.UiBuilder.Draw += DrawUI;
        _pluginInterface.UiBuilder.OpenConfigUi += ShowSettingsWindow;
        _pluginInterface.UiBuilder.OpenMainUi += ShowMainWindow;
        _pluginInterface.ActivePluginsChanged += ActivePluginsChanged;
        var openCommandInfo = new CommandInfo(OnMainCommand)
        {
            HelpMessage = "Open the PoseBrowser main window.",
            ShowInHelp = true
        };
        _commandManager.AddHandler(MainCommand, openCommandInfo);
        _commandManager.AddHandler(AliasCommand, openCommandInfo);

        ApplySettings();
    }



    public void ShowSettingsWindow()
    {
        _settingsWindow.IsOpen = true;
    }

    public void ShowMainWindow()
    {
        _mainWindow.Open(new Vector2(120f, 120f));
        PoseBrowser.Log.Info("PoseBrowser main window opened at forced position");
    }

    private void OnMainCommand(string command, string arguments)
    {
        ShowMainWindow();
    }

    private void ActivePluginsChanged(IActivePluginsChangedEventArgs args)
    {
        _brioService.RefreshBrioStatus();
    }
    

    public void ToggleMainWindow() => _mainWindow.IsOpen = !_mainWindow.IsOpen;
    public void ToggleSettingsWindow() => _settingsWindow.IsOpen = !_settingsWindow.IsOpen;




    private void ApplySettings()
    {

    }

    private void DrawUI()
    {
        try
        {
            _mainWindow.DrawStandalone();
            _windowSystem.Draw();
            FileDialogManager.Draw();
        }
        finally
        {
        }
    }

    public void TemporarilyHideAllOpenWindows()
    {
        foreach(var window in _windowSystem.Windows)
        {
            if(window.IsOpen == true)
            {
                _hiddenWindows.Add(window);
                window.IsOpen = false;
            }
        }
    }

    public void ReopenAllTemporarilyHiddenWindows()
    {
        foreach (var window in _hiddenWindows)
        {
            window.IsOpen = true;
        }
        _hiddenWindows.Clear();
    }

    public void Dispose()
    {
        _configurationService.OnConfigurationChanged -= ApplySettings;
        _pluginInterface.ActivePluginsChanged -= ActivePluginsChanged;
        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenConfigUi -= ShowSettingsWindow;
        _pluginInterface.UiBuilder.OpenMainUi -= ShowMainWindow;
        _commandManager.RemoveHandler(MainCommand);
        _commandManager.RemoveHandler(AliasCommand);

        _mainWindow.Dispose();

        _windowSystem.RemoveAllWindows();

        Instance = null!;
    }

    public IDalamudTextureWrap LoadImage(byte[] data)
    {
        var imgTask = _textureProvider.CreateFromImageAsync(data);
        imgTask.Wait(); // TODO: Don't block
        var img = imgTask.Result;
        return img;
    }
}
