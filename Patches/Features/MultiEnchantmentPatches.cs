using System.Reflection;
using System.Reflection.Emit;
using BaseLib.Utils;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace BaseLib.Patches.Features;

// Patches behind MultiEnchantment; enchanting rules only change once a mod enables it.
public static class MultiEnchantmentPatches
{
    [HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.CanEnchant))]
    static class AllowEnchantedCards
    {
        // Vanilla refuses already-enchanted cards; keep its other checks (subclass restrictions still call this).
        [HarmonyPrefix]
        static bool Prefix(EnchantmentModel __instance, CardModel card, ref bool __result)
        {
            if (!MultiEnchantment.IsEnabled || card.Enchantment == null) return true;

            __result = card.Type is not (CardType.Status or CardType.Curse or CardType.Quest)
                       && __instance.CanEnchantCardType(card.Type)
                       && !(card.Pile is { Type: PileType.Deck } && card.Keywords.Contains(CardKeyword.Unplayable))
                       && !MultiEnchantment.ConflictsWithCard(__instance, card);
            return false;
        }
    }

    // After an enchantment applies its card changes, re-apply any enchantment with priority over it.
    [HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.ModifyCard))]
    static class ApplyPriorities
    {
        [ThreadStatic] private static bool _reapplying;

        [HarmonyPostfix]
        static void Postfix(EnchantmentModel __instance)
        {
            if (_reapplying || !__instance.HasCard) return;
            var winners = MultiEnchantment.WinnersOver(__instance).ToList();
            if (winners.Count == 0) return;

            // A winner re-applying must not start another round.
            _reapplying = true;
            try
            {
                foreach (var winner in winners) winner.ModifyCard();
            }
            finally
            {
                _reapplying = false;
            }
        }
    }

    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Enchant), typeof(EnchantmentModel), typeof(CardModel), typeof(decimal))]
    static class EnchantExtra
    {
        [HarmonyPrefix]
        static bool Prefix(EnchantmentModel enchantment, CardModel card, decimal amount, ref EnchantmentModel? __result)
        {
            // Empty slot, or the same type as the slot: vanilla handles both (the same type adds to the amount).
            if (!MultiEnchantment.IsEnabled || card.Enchantment == null || card.Enchantment.GetType() == enchantment.GetType())
            {
                ExtraEnchantments.LastChanged.Remove(card);
                return true;
            }

            enchantment.AssertMutable();
            if (!enchantment.CanEnchant(card))
                throw new InvalidOperationException($"Cannot enchant {card.Id} with {enchantment.Id}.");

            var existing = card.GetEnchantmentOfSameType(enchantment);
            if (existing != null)
            {
                existing.Amount += (int)amount;
            }
            else
            {
                ExtraEnchantments.Add(card, enchantment, amount);
                existing = enchantment;
            }

            // The rest of vanilla's Enchant.
            card.FinalizeUpgradeInternal();
            if (card.Pile is { Type: PileType.Deck })
            {
                card.Owner.RunState.CurrentMapPointHistoryEntry?.GetEntry(card.Owner.NetId).CardsEnchanted
                    .Add(new CardEnchantmentHistoryEntry(card, enchantment.Id));
            }
            ExtraEnchantments.LastChanged.AddOrUpdate(card, existing);
            MultiEnchantment.RaiseEnchantmentChanged(card);
            __result = existing;
            return false;
        }
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.ClearEnchantmentInternal))]
    static class ClearExtras
    {
        [HarmonyPrefix]
        static void Prefix(CardModel __instance) => ExtraEnchantments.Clear(__instance);
    }

    // Wraps the game's enchantment hook calls (found by IL scan) so extras run too; the original call stays for other transpilers.
    [HarmonyPatch]
    static class IncludeExtrasInEnchantmentHooks
    {
        private static readonly Dictionary<MethodInfo, MethodInfo> Wrappers = new()
        {
            [Method(nameof(EnchantmentModel.EnchantDamageAdditive))] = Helper(nameof(EnchantmentHookHelpers.DamageAdditive)),
            [Method(nameof(EnchantmentModel.EnchantDamageMultiplicative))] = Helper(nameof(EnchantmentHookHelpers.DamageMultiplicative)),
            [Method(nameof(EnchantmentModel.EnchantBlockAdditive))] = Helper(nameof(EnchantmentHookHelpers.BlockAdditive)),
            [Method(nameof(EnchantmentModel.EnchantBlockMultiplicative))] = Helper(nameof(EnchantmentHookHelpers.BlockMultiplicative)),
            [Method(nameof(EnchantmentModel.EnchantPlayCount))] = Helper(nameof(EnchantmentHookHelpers.PlayCount)),
            [Method(nameof(EnchantmentModel.OnPlay))] = Helper(nameof(EnchantmentHookHelpers.OnPlay)),
            [AccessTools.PropertyGetter(typeof(EnchantmentModel), nameof(EnchantmentModel.ShouldStartAtBottomOfDrawPile))]
                = Helper(nameof(EnchantmentHookHelpers.ShouldStartAtBottomOfDrawPile)),
        };

        private static MethodInfo Method(string name) => AccessTools.Method(typeof(EnchantmentModel), name);
        private static MethodInfo Helper(string name) => AccessTools.Method(typeof(EnchantmentHookHelpers), name);

        static IEnumerable<MethodBase> TargetMethods() =>
            GameIlScanner.MethodsCalling(Wrappers.Keys, skipEnchantmentModels: true);

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            foreach (var ins in instructions)
            {
                if (ins.operand is not MethodInfo called || !Wrappers.TryGetValue(called, out var wrapper)
                    || (ins.opcode != OpCodes.Callvirt && ins.opcode != OpCodes.Call))
                {
                    yield return ins;
                    continue;
                }

                // Stack [enchantment, args]: save to locals, make the call, pass result + enchantment + args to the wrapper.
                var args = called.GetParameters().Select(p => generator.DeclareLocal(p.ParameterType)).ToArray();
                var enchantment = generator.DeclareLocal(typeof(EnchantmentModel));

                var first = true;
                foreach (var arg in Enumerable.Reverse(args))
                {
                    yield return Moved(new CodeInstruction(OpCodes.Stloc, arg), ins, ref first);
                }
                yield return Moved(new CodeInstruction(OpCodes.Dup), ins, ref first);
                yield return new CodeInstruction(OpCodes.Stloc, enchantment);
                foreach (var arg in args) yield return new CodeInstruction(OpCodes.Ldloc, arg);
                yield return new CodeInstruction(ins.opcode, ins.operand);

                yield return new CodeInstruction(OpCodes.Ldloc, enchantment);
                foreach (var arg in args) yield return new CodeInstruction(OpCodes.Ldloc, arg);
                yield return new CodeInstruction(OpCodes.Call, wrapper);
            }
        }

        // Jump targets and exception blocks on the original call move to the first instruction that replaces it.
        private static CodeInstruction Moved(CodeInstruction replacement, CodeInstruction original, ref bool first)
        {
            if (!first) return replacement;
            first = false;
            return replacement.MoveLabelsFrom(original).MoveBlocksFrom(original);
        }
    }

    // Makes card.Enchantment is T checks (Thieving Hopper, Clone rest site) use MultiEnchantment.GetEnchantment<T>.
    [HarmonyPatch]
    static class TypeChecksSeeExtras
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            GameIlScanner.MethodsCalling([EnchantmentGetter], skipEnchantmentModels: false, followedByIsinst: true);

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                if (i + 1 < list.Count && list[i].Calls(EnchantmentGetter)
                    && list[i + 1].opcode == OpCodes.Isinst && list[i + 1].operand is Type t
                    && t != typeof(EnchantmentModel) && t.IsSubclassOf(typeof(EnchantmentModel)))
                {
                    var getTyped = AccessTools.Method(typeof(MultiEnchantment), nameof(MultiEnchantment.GetEnchantment))
                        .MakeGenericMethod(t);
                    yield return new CodeInstruction(OpCodes.Call, getTyped).MoveLabelsFrom(list[i]).MoveBlocksFrom(list[i]);
                    i++; // the isinst is replaced too
                    continue;
                }
                yield return list[i];
            }
        }
    }

    // Inside enchantment subclasses, card.Enchantment means "my own type on that card" (Goopy's deck copy), not the slot.
    [HarmonyPatch]
    static class EnchantmentsFindThemselves
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            GameIlScanner.MethodsCalling([EnchantmentGetter], skipEnchantmentModels: false, onlyEnchantmentSubclasses: true);

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var list = instructions.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var isTypeCheck = i + 1 < list.Count && list[i + 1].opcode == OpCodes.Isinst;
                if (!original.IsStatic && list[i].Calls(EnchantmentGetter) && !isTypeCheck)
                {
                    // [card] -> [card, this] -> OwnTypeOn(card, this)
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(list[i]).MoveBlocksFrom(list[i]);
                    yield return CodeInstruction.Call(typeof(EnchantmentHookHelpers), nameof(EnchantmentHookHelpers.OwnTypeOn));
                    continue;
                }
                yield return list[i];
            }
        }
    }

    private static readonly MethodInfo EnchantmentGetter =
        AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Enchantment));

    [HarmonyPatch]
    static class ExtrasReceiveHooks
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            new[]
            {
                AccessTools.Method(typeof(CombatState), nameof(CombatState.IterateHookListeners)),
                AccessTools.Method(typeof(RunState), nameof(RunState.IterateHookListeners)),
            }.Where(m => m != null)!;

        [HarmonyPostfix]
        static void Postfix(ref IEnumerable<AbstractModel> __result)
        {
            if (!ExtraEnchantments.EverUsed) return;

            var listeners = __result as IList<AbstractModel> ?? __result.ToList();
            List<AbstractModel>? expanded = null;
            for (var i = 0; i < listeners.Count; i++)
            {
                var model = listeners[i];
                if (model is CardModel card && ExtraEnchantments.Any(card))
                {
                    expanded ??= listeners.Take(i).ToList();
                    expanded.Add(model);
                    expanded.AddRange(ExtraEnchantments.Peek(card));
                }
                else
                {
                    expanded?.Add(model);
                }
            }
            __result = expanded ?? listeners;
        }
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.HoverTips), MethodType.Getter)]
    static class ExtraHoverTips
    {
        [HarmonyPostfix]
        static void Postfix(CardModel __instance, ref IEnumerable<IHoverTip> __result)
        {
            if (!ExtraEnchantments.Any(__instance)) return;
            __result = __result.Concat(ExtraEnchantments.Peek(__instance).SelectMany(e => e.HoverTips)).ToList();
        }
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.ShouldGlowGold), MethodType.Getter)]
    static class ExtraGlowGold
    {
        [HarmonyPostfix]
        static void Postfix(CardModel __instance, ref bool __result) =>
            __result |= ExtraEnchantments.Peek(__instance).Any(e => e.ShouldGlowGold);
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.ShouldGlowRed), MethodType.Getter)]
    static class ExtraGlowRed
    {
        [HarmonyPostfix]
        static void Postfix(CardModel __instance, ref bool __result) =>
            __result |= ExtraEnchantments.Peek(__instance).Any(e => e.ShouldGlowRed);
    }

    // The description has one line for the slot's extra text; add a line for each extra enchantment.
    [HarmonyPatch]
    static class ExtraCardText
    {
        static IEnumerable<MethodBase> TargetMethods() =>
        [
            AccessTools.Method(typeof(CardModel), nameof(CardModel.GetDescriptionForPile), [typeof(PileType), typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature)]),
            AccessTools.Method(typeof(CardModel), nameof(CardModel.GetDescriptionForUpgradePreview)),
        ];

        [HarmonyPostfix]
        static void Postfix(CardModel __instance, ref string __result)
        {
            if (!ExtraEnchantments.Any(__instance)) return;
            var lines = ExtraEnchantments.Peek(__instance)
                .Select(e => e.DynamicExtraCardText?.GetFormattedText())
                .Where(t => !string.IsNullOrEmpty(t))
                .Select(t => $"[purple]{t}[/purple]")
                .ToList();
            if (lines.Count > 0) __result = string.Join('\n', lines.Prepend(__result).Where(l => !string.IsNullOrEmpty(l)));
        }
    }

    // A downgrade resets the card's values, then vanilla re-applies the slot's enchantment; re-apply the extras too.
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.DowngradeInternal))]
    static class ReapplyExtrasAfterDowngrade
    {
        [HarmonyPostfix]
        static void Postfix(CardModel __instance)
        {
            foreach (var extra in ExtraEnchantments.Peek(__instance)) extra.ModifyCard();
        }
    }

    [HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.RecalculateCardValues))]
    static class RecalculateExtras
    {
        [HarmonyPostfix]
        static void Postfix(PlayerCombatState __instance)
        {
            if (!ExtraEnchantments.EverUsed) return;
            foreach (var card in __instance.AllCards)
            foreach (var extra in ExtraEnchantments.Peek(card))
                extra.RecalculateValues();
        }
    }

    internal static class SaveExtras
    {
        internal static readonly SavedSpireField<CardModel, string> Field = new(() => null, "ExtraEnchantments");

        [HarmonyPatch(typeof(SavedProperties), nameof(SavedProperties.FromInternal))]
        static class Export
        {
            // Runs before BaseLib's SavedSpireField export, so the field holds the card's current extras.
            [HarmonyPrefix]
            static void Prefix(object model)
            {
                if (model is not CardModel card || !ExtraEnchantments.EverUsed) return;
                var extras = ExtraEnchantments.Peek(card);
                Field[card] = extras.Count == 0 ? null : ExtraEnchantments.Signature(card);
            }
        }

        [HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
        static class Import
        {
            // Vanilla restores the slot's enchantment and upgrades; then add the extras the same way.
            [HarmonyPostfix]
            static void Postfix(CardModel __result)
            {
                var saved = Field[__result];
                if (string.IsNullOrEmpty(saved) || __result.Enchantment == null || __result is DeprecatedCard) return;

                foreach (var part in saved.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pieces = part.Split(':');
                    var canonical = MegaCrit.Sts2.Core.Saves.SaveUtil.EnchantmentOrDeprecated(new ModelId(__result.Enchantment.Id.Category, pieces[0]));
                    var amount = pieces.Length > 1 && int.TryParse(pieces[1], out var parsed) ? parsed : 0;
                    ExtraEnchantments.Add(__result, canonical.ToMutable(), amount);
                }
                __result.FinalizeUpgradeInternal();
                Field[__result] = null;
            }
        }
    }

    static class SavedCardEquality
    {
        private static string? SavedExtras(SerializableCard card) =>
            card.Props?.strings?.FirstOrDefault(p => p.name == SaveExtras.Field.Name).value;

        [HarmonyPatch(typeof(SerializableCard), nameof(SerializableCard.Equals), typeof(object))]
        static class CardEquals
        {
            [HarmonyPostfix]
            static void Postfix(SerializableCard __instance, object? obj, ref bool __result)
            {
                if (__result && obj is SerializableCard other)
                    __result = SavedExtras(__instance) == SavedExtras(other);
            }
        }

        [HarmonyPatch(typeof(SerializableCard), nameof(SerializableCard.GetHashCode))]
        static class CardHash
        {
            [HarmonyPostfix]
            static void Postfix(SerializableCard __instance, ref int __result) =>
                __result = HashCode.Combine(__result, SavedExtras(__instance));
        }
    }

    [HarmonyPatch]
    static class CopyExtrasToNewCard
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            new[]
            {
                AccessTools.Method(typeof(ArchaicTooth), nameof(ArchaicTooth.GetTranscendenceTransformedCard)),
                AccessTools.Method(typeof(Claws), nameof(Claws.CreateMaulFromOriginal)),
            }.Where(m => m != null)!;

        [HarmonyPostfix]
        static void Postfix(object[] __args, CardModel __result)
        {
            if (__args.Length == 0 || __args[0] is not CardModel original || __result.Enchantment == null) return;
            foreach (var extra in ExtraEnchantments.Peek(original))
            {
                var copy = (EnchantmentModel)extra.MutableClone();
                if (copy.CanEnchant(__result)) ExtraEnchantments.Add(__result, copy, extra.Amount);
            }
        }
    }
}

// Called from transpiled game code: combine the vanilla slot's result with the card's extras.
public static class EnchantmentHookHelpers
{
    private static IReadOnlyList<EnchantmentModel> Extras(EnchantmentModel slot) =>
        slot.HasCard ? ExtraEnchantments.Peek(slot.Card) : [];

    // Adds the extras' additive damage to the slot's.
    public static decimal DamageAdditive(decimal added, EnchantmentModel slot, decimal damage, ValueProp props)
    {
        foreach (var extra in Extras(slot)) added += extra.EnchantDamageAdditive(damage + added, props);
        return added;
    }

    // Multiplies in the extras' damage multipliers.
    public static decimal DamageMultiplicative(decimal multiplier, EnchantmentModel slot, decimal damage, ValueProp props)
    {
        foreach (var extra in Extras(slot)) multiplier *= extra.EnchantDamageMultiplicative(damage * multiplier, props);
        return multiplier;
    }

    // Adds the extras' additive block to the slot's.
    public static decimal BlockAdditive(decimal added, EnchantmentModel slot, decimal block)
    {
        foreach (var extra in Extras(slot)) added += extra.EnchantBlockAdditive(block + added);
        return added;
    }

    // Multiplies in the extras' block multipliers.
    public static decimal BlockMultiplicative(decimal multiplier, EnchantmentModel slot, decimal block)
    {
        foreach (var extra in Extras(slot)) multiplier *= extra.EnchantBlockMultiplicative(block * multiplier);
        return multiplier;
    }

    // Passes the play count through each extra in turn.
    public static int PlayCount(int count, EnchantmentModel slot, int originalCount)
    {
        foreach (var extra in Extras(slot)) count = extra.EnchantPlayCount(count);
        return count;
    }

    // Runs the extras' on-play effects after the slot's.
    public static Task OnPlay(Task slotPlay, EnchantmentModel slot, PlayerChoiceContext choiceContext, CardPlay? cardPlay)
    {
        var extras = Extras(slot);
        return extras.Count == 0 ? slotPlay : PlayExtras(slotPlay, slot, extras.ToList(), choiceContext, cardPlay);
    }

    private static async Task PlayExtras(Task slotPlay, EnchantmentModel slot, List<EnchantmentModel> extras,
        PlayerChoiceContext choiceContext, CardPlay? cardPlay)
    {
        await slotPlay;
        foreach (var extra in extras)
        {
            if (slot.Card.Owner.Creature.IsDead) return;
            await extra.OnPlay(choiceContext, cardPlay);
            extra.InvokeExecutionFinished();
        }
    }

    // True if the slot or any extra wants the card at the bottom of the draw pile.
    public static bool ShouldStartAtBottomOfDrawPile(bool slotValue, EnchantmentModel slot) =>
        slotValue || Extras(slot).Any(e => e.ShouldStartAtBottomOfDrawPile);

    // The enchantment of self's type on card, or a detached copy so changes go nowhere.
    public static EnchantmentModel OwnTypeOn(CardModel card, EnchantmentModel self) =>
        card.GetEnchantmentOfSameType(self) ?? (EnchantmentModel)self.MutableClone();
}

// Finds game methods that call given methods, by scanning IL bytes for their metadata tokens.
internal static class GameIlScanner
{
    private static Type[]? _gameTypes;

    public static IEnumerable<MethodBase> MethodsCalling(IEnumerable<MethodInfo> callees, bool skipEnchantmentModels,
        bool onlyEnchantmentSubclasses = false, bool followedByIsinst = false)
    {
        var tokens = callees.Select(c => c.MetadataToken).ToHashSet();
        var module = typeof(CardModel).Module;
        _gameTypes ??= AccessTools.GetTypesFromAssembly(typeof(CardModel).Assembly);

        foreach (var type in _gameTypes)
        {
            var outer = type;
            while (outer.DeclaringType != null) outer = outer.DeclaringType;
            var isEnchantment = outer.IsSubclassOf(typeof(EnchantmentModel)) || outer == typeof(EnchantmentModel);
            if (skipEnchantmentModels && isEnchantment) continue;
            if (onlyEnchantmentSubclasses && !outer.IsSubclassOf(typeof(EnchantmentModel))) continue;
            if (type.ContainsGenericParameters) continue;

            foreach (var method in type.GetMethods(AccessTools.allDeclared).Cast<MethodBase>()
                         .Concat(type.GetConstructors(AccessTools.allDeclared)))
            {
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                byte[]? il;
                try { il = method.GetMethodBody()?.GetILAsByteArray(); }
                catch { continue; }
                if (il == null || method.Module != module) continue;
                if (CallsAny(il, tokens, followedByIsinst)) yield return method;
            }
        }
    }

    // call (0x28) / callvirt (0x6F) followed by a 4-byte little-endian token.
    private static bool CallsAny(byte[] il, HashSet<int> tokens, bool followedByIsinst)
    {
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F) continue;
            if (!tokens.Contains(BitConverter.ToInt32(il, i + 1))) continue;
            // isinst is 0x75; the type check `x is T` compiles to the call followed by isinst.
            if (!followedByIsinst || (i + 5 < il.Length && il[i + 5] == 0x75)) return true;
        }
        return false;
    }
}

// Skips a losing enchantment's member while a winning one is on the card (see MultiEnchantment.AddPriority).
internal static class EnchantmentSuppression
{
    private static readonly HashSet<MethodInfo> Installed = [];

    internal static MethodInfo Resolve(Type loser, string name)
    {
        var member = AccessTools.DeclaredMethod(loser, name) ?? AccessTools.DeclaredPropertyGetter(loser, name)
            ?? throw new ArgumentException($"{loser.Name} doesn't declare a method or property named {name}.");
        if (SkipFor(member.ReturnType) == null)
            throw new ArgumentException($"{loser.Name}.{name} returns {member.ReturnType.Name}; only void, Task and bool can be skipped.");
        return member;
    }

    internal static void Install(MethodInfo member)
    {
        if (!Installed.Add(member)) return;
        BaseLibMain.MainHarmony.Patch(member, prefix: new HarmonyMethod(SkipFor(member.ReturnType)));
    }

    private static MethodInfo? SkipFor(Type returnType) =>
        returnType == typeof(void) ? AccessTools.Method(typeof(EnchantmentSuppression), nameof(SkipVoid))
        : returnType == typeof(Task) ? AccessTools.Method(typeof(EnchantmentSuppression), nameof(SkipTask))
        : returnType == typeof(bool) ? AccessTools.Method(typeof(EnchantmentSuppression), nameof(SkipBool))
        : null;

    private static bool SkipVoid(EnchantmentModel __instance, MethodBase __originalMethod) =>
        !MultiEnchantment.IsSuppressed(__instance, __originalMethod);

    private static bool SkipTask(EnchantmentModel __instance, MethodBase __originalMethod, ref Task __result)
    {
        if (!MultiEnchantment.IsSuppressed(__instance, __originalMethod)) return true;
        __result = Task.CompletedTask;
        return false;
    }

    private static bool SkipBool(EnchantmentModel __instance, MethodBase __originalMethod, ref bool __result)
    {
        if (!MultiEnchantment.IsSuppressed(__instance, __originalMethod)) return true;
        __result = false;
        return false;
    }
}
