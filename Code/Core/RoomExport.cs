using System.Collections;
using System.Collections.Generic;
using SpireCodex.Producer;

namespace SpireCodex.Core;

internal static class RoomExport
{
    public static (EventInfo? Event, ShopInfo? Shop, RestInfo? RestSite) Read(object? state)
    {
        var room = Reflect.GetMember(state, "CurrentRoom");
        if (room == null) return (null, null, null);

        return room.GetType().Name switch
        {
            "EventRoom" => (ReadEvent(room), null, null),
            "MerchantRoom" => (null, ReadShop(room), null),
            "RestSiteRoom" => (null, null, ReadRest(room)),
            _ => (null, null, null),
        };
    }

    private static RestInfo? ReadRest(object room)
    {
        if (Reflect.GetMember(room, "Options") is not IEnumerable opts) return null;

        var options = new List<RestOptionInfo>();
        foreach (var opt in opts)
        {
            if (opt == null) continue;
            var id = Reflect.GetString(opt, "OptionId");
            if (string.IsNullOrEmpty(id)) continue;
            options.Add(new RestOptionInfo(
                id!, LocText(Reflect.GetMember(opt, "Title")), Reflect.GetBool(opt, "IsEnabled", true)));
        }
        return options.Count > 0 ? new RestInfo(options) : null;
    }

    private static EventInfo? ReadEvent(object room)
    {
        var live = Reflect.GetMember(room, "LocalMutableEvent");
        var idSource = live ?? Reflect.GetMember(room, "CanonicalEvent");
        var id = Ids.Bare(Reflect.GetString(room, "ModelId"))
                 ?? Ids.Bare(Reflect.GetString(idSource, "Id"));
        if (id == null) return null;

        var options = new List<EventOptionInfo>();
        if (Reflect.GetMember(live, "CurrentOptions") is IEnumerable opts)
        {
            foreach (var opt in opts)
            {
                if (opt == null) continue;
                var key = Reflect.GetString(opt, "TextKey") ?? "";
                var text = LocText(Reflect.GetMember(opt, "Title")) ?? key;
                options.Add(new EventOptionInfo(
                    key, text,
                    LocText(Reflect.GetMember(opt, "Description")),
                    OptionCard(opt),
                    Ids.Bare(Reflect.GetString(Reflect.GetMember(opt, "Relic"), "Id")),
                    Reflect.GetBool(opt, "IsLocked"),
                    Reflect.GetBool(opt, "IsProceed"),
                    Reflect.GetBool(opt, "WasChosen")));
            }
        }

        return new EventInfo(
            id,
            LocText(Reflect.GetMember(live, "Title")),
            LocText(Reflect.GetMember(live, "Description")),
            options);
    }

    internal static string? OptionCard(object opt)
    {
        if (Reflect.GetMember(opt, "HoverTips") is not IEnumerable tips) return null;
        foreach (var t in tips)
        {
            if (t == null || t.GetType().Name != "CardHoverTip") continue;
            var id = Ids.Bare(Reflect.GetString(Reflect.GetMember(t, "Card"), "Id"));
            if (!string.IsNullOrEmpty(id)) return id;
        }
        return null;
    }

    private static ShopInfo? ReadShop(object room)
    {
        var inv = Reflect.Call(room, "GetLocalInventory") ?? FirstInventory(room);
        if (inv == null) return null;

        var cards = new List<ShopItemInfo>();
        AddCards(Reflect.GetMember(inv, "CharacterCardEntries"), cards, "character");
        AddCards(Reflect.GetMember(inv, "ColorlessCardEntries"), cards, "colorless");

        var relics = new List<ShopItemInfo>();
        AddModelItems(Reflect.GetMember(inv, "RelicEntries"), relics);
        var potions = new List<ShopItemInfo>();
        AddModelItems(Reflect.GetMember(inv, "PotionEntries"), potions);

        ShopRemovalInfo? removal = null;
        if (Reflect.GetMember(inv, "CardRemovalEntry") is { } rm)
            removal = new ShopRemovalInfo(Reflect.GetInt(rm, "Cost"), Reflect.GetBool(rm, "IsStocked"));

        return new ShopInfo(cards, relics, potions, removal);
    }

    private static object? FirstInventory(object room)
    {
        if (Reflect.GetMember(room, "Inventories") is IEnumerable list)
            foreach (var i in list)
                if (i != null) return i;
        return null;
    }

    private static void AddCards(object? entries, List<ShopItemInfo> into, string slot)
    {
        if (entries is not IEnumerable list) return;
        foreach (var e in list)
        {
            if (e == null) continue;
            var stocked = Reflect.GetBool(e, "IsStocked");
            var id = stocked
                ? Ids.Bare(Reflect.GetString(Reflect.GetMember(Reflect.GetMember(e, "CreationResult"), "Card"), "Id"))
                : null;
            into.Add(new ShopItemInfo(id, Reflect.GetInt(e, "Cost"), stocked, Reflect.GetBool(e, "IsOnSale"), slot));
        }
    }

    private static void AddModelItems(object? entries, List<ShopItemInfo> into)
    {
        if (entries is not IEnumerable list) return;
        foreach (var e in list)
        {
            if (e == null) continue;
            var stocked = Reflect.GetBool(e, "IsStocked");
            var id = stocked ? Ids.Bare(Reflect.GetString(Reflect.GetMember(e, "Model"), "Id")) : null;
            into.Add(new ShopItemInfo(id, Reflect.GetInt(e, "Cost"), stocked, false, null));
        }
    }

    private static readonly HashSet<string> _unresolvable = new();

    private static string? LocText(object? locString)
    {
        if (locString == null) return null;

        var table = Reflect.GetString(locString, "LocTable");
        var key = Reflect.GetString(locString, "LocEntryKey");
        var id = table != null && key != null ? table + "|" + key : null;
        if (id != null)
        {
            lock (_unresolvable)
                if (_unresolvable.Contains(id)) return null;
        }

        var s = Reflect.CallString(locString, "GetFormattedText");
        if (!string.IsNullOrWhiteSpace(s)) return s;

        if (id != null)
            lock (_unresolvable)
                _unresolvable.Add(id);
        return null;
    }
}
