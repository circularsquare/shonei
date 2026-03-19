using System.Collections;
using UnityEngine;

/// <summary>
/// Simulates and renders a cellular-automaton water fluid system.
/// Water is stored as a byte (0–16) on each Tile. Each TickUpdate:
///   1. SimulateStep — water falls downward then spreads laterally (volume-preserving).
///   2. UpdateVisuals — syncs blue water quads on each tile to the current water level.
///
/// Called every 0.2 seconds from World.Update (same cadence as inventory tick).
/// </summary>
public class WaterController : MonoBehaviour {
    public static WaterController instance { get; private set; }

    // Flat array of water-quad GameObjects, indexed y * world.nx + x.
    private GameObject[] waterObjects;

    // Alternates each SimulateStep to prevent directional spread bias.
    private bool flipDir = false;

    void Awake() {
        if (instance != null) {
            Debug.LogError("WaterController: duplicate instance detected");
            return;
        }
        instance = this;
    }

    IEnumerator Start() {
        // Wait one frame so WorldController.Start() has created all tile.go references.
        yield return null;

        World world = World.instance;

        // Build a 1×1 blue semi-transparent sprite shared by all water quads.
        Texture2D tex = new Texture2D(1, 1);
        tex.filterMode = FilterMode.Point;
        tex.SetPixel(0, 0, new Color(0.1f, 0.4f, 0.9f, 0.55f));
        tex.Apply();
        // PPU = 1 so the sprite fills exactly 1 Unity unit at scale (1, 1, 1).
        Sprite waterSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);

        waterObjects = new GameObject[world.nx * world.ny];
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                GameObject go = new GameObject("Water_" + x + "_" + y);
                go.transform.SetParent(tile.go.transform, false);
                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = waterSprite;
                // Render above tile sprites (0) and normal maps, below structures and animals.
                sr.sortingOrder = 2;
                go.SetActive(false);
                waterObjects[y * world.nx + x] = go;
            }
        }

        // Sync visuals with any water that was already set (e.g. from world gen or save load).
        UpdateVisuals();
    }

    /// <summary>Called by World.Update every 0.2 seconds.</summary>
    public void TickUpdate() {
        SimulateStep();
        UpdateVisuals();
    }

    /// <summary>
    /// One cellular-automaton step.
    /// Pass 1 (bottom-to-top): pour water straight down into the tile below.
    /// Pass 2 (bottom-to-top, alternating L/R): equalize with one horizontal neighbor.
    /// Integer math guarantees volume conservation.
    /// </summary>
    private void SimulateStep() {
        World world = World.instance;

        // Pass 1 — Fall
        for (int y = 1; y < world.ny; y++) {
            for (int x = 0; x < world.nx; x++) {
                Tile tile = world.GetTileAt(x, y);
                if (tile.type.solid || tile.water == 0) continue;

                Tile below = world.GetTileAt(x, y - 1);
                if (below == null || below.type.solid) continue;

                int flow = Mathf.Min(tile.water, 16 - below.water);
                if (flow <= 0) continue;
                tile.water  -= (byte)flow;
                below.water += (byte)flow;
            }
        }

        // Pass 2 — Spread (one direction, alternates each tick)
        for (int y = 0; y < world.ny; y++) {
            int xStart = flipDir ? world.nx - 1 : 0;
            int xEnd   = flipDir ? -1            : world.nx;
            int xStep  = flipDir ? -1            : 1;

            for (int x = xStart; x != xEnd; x += xStep) {
                Tile tile = world.GetTileAt(x, y);
                if (tile.type.solid || tile.water == 0) continue;

                int nx = x + xStep; // neighbour in sweep direction
                if (nx < 0 || nx >= world.nx) continue;

                Tile neighbor = world.GetTileAt(nx, y);
                if (neighbor.type.solid) continue;

                int flow = (tile.water - neighbor.water) / 2;
                if (flow <= 0) continue;
                tile.water     -= (byte)flow;
                neighbor.water += (byte)flow;
            }
        }

        flipDir = !flipDir;
    }

    /// <summary>
    /// Syncs all water-quad transforms with current tile.water values.
    /// Uses bottom-anchored scaling: bottom edge stays at tile floor regardless of fill level.
    /// </summary>
    private void UpdateVisuals() {
        if (waterObjects == null) return;
        World world = World.instance;

        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                GameObject go = waterObjects[y * world.nx + x];
                if (go == null) continue;

                if (tile.water == 0) {
                    if (go.activeSelf) go.SetActive(false);
                    continue;
                }
                if (!go.activeSelf) go.SetActive(true);

                // h is the fraction of the tile filled (0–1).
                // localScale.y = h stretches the 1×1 sprite to the right height.
                // localPosition.y = -0.5 + h/2 bottom-anchors the quad at the tile floor.
                float h = tile.water / 16f;
                go.transform.localScale    = new Vector3(1f, h, 1f);
                go.transform.localPosition = new Vector3(0f, -0.5f + h / 2f, 0f);
            }
        }
    }

    /// <summary>
    /// Zeros all tile water and hides all water quads.
    /// Called from WorldController.ClearWorld() before regenerating the world.
    /// </summary>
    public void ClearWater() {
        World world = World.instance;
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                world.GetTileAt(x, y).water = 0;
            }
        }
        if (waterObjects == null) return;
        foreach (var go in waterObjects) {
            if (go != null && go.activeSelf) go.SetActive(false);
        }
    }
}
