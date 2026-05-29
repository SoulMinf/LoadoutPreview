using LoadoutPreview.Cross;

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;

namespace LoadoutPreview
{
    public class LoadoutPreview : Mod { }

    public static class LoadoutReflectionHelper
    {
        private static readonly Dictionary<Type, List<FieldInfo>> _itemFieldCache = new();
        private static readonly Dictionary<Type, List<FieldInfo>> _boolFieldCache = new();

        public static List<FieldInfo> GetItemArrayFields(Type loadoutType)
        {
            if (_itemFieldCache.TryGetValue(loadoutType, out var cached))
                return cached;

            var result = new List<FieldInfo>();
            foreach (var f in loadoutType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (f.FieldType == typeof(Item[]))
                    result.Add(f);

            return _itemFieldCache[loadoutType] = result;
        }

        public static List<FieldInfo> GetBoolArrayFields(Type loadoutType)
        {
            if (_boolFieldCache.TryGetValue(loadoutType, out var cached))
                return cached;

            var result = new List<FieldInfo>();
            foreach (var f in loadoutType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (f.FieldType == typeof(bool[]))
                    result.Add(f);

            return _boolFieldCache[loadoutType] = result;
        }

        public static Item[] FindLoadoutArrayForPlayerArray(EquipmentLoadout loadout, Item[] playerArray, Player player)
        {
            var loadoutType = loadout.GetType();

            foreach (var lField in GetItemArrayFields(loadoutType))
            {
                string camelName = char.ToLowerInvariant(lField.Name[0]) + lField.Name[1..];

                var pField = typeof(Player).GetField(
                camelName,
                BindingFlags.Public | BindingFlags.Instance);

                if (pField?.FieldType != typeof(Item[]))
                    continue;

                var playerFieldValue = pField.GetValue(player) as Item[];
                if (ReferenceEquals(playerFieldValue, playerArray))
                    return lField.GetValue(loadout) as Item[];
            }

            return null;
        }
    }

    public class LoadoutPreviewSystem : ModSystem
    {
        private static readonly Dictionary<(int arrayId, int slot), Vector2> _slotPositions = new();
        private static readonly Dictionary<int, Item[]> _trackedArraysById = new();

        private static Hook _itemSlotDrawHook;
        private static Hook _drawLoadoutHook;
        private static int _visualLoadoutIndex = -1;
        public static int HoveredLoadoutIndex = -1;

        private static Rectangle? _activeHoverZone;

        public override void Load()
        {
            IL_Main.DrawInventory += IL_InjectFrameReset;
            SetupItemSlotDrawHook();
            SetupDrawLoadoutButtonsHook();
        }

        public override void Unload()
        {
            IL_Main.DrawInventory -= IL_InjectFrameReset;
            _itemSlotDrawHook?.Dispose();
            _drawLoadoutHook?.Dispose();
            _itemSlotDrawHook = null;
            _drawLoadoutHook = null;
            _slotPositions.Clear();
            _trackedArraysById.Clear();
        }

        private static void IL_InjectFrameReset(ILContext il)
        {
            var c = new ILCursor(il);
            c.Emit(OpCodes.Call, typeof(LoadoutPreviewSystem).GetMethod(nameof(BeginInventoryDraw), BindingFlags.Public | BindingFlags.Static)!);
        }

        public static void BeginInventoryDraw()
        {
            _slotPositions.Clear();
            _trackedArraysById.Clear();

            var player = Main.LocalPlayer;
            if (player == null) return;

            RegisterArray(player.armor);
            RegisterArray(player.dye);

            if (player.Loadouts is { Length: > 0 })
            {
                var loadoutType = player.Loadouts[0].GetType();
                foreach (var lField in LoadoutReflectionHelper.GetItemArrayFields(loadoutType))
                {
                    string camelName = char.ToLowerInvariant(lField.Name[0]) + lField.Name[1..];
                    var pField = typeof(Player).GetField(camelName, BindingFlags.Public | BindingFlags.Instance);

                    if (pField?.FieldType == typeof(Item[]))
                        RegisterArray(pField.GetValue(player) as Item[]);
                }
            }
        }

        private static void RegisterArray(Item[] arr)
        {
            if (arr != null)
                _trackedArraysById[RuntimeHelpers.GetHashCode(arr)] = arr;
        }
        private void SetupItemSlotDrawHook()
        {
            var method = typeof(ItemSlot)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "Draw") return false;
                    var p = m.GetParameters();
                    return p.Length >= 4
                        && p[0].ParameterType == typeof(SpriteBatch)
                        && p[1].ParameterType == typeof(Item[])
                        && p[2].ParameterType == typeof(int)
                        && p[3].ParameterType == typeof(int);
                });

            _itemSlotDrawHook = new Hook(method, OnItemSlotDrawArray);
        }

        private delegate void D_DrawArray(SpriteBatch sb, Item[] inv, int context, int slot, Vector2 position, Color lightColor);


        private static void OnItemSlotDrawArray(D_DrawArray orig, SpriteBatch sb, Item[] inv, int context, int slot, Vector2 position, Color lightColor)
        {
            bool previewing = HoveredLoadoutIndex >= 0 && HoveredLoadoutIndex != ExtraLoadouts.GetCurrentUnifiedIndex(Main.LocalPlayer);

            if (!previewing || !IsLoadoutEquipContext(context))
            {
                orig(sb, inv, context, slot, position, lightColor);
                return;
            }

            var player = Main.LocalPlayer;
            var loadout = ExtraLoadouts.GetLoadout(player, HoveredLoadoutIndex);
            if (loadout == null) { orig(sb, inv, context, slot, position, lightColor); return; }

            var previewArray = LoadoutReflectionHelper.FindLoadoutArrayForPlayerArray(loadout, inv, player);

            if (previewArray == null || slot >= previewArray.Length)
            {
                orig(sb, inv, context, slot, position, lightColor);
                return;
            }

            Item preview = previewArray[slot];
            Item backup = inv[slot];

            if (preview == null || preview.IsAir)
            {
                Item air = new Item();
                inv[slot] = air;
                orig(sb, inv, context, slot, position, lightColor);
                inv[slot] = backup;
                return;
            }

            inv[slot] = preview;
            orig(sb, inv, context, slot, position, lightColor);
            inv[slot] = backup;
        }

        private static bool IsLoadoutEquipContext(int context)
            => context == ItemSlot.Context.EquipArmor
            || context == ItemSlot.Context.EquipAccessory
            || context == ItemSlot.Context.EquipArmorVanity
            || context == ItemSlot.Context.EquipAccessoryVanity
            || context == ItemSlot.Context.EquipDye;

        private void SetupDrawLoadoutButtonsHook()
        {
            var method = typeof(Main).GetMethod("DrawLoadoutButtons", BindingFlags.NonPublic | BindingFlags.Static);
            _drawLoadoutHook = new Hook(method, OnDrawLoadoutButtons);
        }

        private delegate void D_DrawLoadout(int inventoryTop, bool demonHeart, bool masterMode);

        private static void OnDrawLoadoutButtons(D_DrawLoadout orig, int inventoryTop, bool demonHeart, bool masterMode)
        {
            int hovered = ComputeHoveredButtonIndex(inventoryTop);

            HoveredLoadoutIndex = hovered;
            orig(inventoryTop, demonHeart, masterMode);
        }

        private static int ComputeHoveredButtonIndex(int inventoryTop)
        {
            if (PlayerInput.IgnoreMouseInterface)
            {
                _activeHoverZone = null;
                return -1;
            }

            const int btnSize = 32;
            const int btnGap = 4;
            int btnX = Main.screenWidth - 40;
            Point mouse = Main.MouseScreen.ToPoint();

            int total = ExtraLoadouts.GetTotalCount();

            for (int i = 0; i < total; i++)
            {
                if (i == ExtraLoadouts.GetCurrentUnifiedIndex(Main.LocalPlayer))
                    continue;

                Rectangle btn = new Rectangle(
                    btnX,
                    inventoryTop + (btnSize + btnGap) * i,
                    btnSize, btnSize);

                if (btn.Contains(mouse))
                {
                    _activeHoverZone = new Rectangle(
                        btnX - 6,
                        inventoryTop + (btnSize + btnGap) * i - 6,
                        btnSize + 12,
                        btnSize + btnGap + 12);

                    _visualLoadoutIndex = i;
                    return i;
                }
            }

            if (_activeHoverZone.HasValue && _activeHoverZone.Value.Contains(mouse))
                return _visualLoadoutIndex;

            _activeHoverZone = null;
            _visualLoadoutIndex = -1;
            return -1;
        }
    }
}