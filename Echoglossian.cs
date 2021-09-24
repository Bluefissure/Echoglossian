﻿// <copyright file="Echoglossian.cs" company="lokinmodar">
// Copyright (c) lokinmodar. All rights reserved.
// Licensed under the Creative Commons Attribution-NonCommercial-NoDerivatives 4.0 International Public License license.
// </copyright>

using System;
using System.Globalization;
using System.Reflection;
using System.Threading;

using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Gui.Toast;
using Dalamud.IoC;
using Dalamud.Plugin;
using Echoglossian.EFCoreSqlite;
using Echoglossian.Properties;

using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiScene;
using XivCommon;

namespace Echoglossian
{
  // TODO: Handle weird alphabets correctly.
  // TODO: Add translation database history.
  // TODO: implement multiple fallback translation engines.
  public partial class Echoglossian : IDalamudPlugin
  {
    private const string SlashCommand = "/eglo";
    private static int languageInt = 16;
    private static int fontSize = 20;
    private static int chosenTransEngine = 0;
    private static string transEngineName;

    private readonly CommandManager commandManager;
    private bool config;
    private readonly Config configuration;

    private readonly Framework framework;
    private readonly GameGui gameGui;

    private readonly SemaphoreSlim toastTranslationSemaphore;
    private readonly SemaphoreSlim talkTranslationSemaphore;
    private readonly SemaphoreSlim nameTranslationSemaphore;

    private readonly DalamudPluginInterface pluginInterface;
    private readonly TextureWrap pixImage;
    private readonly TextureWrap choiceImage;
    private readonly TextureWrap cutsceneChoiceImage;
    private readonly TextureWrap talkImage;

    private readonly ToastGui toastGui;
    private CultureInfo cultureInfo;

    /// <summary>
    ///   Initializes a new instance of the <see cref="Echoglossian" /> class.
    /// </summary>
    /// <param name="dalamudPluginInterface">Plugin Interface.</param>
    /// <param name="pframework">Framework.</param>
    /// <param name="pCommandManager">Command Manager.</param>
    /// <param name="pGameGui">Game Gui object.</param>
    /// <param name="pToastGui">Toast Gui Object.</param>
    public Echoglossian(
      [RequiredVersion("1.0")] DalamudPluginInterface dalamudPluginInterface,
      Framework pframework,
      [RequiredVersion("1.0")] CommandManager pCommandManager,
      GameGui pGameGui,
      ToastGui pToastGui)
    {
      this.framework = pframework;

      this.pluginInterface = dalamudPluginInterface;
      this.configuration = this.pluginInterface.GetPluginConfig() as Config ?? new Config();
      this.cultureInfo = new CultureInfo(this.configuration.PluginCulture);

      Common = new XivCommonBase(Hooks.Talk | Hooks.BattleTalk);

      this.commandManager = pCommandManager;
      this.commandManager.AddHandler(SlashCommand, new CommandInfo(this.Command)
      {
        HelpMessage = Resources.HelpMessage,
      });

      this.gameGui = pGameGui;

      this.toastGui = pToastGui;

      this.CreateOrUseDb();
      this.ListCultureInfos();

      this.pixImage = this.pluginInterface.UiBuilder.LoadImage(Resources.pix);
      this.choiceImage = this.pluginInterface.UiBuilder.LoadImage(Resources.choice);
      this.cutsceneChoiceImage = this.pluginInterface.UiBuilder.LoadImage(Resources.cutscenechoice);
      this.talkImage = this.pluginInterface.UiBuilder.LoadImage(Resources.prttws);

      this.pluginInterface.UiBuilder.DisableCutsceneUiHide = this.configuration.ShowInCutscenes;

      this.pluginInterface.UiBuilder.Draw += this.EchoglossianConfigUi;
      this.pluginInterface.UiBuilder.OpenConfigUi += this.ConfigWindow;

      languageInt = this.configuration.Lang;

      fontSize = this.configuration.FontSize;

      chosenTransEngine = this.configuration.ChosenTransEngine;

      transEngineName = ((TransEngines)chosenTransEngine).ToString();

      this.framework.Update += this.Tick;

      this.nameTranslationSemaphore = new SemaphoreSlim(1, 1);
      this.toastTranslationSemaphore = new SemaphoreSlim(1, 1);
      this.talkTranslationSemaphore = new SemaphoreSlim(1, 1);

      this.toastGui.Toast += this.OnToast;
      this.toastGui.ErrorToast += this.OnToast;
      this.toastGui.QuestToast += this.OnToast;

      Common.Functions.Talk.OnTalk += this.GetTalk;
      Common.Functions.BattleTalk.OnBattleTalk += this.GetBattleTalk;
      this.pluginInterface.UiBuilder.Draw += this.DrawTranslatedDialogueWindow;
      this.pluginInterface.UiBuilder.Draw += this.DrawTranslatedToastWindow;
    }

    /// <summary>
    ///   Gets or sets AssemblyLocation. When loaded by LivePluginLoader, the executing assembly will be wrong. Supplying this
    ///   property allows LivePluginLoader to supply the correct location, so that you have full compatibility when loaded
    ///   normally and through LPL.
    /// </summary>
    public string AssemblyLocation { get; set; } = Assembly.GetExecutingAssembly().Location;

    private static XivCommonBase Common { get; set; }

    public string Name => Resources.Name;

    /// <inheritdoc />
    public void Dispose()
    {
      this.Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
      Common.Functions.Talk.OnTalk -= this.GetTalk;
      Common.Functions.BattleTalk.OnBattleTalk -= this.GetBattleTalk;
      Common?.Functions.Dispose();
      Common?.Dispose();

      this.toastGui.Toast -= this.OnToast;
      this.toastGui.ErrorToast -= this.OnToast;
      this.toastGui.QuestToast -= this.OnToast;

      this.pluginInterface.UiBuilder.OpenConfigUi -= this.ConfigWindow;

      this.nameTranslationSemaphore?.Dispose();
      this.toastTranslationSemaphore?.Dispose();
      this.talkTranslationSemaphore?.Dispose();

      this.pluginInterface.UiBuilder.Draw -= this.DrawTranslatedDialogueWindow;
      this.pluginInterface.UiBuilder.Draw -= this.DrawTranslatedToastWindow;
      this.pluginInterface.UiBuilder.Draw -= this.EchoglossianConfigUi;

      this.pixImage?.Dispose();
      this.choiceImage?.Dispose();
      this.cutsceneChoiceImage?.Dispose();
      this.talkImage?.Dispose();

      this.framework.Update -= this.Tick;

      this.UiFont.Destroy();

      this.commandManager.RemoveHandler(SlashCommand);
      /*this.toastGui?.Dispose();
      this.gameGui?.Dispose();*/

      // this.pluginInterface.UiBuilder.Dispose();

      // this.pluginInterface?.Dispose();
    }

    private void Tick(Framework tFramework)
    {
      if (this.configuration.UseImGui)
      {
        this.TalkHandler("Talk", 1);
        this.ToastHandler("_TextError", 1);
        this.ToastHandler("_TextError", 2);
        this.ToastHandler("_WideText", 1);
        this.ToastHandler("_WideText", 2);
        this.ToastHandler("_ScreenText", 1);
        this.ToastHandler("_ScreenText", 2);
        this.ToastHandler("_AreaText", 1);
        this.ToastHandler("_AreaText", 2);
      }
      else
      {
        this.toastDisplayTranslation = false;
        this.talkDisplayTranslation = false;
      }
    }

    private unsafe void TalkHandler(string addonName, int index)
    {
      var talk = this.gameGui.GetAddonByName(addonName, index);
      if (talk != IntPtr.Zero)
      {
        var talkMaster = (AtkUnitBase*)talk;
        if (talkMaster->IsVisible)
        {
          this.talkDisplayTranslation = true;
          this.talkTextDimensions.X = talkMaster->RootNode->Width * talkMaster->Scale;
          this.talkTextDimensions.Y = talkMaster->RootNode->Height * talkMaster->Scale;
          this.talkTextPosition.X = talkMaster->RootNode->X;
          this.talkTextPosition.Y = talkMaster->RootNode->Y;
        }
        else
        {
          this.talkDisplayTranslation = false;
        }
      }
      else
      {
        this.talkDisplayTranslation = false;
      }
    }

    private unsafe void ToastHandler(string toastName, int index)
    {
      var toastByName = this.gameGui.GetAddonByName(toastName, index);
      if (toastByName != IntPtr.Zero)
      {
        var toastByNameMaster = (AtkUnitBase*)toastByName;
        if (toastByNameMaster->IsVisible)
        {
          this.toastDisplayTranslation = true;
          this.toastTranslationTextDimensions.X = toastByNameMaster->RootNode->Width * toastByNameMaster->Scale * 2;
          this.toastTranslationTextDimensions.Y = toastByNameMaster->RootNode->Height * toastByNameMaster->Scale;
          this.toastTranslationTextPosition.X = toastByNameMaster->RootNode->X;
          this.toastTranslationTextPosition.Y = toastByNameMaster->RootNode->Y;
        }
        else
        {
          this.toastDisplayTranslation = false;
        }
      }
      else
      {
        this.toastDisplayTranslation = false;
      }
    }

    private unsafe void AddonHandlers(string addonName, int index)
    {
      var addonByName = this.gameGui.GetAddonByName(addonName, index);
      if (addonByName != IntPtr.Zero)
      {
        var addonByNameMaster = (AtkUnitBase*)addonByName;
        if (addonByNameMaster->IsVisible)
        {
          this.addonDisplayTranslation = true;
          this.addonTranslationTextDimensions.X = addonByNameMaster->RootNode->Width * addonByNameMaster->Scale * 2;
          this.addonTranslationTextDimensions.Y = addonByNameMaster->RootNode->Height * addonByNameMaster->Scale;
          this.addonTranslationTextPosition.X = addonByNameMaster->RootNode->X;
          this.addonTranslationTextPosition.Y = addonByNameMaster->RootNode->Y;
        }
        else
        {
          this.addonDisplayTranslation = false;
        }
      }
      else
      {
        this.addonDisplayTranslation = false;
      }
    }

    private void ConfigWindow()
    {
      this.config = true;
    }

    private void Command(string command, string arguments)
    {
      this.config = true;
    }
  }
}
