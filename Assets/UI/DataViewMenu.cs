using UnityEngine;

// Data-views menu — the little map-button dropdown that switches the world data overlay
// (OverlayController). The map button toggles a dropdown panel; each entry in the panel
// turns on one data view. Selecting a view closes the dropdown; a left-click in the world
// dismisses the overlay itself (handled by OverlayController).
//
// Today the dropdown holds one entry (foot traffic). Adding a view is: author another button
// in the dropdown panel in the scene, then bind its OnClick to a new ShowX() here that calls
// the matching OverlayController.ShowX(). Soil moisture is the obvious next one — Tile.moisture
// already exists, so it only needs a FillSoilMoisture in OverlayController.
public class DataViewMenu : MonoBehaviour {
    [SerializeField] GameObject dropdown; // panel listing the data views; hidden until the map button is clicked

    void Awake() {
        if (dropdown != null) dropdown.SetActive(false);
    }

    // Bound to the map button's OnClick.
    public void ToggleDropdown() {
        if (dropdown == null) return;
        dropdown.SetActive(!dropdown.activeSelf);
    }

    void CloseDropdown() {
        if (dropdown != null) dropdown.SetActive(false);
    }

    // Bound to the "foot traffic" entry's OnClick.
    public void ShowFootTraffic() {
        OverlayController.instance?.ShowFootTraffic();
        CloseDropdown();
    }

    // Bound to the "soil moisture" entry's OnClick.
    public void ShowSoilMoisture() {
        OverlayController.instance?.ShowSoilMoisture();
        CloseDropdown();
    }
}
