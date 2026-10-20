using System.Reflection;
using System.Runtime.CompilerServices;
using BaseLib.Utils;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace BaseLib.Patches.UI;

// Card UI for MultiEnchantment: a copied enchantment tab per extra, with its values previewed.
public static class MultiEnchantmentUiPatches
{
    internal const string ExtraTabPrefix = "BaseLibExtraEnchantment";
    private const float TabGap = 4f;

    // Status subscriptions per card node, so they can be removed when the card's enchantments change.
    private static readonly ConditionalWeakTable<NCard, List<EnchantmentModel>> Subscribed = new();

    [HarmonyPatch(typeof(NCard), nameof(NCard.UpdateEnchantmentVisuals))]
    static class ExtraTabs
    {
        [HarmonyPostfix]
        static void Postfix(NCard __instance)
        {
            var tab = __instance._enchantmentTab;
            RemoveExtraTabs(tab);
            Resubscribe(__instance);

            var model = __instance.Model;
            if (model == null) return;
            var extras = ExtraEnchantments.Peek(model);
            for (var i = 0; i < extras.Count; i++)
            {
                var extra = extras[i];
                var copy = (Control)tab.Duplicate();
                copy.Name = $"{ExtraTabPrefix}{i}";
                // The tab's shader material is shared and the status tint edits it; give each copy its own.
                if (tab.Material != null) copy.Material = (Material)tab.Material.Duplicate();
                tab.GetParent().AddChild(copy);
                copy.Position = tab.Position + Vector2.Down * (tab.Size.Y + TabGap) * (i + 1);

                var icon = copy.GetNode<TextureRect>("Icon");
                icon.Texture = extra.Icon;
                var label = copy.GetNode<MegaLabel>("Label");
                label.SetTextAutoSize(extra.DisplayAmount.ToString());
                label.Visible = extra.ShowAmount;
                // The copy inherits the slot tab's status styling; restyle it for this enchantment's own status.
                ApplyStatus(copy, icon, label, extra.Status);
            }
        }

        // Same styling as NCard.SetEnchantmentStatus, applied to a copied tab.
        private static void ApplyStatus(Control tab, TextureRect icon, MegaLabel label, EnchantmentStatus status)
        {
            var disabled = status == EnchantmentStatus.Disabled;
            tab.Modulate = disabled ? new Color(1f, 1f, 1f, 0.9f) : Colors.White;
            if (tab.Material is ShaderMaterial material)
            {
                material.SetShaderParameter("h", 0.25);
                material.SetShaderParameter("s", disabled ? 0.1 : 0.4);
                material.SetShaderParameter("v", 0.6);
            }
            icon.UseParentMaterial = disabled;
            label.SelfModulate = disabled ? StsColors.gray : Colors.White;
        }

        private static void RemoveExtraTabs(Control tab)
        {
            var parent = tab.GetParent();
            foreach (var node in parent.GetChildren().Where(n => n.Name.ToString().StartsWith(ExtraTabPrefix)))
            {
                parent.RemoveChild(node);
                node.QueueFree();
            }
        }

        // Redraw when an extra enchantment's status changes (vanilla only listens to the slot's).
        private static void Resubscribe(NCard card)
        {
            if (Subscribed.TryGetValue(card, out var previous))
            {
                foreach (var enchantment in previous) enchantment.StatusChanged -= card.UpdateEnchantmentVisuals;
            }
            var current = card.Model == null ? [] : ExtraEnchantments.Peek(card.Model).ToList();
            foreach (var enchantment in current) enchantment.StatusChanged += card.UpdateEnchantmentVisuals;
            Subscribed.AddOrUpdate(card, current);
        }
    }

    // When the enchantment just added was an extra, the enchant animation shows it and plays over its tab.
    [HarmonyPatch(typeof(NCardEnchantVfx), nameof(NCardEnchantVfx._Ready))]
    static class EnchantAnimationShowsExtra
    {
        [HarmonyPostfix]
        static void Postfix(NCardEnchantVfx __instance)
        {
            var card = __instance._cardModel;
            if (!ExtraEnchantments.LastChanged.TryGetValue(card, out var changed)) return;
            var index = ExtraEnchantments.Peek(card).ToList().IndexOf(changed);
            if (index < 0) return;

            __instance._enchantmentIcon.Texture = changed.Icon;
            __instance._enchantmentLabel.SetTextAutoSize(changed.DisplayAmount.ToString());
            __instance._enchantmentLabel.Visible = changed.ShowAmount;

            // Vanilla hides the slot's tab for the effect; keep it and play the effect over the extra's tab.
            var cardNode = __instance._cardNode;
            var slotTab = cardNode.EnchantmentTab;
            var extraTab = slotTab.GetParent().GetNodeOrNull<Control>($"{ExtraTabPrefix}{index}");
            if (extraTab == null) return;
            slotTab.Visible = true;
            extraTab.Visible = false;
            var offset = extraTab.Position - slotTab.Position;
            cardNode.EnchantmentVfxOverride.Position += offset;
            __instance._enchantmentSparkles.Position += offset * cardNode.Scale;
        }
    }

    [HarmonyPatch(typeof(NDeckHistoryEntry), nameof(NDeckHistoryEntry.Reload))]
    static class DeckHistoryIcons
    {
        private const string ExtraIconPrefix = "BaseLibExtraEnchantmentIcon";
        private const float IconGap = 2f;

        [HarmonyPostfix]
        static void Postfix(NDeckHistoryEntry __instance)
        {
            var icon = __instance._enchantmentImage;
            var parent = icon.GetParent();
            foreach (var old in parent.GetChildren().Where(n => n.Name.ToString().StartsWith(ExtraIconPrefix)))
            {
                parent.RemoveChild(old);
                old.QueueFree();
            }

            var extras = ExtraEnchantments.Peek(__instance.Card);
            for (var i = 0; i < extras.Count; i++)
            {
                var copy = (TextureRect)icon.Duplicate();
                copy.Name = $"{ExtraIconPrefix}{i}";
                copy.Texture = extras[i].Icon;
                copy.Visible = true;
                parent.AddChild(copy);
                copy.Position = icon.Position + Vector2.Right * (icon.Size.X + IconGap) * (i + 1);
            }
            if (extras.Count > 0)
            {
                __instance.Size += Vector2.Right * (icon.Size.X + IconGap) * extras.Count;
            }
        }
    }

    // Multiplayer deck grouping keeps cards that differ only in extra enchantments apart.
    static class MultiplayerDeckGrouping
    {
        private static readonly Type? KeyType = AccessTools.Inner(typeof(NMultiplayerPlayerExpandedState), "CardGroupKey");

        private static CardModel CardOf(object key) => Traverse.Create(key).Field("_card").GetValue<CardModel>();

        [HarmonyPatch]
        static class KeyEquals
        {
            static bool Prepare() => KeyType != null;
            static MethodBase TargetMethod() => AccessTools.Method(KeyType, nameof(Equals), [typeof(object)]);

            [HarmonyPostfix]
            static void Postfix(object __instance, object? obj, ref bool __result)
            {
                if (__result && obj != null)
                    __result = ExtraEnchantments.Signature(CardOf(__instance)) == ExtraEnchantments.Signature(CardOf(obj));
            }
        }

        [HarmonyPatch]
        static class KeyHash
        {
            static bool Prepare() => KeyType != null;
            static MethodBase TargetMethod() => AccessTools.Method(KeyType, nameof(GetHashCode));

            [HarmonyPostfix]
            static void Postfix(object __instance, ref int __result) =>
                __result = HashCode.Combine(__result, ExtraEnchantments.Signature(CardOf(__instance)));
        }
    }

    // Preview extra enchantments' own values (used in their card text), like vanilla does for the slot.
    [HarmonyPatch(typeof(NCard), nameof(NCard.UpdateVisuals))]
    static class PreviewExtraValues
    {
        [HarmonyPrefix]
        static void Prefix(NCard __instance, CardPreviewMode previewMode)
        {
            var model = __instance.Model;
            if (!__instance.IsNodeReady() || model == null || __instance._forceUnpoweredPreview) return;
            var extras = ExtraEnchantments.Peek(model);
            if (extras.Count == 0) return;

            var target = __instance._previewTarget ?? model.CurrentTarget;
            foreach (var extra in extras)
            {
                extra.DynamicVars.ClearPreview();
                model.UpdateDynamicVarPreview(previewMode, target, extra.DynamicVars);
            }
        }
    }
}
