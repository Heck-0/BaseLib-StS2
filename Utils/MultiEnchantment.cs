using System.Reflection;
using System.Runtime.CompilerServices;
using BaseLib.Patches.Features;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;

namespace BaseLib.Utils;

// Lets cards hold several enchantments (opt-in via Enable); read them with AllEnchantments, not CardModel.Enchantment.
public static class MultiEnchantment
{
    private static readonly HashSet<string> EnabledBy = [];

    // Whether any mod has turned on multiple enchantments per card.
    public static bool IsEnabled => EnabledBy.Count > 0;

    // Allows cards to hold several enchantments for the rest of the session; call from your mod's initializer.
    public static void Enable(string modId)
    {
        if (!EnabledBy.Add(modId)) return;
        BaseLibMain.Logger.Info($"Multiple enchantments per card enabled by {modId}.");
        foreach (var member in SuppressedBy.Keys) EnchantmentSuppression.Install(member);
    }

    // Every enchantment on the card: the vanilla CardModel.Enchantment slot first, then any others.
    public static IEnumerable<EnchantmentModel> AllEnchantments(this CardModel card)
    {
        if (card.Enchantment == null) yield break;
        yield return card.Enchantment;
        foreach (var extra in ExtraEnchantments.Peek(card)) yield return extra;
    }

    // The card's enchantment of type T, or null if it has none.
    public static T? GetEnchantment<T>(this CardModel card) where T : EnchantmentModel =>
        card.AllEnchantments().OfType<T>().FirstOrDefault();

    // Whether the card has an enchantment of type T.
    public static bool HasEnchantment<T>(this CardModel card) where T : EnchantmentModel =>
        card.GetEnchantment<T>() != null;

    // The card's enchantment with the same type as enchantment, or null.
    public static EnchantmentModel? GetEnchantmentOfSameType(this CardModel card, EnchantmentModel enchantment) =>
        card.AllEnchantments().FirstOrDefault(e => e.GetType() == enchantment.GetType());

    private static readonly HashSet<(Type Winner, Type Loser)> Priorities = [];
    private static readonly Dictionary<MethodInfo, HashSet<Type>> SuppressedBy = [];
    private static readonly HashSet<(Type, Type)> Incompatible = [];

    static MultiEnchantment()
    {
        AddPriority<SoulsPower, Goopy>();
        AddPriority<TezcatarasEmber, Slither>(nameof(Slither.AfterCardDrawn));
        AddPriority<RoyallyApproved, Imbued>(nameof(Imbued.ShouldStartAtBottomOfDrawPile));
    }

    // TWinner's card changes re-apply after TLoser's (must be safe twice); the named TLoser members are skipped while TWinner is on the card.
    public static void AddPriority<TWinner, TLoser>(params string[] suppressedLoserMembers)
        where TWinner : EnchantmentModel where TLoser : EnchantmentModel
    {
        Priorities.Add((typeof(TWinner), typeof(TLoser)));
        foreach (var name in suppressedLoserMembers)
        {
            var member = EnchantmentSuppression.Resolve(typeof(TLoser), name);
            if (!SuppressedBy.TryGetValue(member, out var winners)) SuppressedBy[member] = winners = [];
            winners.Add(typeof(TWinner));
            if (IsEnabled) EnchantmentSuppression.Install(member);
        }
    }

    public static void RemovePriority<TWinner, TLoser>() where TWinner : EnchantmentModel where TLoser : EnchantmentModel
    {
        Priorities.Remove((typeof(TWinner), typeof(TLoser)));
        foreach (var (member, winners) in SuppressedBy.Where(s => s.Key.DeclaringType == typeof(TLoser)))
            winners.Remove(typeof(TWinner));
    }

    internal static bool IsSuppressed(EnchantmentModel loser, MethodBase member) =>
        loser.HasCard && member is MethodInfo info && SuppressedBy.TryGetValue(info, out var winners)
        && loser.Card.AllEnchantments().Any(e => winners.Contains(e.GetType()));

    // A card can't hold both; whichever comes second can't be applied.
    public static void AddIncompatible<TA, TB>() where TA : EnchantmentModel where TB : EnchantmentModel
    {
        Incompatible.Add((typeof(TA), typeof(TB)));
        Incompatible.Add((typeof(TB), typeof(TA)));
    }

    // Removes an incompatibility, including one BaseLib defines for vanilla enchantments.
    public static void RemoveIncompatible<TA, TB>() where TA : EnchantmentModel where TB : EnchantmentModel
    {
        Incompatible.Remove((typeof(TA), typeof(TB)));
        Incompatible.Remove((typeof(TB), typeof(TA)));
    }

    internal static IEnumerable<EnchantmentModel> WinnersOver(EnchantmentModel loser) =>
        Priorities.Count == 0
            ? []
            : loser.Card.AllEnchantments().Where(e => Priorities.Contains((e.GetType(), loser.GetType())));

    internal static bool ConflictsWithCard(EnchantmentModel incoming, CardModel card) =>
        card.AllEnchantments().Any(e => Incompatible.Contains((incoming.GetType(), e.GetType())));

    private static readonly FieldInfo? EnchantmentChangedField =
        AccessTools.Field(typeof(CardModel), nameof(CardModel.EnchantmentChanged));

    internal static void RaiseEnchantmentChanged(CardModel card) =>
        (EnchantmentChangedField?.GetValue(card) as Action)?.Invoke();
}

// Enchantments beyond a card's vanilla slot, cloned with the card; only non-empty while the slot is filled.
internal sealed class ExtraEnchantments : ICloneableField
{
    private static readonly ExtraEnchantments Instance = new();
    private static readonly ConditionalWeakTable<CardModel, List<EnchantmentModel>> Table = new();

    // Whether any card has had an extra enchantment this session; lets hot paths skip all work otherwise.
    public static bool EverUsed { get; private set; }

    // The extra most recently added or increased on a card, for the enchant animation; cleared when the slot changes.
    public static readonly ConditionalWeakTable<CardModel, EnchantmentModel> LastChanged = new();

    public static string Signature(CardModel card) =>
        string.Join(';', Peek(card).Select(e => $"{e.Id.Entry}:{e.Amount}"));

    // The card's extra enchantments, without allocating a list for cards that have none.
    public static IReadOnlyList<EnchantmentModel> Peek(CardModel card) =>
        Table.TryGetValue(card, out var list) ? list : [];

    public static bool Any(CardModel card) => Table.TryGetValue(card, out var list) && list.Count > 0;

    public static List<EnchantmentModel> GetOrCreate(CardModel card)
    {
        EverUsed = true;
        if (Table.TryGetValue(card, out var list)) return list;
        list = [];
        Table.Add(card, list);
        ICloneableField.AddClonedField(card, Instance);
        return list;
    }

    public static void Add(CardModel card, EnchantmentModel enchantment, decimal amount, bool modifyCard = true)
    {
        GetOrCreate(card).Add(enchantment);
        enchantment.ApplyInternal(card, amount);
        if (modifyCard) enchantment.ModifyCard();
    }

    public static void Clear(CardModel card)
    {
        if (!Table.TryGetValue(card, out var list)) return;
        foreach (var enchantment in list) enchantment.ClearInternal();
        list.Clear();
    }

    // Like vanilla's clone of the main enchantment: the card's values already include the effects, so no ModifyCard.
    public void Clone(AbstractModel src, AbstractModel dst)
    {
        if (src is not CardModel srcCard || dst is not CardModel dstCard) return;
        var extras = Peek(srcCard);
        if (extras.Count == 0) return;

        var copies = new List<EnchantmentModel>(extras.Count);
        foreach (var extra in extras)
        {
            var copy = (EnchantmentModel)extra.ClonePreservingMutability();
            copy.ApplyInternal(dstCard, extra.Amount);
            copies.Add(copy);
        }
        Table.AddOrUpdate(dstCard, copies);
        ICloneableField.AddClonedField(dstCard, Instance);
    }
}
