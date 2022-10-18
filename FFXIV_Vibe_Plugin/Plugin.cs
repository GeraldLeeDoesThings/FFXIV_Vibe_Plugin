﻿using System;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;

#region Dalamud deps
using Dalamud.IoC;
using Dalamud.Data;
using Dalamud.Plugin;
using Dalamud.Game;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Network;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
#endregion

#region FFXIV_Vibe_Plugin deps
using FFXIV_Vibe_Plugin.Commons;
using FFXIV_Vibe_Plugin.Triggers;
using FFXIV_Vibe_Plugin.Hooks;
using FFXIV_Vibe_Plugin.Experimental;
#endregion

namespace FFXIV_Vibe_Plugin {

  public sealed class Plugin : IDalamudPlugin {
    [PluginService]
    [RequiredVersion("1.0")]
    private Dalamud.Game.Gui.ChatGui? DalamudChat { get; init; }
    private DalamudPluginInterface PluginInterface { get; init; }
    private CommandManager CommandManager { get; init; }
    private Configuration Configuration { get; init; }
    private PluginUI PluginUi { get; init; }
    private GameNetwork GameNetwork { get; init; }
    private DataManager DataManager { get; init; }
    private ClientState ClientState { get; init; }

    // Plugin definition
    public string Name => "FFXIV Vibe Plugin";
    public static readonly string ShortName = "FVP";
    public readonly string commandName = "/fvp";

    // Custom variables from Kacie
    private bool _firstUpdated = false;
    private readonly Logger Logger;
    private readonly ActionEffect hook_ActionEffect;
    private readonly PlayerStats PlayerStats;
    private readonly Device.Controller DeviceController;
    private readonly Triggers.Controller TriggersController;
    private string AuthorizedUser = "";
    private SortedSet<Triggers.ChatTrigger> ChatTriggers = new();

    // Experiments
    private readonly NetworkCapture experiment_networkCapture;

    // Chat types
    private readonly XivChatType[] allowedChatTypes = {
      XivChatType.Say, XivChatType.Party,
      XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
      XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
      XivChatType.FreeCompany, XivChatType.CrossParty,
      XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2,
      XivChatType.CrossLinkShell3, XivChatType.CrossLinkShell4,
      XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6,
      XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
      XivChatType.StandardEmote, XivChatType.CustomEmote
    };

    public Plugin(
        [RequiredVersion("1.0")] DalamudPluginInterface pluginInterface,
        [RequiredVersion("1.0")] CommandManager commandManager,
        [RequiredVersion("1.0")] ClientState clientState,
        [RequiredVersion("1.0")] GameNetwork gameNetwork,
        [RequiredVersion("1.0")] SigScanner scanner,
        [RequiredVersion("1.0")] ObjectTable gameObjects,
        [RequiredVersion("1.0")] DataManager dataManager
        ) {

      // Init Plugin
      this.PluginInterface = pluginInterface;
      this.CommandManager = commandManager;
      this.GameNetwork = gameNetwork;
      this.ClientState = clientState;
      this.DataManager = dataManager;
      this.Configuration = this.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
      this.Configuration.Initialize(this.PluginInterface);
      this.CommandManager.AddHandler(commandName, new CommandInfo(OnCommand) {
        HelpMessage = "A vibe plugin for fun..."
      });
      if(DalamudChat != null) {
        DalamudChat.ChatMessage += CheckForTriggers_oldChat; 
      }

      // Initialize the logger
      this.Logger = new Logger(this.DalamudChat, ShortName, Logger.LogLevel.VERBOSE);

      // Initialize player stats monitoring.
      this.PlayerStats = new PlayerStats(this.ClientState);
      PlayerStats.Event_CurrentHpChanged += this.Player_currentHPChanged;
      PlayerStats.Event_MaxHpChanged += this.Player_currentHPChanged;

      // Initialize the devices Controller
      this.DeviceController = new Device.Controller(this.Logger, this.Configuration);
      if(this.Configuration.AUTO_CONNECT) {
        Task.Delay(2000);
        this.Command_DeviceController_Connect();
      }     

      // Initialize Hook ActionEffect
      this.hook_ActionEffect = new(this.DataManager, this.Logger, scanner, clientState, gameObjects);
      this.hook_ActionEffect.ReceivedEvent += SpellWasTriggered;

      // Triggers
      this.TriggersController = new Triggers.Controller(this.Logger);
      
      // Experimental
      this.experiment_networkCapture = new NetworkCapture(this.Logger, this.GameNetwork);
      
      // UI
      this.PluginUi = new PluginUI(this.Logger, this.PluginInterface, this.Configuration, this, this.DeviceController, this.TriggersController);
      this.PluginInterface.UiBuilder.Draw += DrawUI;
      this.PluginInterface.UiBuilder.OpenConfigUi += DisplayConfigUI;
    }

    public void Dispose() {
      this.Logger.Debug("Disposing plugin...");

      // Cleaning device controller.
      if(this.DeviceController != null) {
        this.DeviceController.Dispose();
      }

      // Cleaning chat triggers.
      this.CommandManager.RemoveHandler(commandName);
      if(DalamudChat != null) {
        DalamudChat.ChatMessage -= CheckForTriggers_oldChat;
      }

      // Cleaning hooks
      this.hook_ActionEffect.Dispose();

      // Cleaning experimentations
      this.experiment_networkCapture.Dispose();

      this.PluginUi.Dispose();
      this.Logger.Debug("Plugin disposed!");
    }

    private void DrawUI() {

      this.PluginUi.Draw();

      this.PlayerStats.Update();

      // TODO: this.RunSequencer(this.sequencerTasks);

      // Trigger first updated method
      if(!this._firstUpdated) {
        this.FirstUpdated();
        this._firstUpdated = true;
      }
    }

    private void FirstUpdated() {
      this.Logger.Debug("First updated");
      this.LoadTriggersConfig();
      if(this.Configuration.AUTO_OPEN) {
        this.DisplayUI();
      }
    }

    private void DisplayUI() {
      this.PluginUi.Visible = true;
    }

    private void DisplayConfigUI() {
      this.PluginUi.Visible = true;
    }


    public static string GetHelp(string command) {
      string helpMessage = $@"Usage:
      {command} config      
      {command} connect
      {command} disconnect
      {command} save [file path]
      {command} load [file path]

Chat features:
      {command} chat_list_triggers
      {command} chat_add <intensity 0-100> <trigger text>
      {command} chat_remove <id>
      {command} chat_user <authorized user>

Vibes:
      {command} send <0-100> # BROKEN
      {command} stop

Example:
       {command} connect
       {command} chat_add 0 shh
       {command} chat_add 20 slowly 
       {command} chat_add 75 getting there
       {command} chat_add 100 hey ;)
       {command} user Alice

These commands let anyone whose name contains 'Alice' control all your connected toys with the appropriate phrases, as long as those are uttered in a tell, a party, a (cross) linkshell, or a free company chat.
";
      return helpMessage;
    }

    private void OnCommand(string command, string args) {
      if(args.Length == 0) {
        this.DisplayUI();
      } else {
        if(args.StartsWith("help")) {
          this.Logger.Chat(GetHelp($"/{ShortName}"));
        } else if(args.StartsWith("config")) {
          this.DisplayConfigUI();
        } else if(args.StartsWith("connect")) {
          this.Command_DeviceController_Connect();
        } else if(args.StartsWith("disconnect")) {
          this.Command_DeviceController_Disconnect();
        } else if(args.StartsWith("chat_list_triggers")) {
          this.Command_ListTriggers();
        } else if(args.StartsWith("chat_add")) {
          this.Command_AddTrigger(args);
        } else if(args.StartsWith("chat_remove")) {
          this.Command_RemoveTrigger(args);
        } else if(args.StartsWith("chat_user")) {
          this.Command_SetAuthorizedUser(args);
        } else if(args.StartsWith("save")) {
          Command_SaveConfig(args);
        } else if(args.StartsWith("load")) {
          Command_LoadConfig(args);
        } else if(args.StartsWith("send")) {
          this.Command_SendIntensity(args);
        } else if(args.StartsWith("stop")) {
          this.DeviceController.SendVibeToAll(0);
        } else if(args.StartsWith("play_pattern")) {
          this.Play_pattern(args);
        } else if(args.StartsWith("exp_network_start")) {
          this.experiment_networkCapture.StartNetworkCapture();
        } else if(args.StartsWith("exp_network_stop")) {
          this.experiment_networkCapture.StopNetworkCapture();
        } else {
          this.Logger.Chat($"Unknown subcommand: {args}");
        }
      }
    }

    public void Command_DeviceController_Connect() {
      string host = this.Configuration.BUTTPLUG_SERVER_HOST;
      int port = this.Configuration.BUTTPLUG_SERVER_PORT;
      this.DeviceController.Connect(host, port);
    }

    private void Command_DeviceController_Disconnect() {
      this.DeviceController.Disconnect();
    }

    private void SpellWasTriggered(object? sender, HookActionEffects_ReceivedEventArgs args) {
      Structures.Spell spell = args.Spell;
      Triggers.Trigger? trigger = this.TriggersController.CheckTrigger_Spell(spell);
      if(trigger != null) {
        this.Logger.Log($"SPELL_TRIGGER {trigger.SpellText}");
        this.DeviceController.SendVibeToAll(0);
      }


    }

    public void CheckTriggers_Chat(string message) {
       Triggers.Trigger? trigger = this.TriggersController.CheckTrigger_Chat(message);
      if(trigger != null) {
        this.Logger.Log($"CHAT_TRIGGER:{trigger.ChatText}");
        this.DeviceController.SendVibeToAll(0);
      }

    }




    /** LEGACY CODE IS BELLOW */

    private void Play_pattern(string args) {
      try {
        string[] param = args.Split(" ", 2);
        string patternName = param[1];
        this.Logger.Chat($"Play pattern {patternName}");
        if(patternName == "shake") {
          this.DeviceController.Play_PatternShake(100);
        } else if(patternName == "mountain") {
          this.DeviceController.Play_PatternMountain(30);
        }
      } catch(Exception e) when(e is FormatException or IndexOutOfRangeException) {
        this.Logger.Error($"Malformed arguments for play_pattern [pattern_name] # shake, mountain", e);
        return;
      }
    }


    private void CheckForTriggers_oldChat(XivChatType type, uint senderId, ref SeString _sender, ref SeString _message, ref bool isHandled) {
      
      string sender = _sender.ToString();
      if(!allowedChatTypes.Any(ct => ct == type) || (AuthorizedUser.Length > 0 && !sender.ToString().Contains(AuthorizedUser))) {
        return;
      }
      this.CheckTriggers_Chat(_message.TextValue); // NEW
      /* DEPRECATED
      string message = _message.ToString().ToLower();
      
      var matchingintensities = this.ChatTriggers.Where(t => message.Contains(t.Text.ToLower()));
      if(matchingintensities.Any() && this.DeviceController.IsConnected()) {
        int intensity = matchingintensities.Select(t => t.Intensity).Max();
        this.Logger.Debug($"Sending vibe from chat {message}, {intensity}");
        this.DeviceController.SendVibeToAll(intensity);
      }*/
    }



    private void Command_LoadConfig(string args) {
      string config;
      try {
        string path = args.Split(" ")[1];
        config = File.ReadAllText(path);
      } catch(Exception e) {
        this.Logger.Error($"Malformed or invalid arguments for [load]: {args}", e);
        return;
      }
      foreach(string line in config.Split("\n")) {
        string[] trigargs = line.Split(" ");
        string toMatch = trigargs[1];
        if(int.TryParse(trigargs[0], out int intensity)) {
          ChatTrigger trigger = new(intensity, toMatch);
          if(!ChatTriggers.Add(trigger)) {
            this.Logger.Chat($"Note: duplicate trigger: {trigger}");
          };
        }
      }
      UpdateTriggersConfig();
    }

    private void UpdateTriggersConfig() {
      this.Configuration.CHAT_TRIGGERS = this.ChatTriggers;
      this.Configuration.Save();
    }

    private void LoadTriggersConfig() {
      //TODO: enable me to save triggers configuration
      this.TriggersController.Set(this.Configuration.TRIGGERS_NEW_2);
      SortedSet<ChatTrigger> chatTriggers = this.Configuration.CHAT_TRIGGERS;
      this.Logger.Debug($"Loading {chatTriggers.Count} triggers");
      this.ChatTriggers = new SortedSet<ChatTrigger>();
      foreach(ChatTrigger trigger in chatTriggers) {
        this.ChatTriggers.Add(new ChatTrigger(trigger.Intensity, trigger.Text));
      }
    }

    private void Command_SaveConfig(string args) {
      string path;
      var config = string.Join("\n", ChatTriggers.Select(t => t.ToString()));
      try {
        path = args.Split(" ")[1];
        File.WriteAllText(path, config);
      } catch(Exception e) {
        this.Logger.Error($"Malformed or invalid arguments for [save]: {args}", e);
        return;
      }
      this.Logger.Chat($"Wrote current config to {path}");
    }

    private void Command_SetAuthorizedUser(string args) {
      try {
        AuthorizedUser = args.Split(" ", 2)[1];
      } catch(IndexOutOfRangeException) {
        this.Logger.Chat("Cleared authorized user.");
        return;
      }
      this.Logger.Chat($"Authorized user set to '{AuthorizedUser}'");
    }

    private void Command_AddTrigger(string args) {
      string[] blafuckcsharp;
      int intensity;
      string text;
      try {
        blafuckcsharp = args.Split(" ", 3);
        intensity = int.Parse(blafuckcsharp[1]);
        text = blafuckcsharp[2].ToLower(); ;
      } catch(Exception e) when(e is FormatException or IndexOutOfRangeException) {
        this.Logger.Error($"Malformed arguments for [chat_add].", e);
        return; // XXX: exceptional control flow
      }
      ChatTrigger newTrigger = new(intensity, text);

      if(ChatTriggers.Add(newTrigger)) {
        this.Logger.Chat($"Trigger added successfully: {newTrigger}...");
        this.UpdateTriggersConfig();
      } else {
        this.Logger.Error($"Failed. Possible duplicate for intensity {intensity}");
      }
    }
    private void Command_RemoveTrigger(string args) {
      int id;
      try {
        id = int.Parse(args.Split(" ")[1]);
        if(id < 0) {
          throw new FormatException(); // XXX: exceptionally exceptional control flow please somnenoee hehhehjel;;  ,.-
        }
      } catch(FormatException e) {
        this.Logger.Error("Malformed argument for [chat_remove]", e);
        return; // XXX: exceptional control flow
      }
      ChatTrigger removed = ChatTriggers.ElementAt(id);
      ChatTriggers.Remove(removed);
      this.Logger.Chat($"Removed Trigger: {removed}");
      this.UpdateTriggersConfig();
    }

    private void Command_ListTriggers() {
      string message =
          @"Configured triggers:
ID   Intensity   Text Match
";
      for(int i = 0; i < ChatTriggers.Count; ++i) {
        message += $"[{i}] | {ChatTriggers.ElementAt(i).Intensity} | {ChatTriggers.ElementAt(i).Text}\n";
      }
      this.Logger.Chat(message);
    }

    private void Player_currentHPChanged(object? send, EventArgs e) {
      float currentHP = this.PlayerStats.GetCurrentHP();
      float maxHP = this.PlayerStats.GetMaxHP();

      if(this.Configuration.VIBE_HP_TOGGLE) {
        float percentageHP = currentHP / maxHP * 100f;
        float percentage = 100 - percentageHP;
        if(percentage == 0) {
          percentage = 0;
        }
        this.Logger.Debug($"Current: HP={currentHP} MaxHP={maxHP} Percentage={percentage}");

        int mode = this.Configuration.VIBE_HP_MODE;
        if(mode == 0) { // normal
          this.DeviceController.SendVibeToAll((int)percentage);
        } else if(mode == 1) { // shake
          this.DeviceController.Play_PatternShake(percentage);
        } else if(mode == 2) { // mountain
          this.DeviceController.Play_PatternMountain(percentage);
        }
      }
    }

    private void Command_SendIntensity(string args) {
      string[] blafuckcsharp;
      int intensity;
      try {
        blafuckcsharp = args.Split(" ", 2);
        intensity = int.Parse(blafuckcsharp[1]);
        this.Logger.Chat($"Command Send intensity {intensity}");
      } catch(Exception e) when(e is FormatException or IndexOutOfRangeException) {
        this.Logger.Error($"Malformed arguments for send [intensity].", e);
        return;
      }
      this.DeviceController.SendVibeToAll(intensity);
    }

    

    
  }
}
