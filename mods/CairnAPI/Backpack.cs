using System;
using UnityEngine;
using Cairn = Il2CppTheGameBakers.Cairn.UI;

namespace CairnAPI;

// Backpack-screen access gate. Vanilla blocks bag contents while climbing: BagInventorySection
// sets Allowed=false in OnOpening and turns on a "climbingInavailabilityMask" overlay (the
// "Backpack contents are not accessible while climbing" message). Unlock = force Allowed and
// hide that mask. Verified live (set Allowed + disable mask -> contents become interactable).
public static class Backpack
{
    // Unlock a single section: allow access and hide the climbing mask. Returns false if nothing
    // to do (null section). Safe to call repeatedly.
    public static bool Unlock(Cairn.BagInventorySection section)
    {
        if (section == null) return false;
        try
        {
            section.Allowed = true;
            var mask = section.climbingInavailabilityMask;
            if (mask != null) { mask.enabled = false; mask.gameObject.SetActive(false); }
            var img = section.climbingInavailabilityMaskImage;
            if (img != null) img.enabled = false;
            var text = section.climbingInavailabilityMaskText;
            if (text != null) text.gameObject.SetActive(false);
            return true;
        }
        catch (Exception) { return false; }
    }

    // Unlock every loaded BagInventorySection (active or not). Returns how many were touched.
    public static int UnlockAll()
    {
        int n = 0;
        var sections = Resources.FindObjectsOfTypeAll<Cairn.BagInventorySection>();
        if (sections == null) return 0;
        for (int i = 0; i < sections.Length; i++)
            if (Unlock(sections[i])) n++;
        return n;
    }
}
