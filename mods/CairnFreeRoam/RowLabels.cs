using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using TGB = Il2CppTheGameBakers.Cairn;
using CairnUI = Il2CppTheGameBakers.Cairn.UI;

namespace CairnFreeRoam;

/// <summary>
/// Paints each custom bookmark's name onto its row in the eagle-eye list. The row's name TMP is the
/// "Text (TMP) (2)" element; vanilla it shows the warp point's localized locKey, which our points lack
/// (it resolves to "[none_string]"). We write the stored label directly and disable the cell's LocalizedText
/// so it is not overwritten on the next refresh. During a rename, the row shows the live edit buffer with a
/// caret instead of the stored name.
/// </summary>
public static class RowLabels
{
    private const string NameCell = "Text (TMP) (2)";

    /// <summary>Stamp stored names onto every custom bookmark row. Call after the list (re)builds.</summary>
    public static void Apply(CairnUI.FreeRoamEagleEyeWarpPointListUI list, BookmarkStore store)
    {
        if (list == null) return;
        var rows = list.activeWarpPoints;
        if (rows == null) return;
        for (int i = 0; i < rows.Count; i++)
        {
            var wp = rows[i].WarpPoint;
            var data = store.DataFor(wp);
            if (data != null) SetRowText(rows[i], data.Label);
        }
    }

    /// <summary>Show the live edit buffer (with caret) on the row that owns <paramref name="wp"/>.</summary>
    public static void ApplyEditing(
        CairnUI.FreeRoamEagleEyeWarpPointListUI list, TGB.FreeRoamWarpPoint wp, string buffer)
    {
        if (list == null || wp == null) return;
        var rows = list.activeWarpPoints;
        if (rows == null) return;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].WarpPoint != null && rows[i].WarpPoint.Pointer == wp.Pointer)
            {
                SetRowText(rows[i], buffer + "▏"); // ▏ caret
                return;
            }
        }
    }

    private static void SetRowText(CairnUI.FreeRoamEagleEyeWarpPointUI row, string text)
    {
        var tmps = row.gameObject.GetComponentsInChildren<TMP_Text>(true);
        if (tmps == null) return;
        for (int i = 0; i < tmps.Length; i++)
        {
            if (tmps[i].gameObject.name != NameCell) continue;
            var loc = tmps[i].GetComponent<LocalizedText>();
            if (loc != null) loc.enabled = false;
            tmps[i].text = text;
            return;
        }
    }
}
