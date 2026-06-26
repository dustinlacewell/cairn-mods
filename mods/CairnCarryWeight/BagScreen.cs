using CairnAPI;
using Il2Cpp;
using Il2CppCairn.UI;
using Il2CppTheGameBakers.Cairn.UI;
using UnityEngine;
using TheGameBakers = Il2CppTheGameBakers.Cairn;

namespace CairnCarryWeight;

// Shows the current carry weight while the bag/inventory screen (CharacterMenu) is open.
// Subscribes to Screen.OnMenuChanged; shows a ScreenPrompt on open, hides it on close.
// Updates the weight text each frame while the menu is open so changes from consuming
// items appear live.
//
// CharacterMenu is a MenuBehaviour on the Menu canvas: detect via menu.behaviour is CharacterMenu.
internal static class BagScreen
{
    private static ScreenPromptHandle _handle;
    private static bool _showing;

    internal static void Register()
    {
        CairnAPI.Screen.OnMenuChanged += OnMenuChanged;
    }

    private static void OnMenuChanged(Menu menu)
    {
        if (menu != null && menu.behaviour?.TryCast<CharacterMenu>() != null)
            Show();
        else
            Hide();
    }

    internal static void UpdateText()
    {
        if (!_showing || _handle == null || !_handle.Valid)
            return;

        ScreenPrompt.SetText(_handle, BuildText());
    }

    private static void Show()
    {
        Hide();

        if (!Core.Enabled.Value)
            return;

        _handle = ScreenPrompt.Show(BuildText());
        if (_handle != null && _handle.Valid)
        {
            ScreenPrompt.Move(_handle, new Vector2(0f, -300f));
            _showing = true;
        }
    }

    private static void Hide()
    {
        if (_handle != null && _handle.Valid)
            ScreenPrompt.Hide(_handle);
        _handle = null;
        _showing = false;
    }

    private static string BuildText()
    {
        var mgr = TheGameBakers.InventoryManager.Instance;
        float current = mgr != null ? mgr.GetInventoryWeight(StorageType.Bag) : 0f;
        float max = Inventory.MaxBagWeight();
        return max > 0f
            ? $"Carry Weight: {current:F1} / {max:F1}"
            : $"Carry Weight: {current:F1}";
    }
}
