using Terraria;
using Terraria.ModLoader;

namespace LoadoutPreview.Cross
{
    public static class ExtraLoadouts
    {
        public const int VanillaLoadoutCount = 3;

        private static Mod _mod;
        private static bool _checked;

        public static bool IsLoaded
        {
            get
            {
                if (_checked) return _mod != null;
                _checked = true;

                if (ModLoader.TryGetMod("ExtraLoadouts", out var mod) && mod.Call("AreWeCallYet.0") is bool b && b) _mod = mod;

                return _mod != null;
            }
        }

        public static void Reset() { _mod = null; _checked = false; }

        public static int GetCurrentUnifiedIndex(Player player)
        {
            if (IsLoaded)
            {
                int extra = (int)_mod.Call("CurrentExtraLoadoutIndex.0", player);
                if (extra >= 0)
                    return VanillaLoadoutCount + extra;
            }
            return player.CurrentLoadoutIndex;
        }

        public static int GetTotalCount()
        {
            if (IsLoaded)
                return VanillaLoadoutCount + (int)_mod.Call("TotalExtraLoadouts.0");
            return VanillaLoadoutCount;
        }

        public static EquipmentLoadout GetLoadout(Player player, int unifiedIndex)
        {
            if (unifiedIndex < VanillaLoadoutCount)
                return player.Loadouts[unifiedIndex];

            if (IsLoaded)
                return (EquipmentLoadout)_mod.Call("GetExtraLoadoutVanilla.0", player, unifiedIndex - VanillaLoadoutCount);

            return null;
        }
    }
}