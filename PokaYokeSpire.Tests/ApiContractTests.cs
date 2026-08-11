using Xunit;

namespace PokaYokeSpire.Tests;

/// <summary>
/// Every game member the mod calls. If a game update breaks one, its test fails here —
/// no relaunch needed. Grouped by the mod file that depends on it.
/// </summary>
public class ApiContractTests : IClassFixture<GameApi>
{
    private readonly GameApi _g;
    public ApiContractTests(GameApi g) => _g = g;

    // ---- Guard 1: EndTurnEnergyGuard (hooks OnRelease; reads energy) ----
    [Fact] public void EndTurnButton_HasOnRelease() => _g.AssertMethod("MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton", "OnRelease");
    [Fact] public void EndTurnButton_HasCallReleaseLogic() => _g.AssertMethod("MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton", "CallReleaseLogic");
    [Fact] public void EndTurnButton_HasCombatStateField() => _g.AssertField("MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton", "_combatState");
    [Fact] public void EndTurnButton_HasAnimIn() => _g.AssertMethod("MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton", "AnimIn");
    [Fact] public void PlayerCombatState_HasEnergy() => _g.AssertProperty("MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState", "Energy");
    [Fact] public void PlayerCombatState_HasCardsToPlay() => _g.AssertMethod("MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState", "HasCardsToPlay");
    [Fact] public void LocalContext_HasGetMe() => _g.AssertMethod("MegaCrit.Sts2.Core.Context.LocalContext", "GetMe");

    // ---- Guard 2: ElitePotionGuard (AnimIn postfix; potions + encounter) ----
    [Fact] public void CombatManager_HasInstance() => _g.AssertProperty("MegaCrit.Sts2.Core.Combat.CombatManager", "Instance");
    [Fact] public void CombatManager_HasDebugOnlyGetState() => _g.AssertMethod("MegaCrit.Sts2.Core.Combat.CombatManager", "DebugOnlyGetState");
    [Fact] public void CombatState_HasEncounter() => _g.AssertProperty("MegaCrit.Sts2.Core.Combat.CombatState", "Encounter");
    [Fact] public void EncounterModel_HasRoomType() => _g.AssertProperty("MegaCrit.Sts2.Core.Models.EncounterModel", "RoomType");
    [Fact] public void RoomType_HasEliteAndBoss()
    {
        var t = _g.Type("MegaCrit.Sts2.Core.Rooms.RoomType");
        Assert.True(Enum.GetNames(t).Contains("Elite"), "RoomType.Elite missing");
        Assert.True(Enum.GetNames(t).Contains("Boss"), "RoomType.Boss missing");
    }
    [Fact] public void Player_HasPotions() => _g.AssertProperty("MegaCrit.Sts2.Core.Entities.Players.Player", "Potions");
    [Fact] public void Player_HasOpenPotionSlots() => _g.AssertProperty("MegaCrit.Sts2.Core.Entities.Players.Player", "HasOpenPotionSlots");

    // ---- Guard 3: DeckCheckGuard (reward + deck screens) ----
    [Fact] public void CardReward_HasRefreshOptions() => _g.AssertMethod("MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen", "RefreshOptions");
    [Fact] public void CardReward_HasSelectCard() => _g.AssertMethod("MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen", "SelectCard");
    [Fact] public void DeckView_HasShowScreen() => _g.AssertMethod("MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen", "ShowScreen");

    // ---- Feature 4/5/6: energy, radial relics, blue counters ----
    [Fact] public void EnergyCounter_HasLayersField() => _g.AssertField("MegaCrit.Sts2.Core.Nodes.Combat.NEnergyCounter", "_layers");
    [Fact] public void EnergyCounter_HasProcess() => _g.AssertMethod("MegaCrit.Sts2.Core.Nodes.Combat.NEnergyCounter", "_Process");
    [Fact] public void RelicInventory_HasOnRelicClicked() => _g.AssertMethod("MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory", "OnRelicClicked");
    [Fact] public void RelicHolder_HasRelic() => _g.AssertProperty("MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventoryHolder", "Relic");
    [Fact] public void NRelic_HasCreate() => _g.AssertMethod("MegaCrit.Sts2.Core.Nodes.Relics.NRelic", "Create");
    [Fact] public void NRelic_HasIconSizeEnum() => Assert.True(_g.Type("MegaCrit.Sts2.Core.Nodes.Relics.NRelic").GetNestedType("IconSize", GameApi.Any) is not null, "NRelic.IconSize missing");
    [Fact] public void RelicModel_HasShowCounter() => _g.AssertProperty("MegaCrit.Sts2.Core.Models.RelicModel", "ShowCounter");
    [Fact] public void RelicModel_HasDisplayAmount() => _g.AssertProperty("MegaCrit.Sts2.Core.Models.RelicModel", "DisplayAmount");
    [Fact] public void ClickableControl_HasGuiInput() => _g.AssertMethod("MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl", "_GuiInput");

    // ---- PopupHelper (modal system) ----
    [Fact] public void ModalContainer_HasInstanceAndAdd()
    {
        _g.AssertProperty("MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer", "Instance");
        _g.AssertMethod("MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer", "Add");
    }
    [Fact] public void GenericPopup_HasCreateAndWait()
    {
        _g.AssertMethod("MegaCrit.Sts2.Core.Nodes.Multiplayer.NGenericPopup", "Create");
        _g.AssertMethod("MegaCrit.Sts2.Core.Nodes.Multiplayer.NGenericPopup", "WaitForConfirmation");
    }

    // ---- PopupHelper: runtime loc-table registration (for popup text) ----
    [Fact] public void LocManager_HasInstance() => _g.AssertProperty("MegaCrit.Sts2.Core.Localization.LocManager", "Instance");
    [Fact] public void LocManager_HasTablesField() => _g.AssertField("MegaCrit.Sts2.Core.Localization.LocManager", "_tables");
    [Fact] public void LocTable_HasStringDictCtor()
    {
        var t = _g.Type("MegaCrit.Sts2.Core.Localization.LocTable");
        Assert.True(
            t.GetConstructors(GameApi.Any).Any(c =>
            {
                var p = c.GetParameters();
                return p.Length >= 2 && p[0].ParameterType.Name == "String"
                    && p[1].ParameterType.Name.StartsWith("Dictionary");
            }),
            "LocTable(string, Dictionary<string,string>) ctor missing");
    }

    // ---- Plugin: mod entry + config (BaseLib) ----
    [Fact] public void ModInitializerAttribute_Exists() => _g.Type("MegaCrit.Sts2.Core.Modding.ModInitializerAttribute");
    [Fact] public void ModManifest_HasIdField() => _g.AssertField("MegaCrit.Sts2.Core.Modding.ModManifest", "id");
}
