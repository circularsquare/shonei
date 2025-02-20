using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

// this class handles tile sprites, and also places initial objects into world.
public class WorldController : MonoBehaviour
{
    public static WorldController instance {get; protected set;}
    public World world {get; protected set;}
    public Transform tilesTransform;

    Dictionary<Tile, GameObject> tileGameObjectMap;

    void Start() {
        if (instance != null) {
            Debug.LogError("there should only be one world controller");}
        instance = this;

        Application.runInBackground = true;
        world = this.gameObject.AddComponent<World>(); // add world

        tileGameObjectMap = new Dictionary<Tile, GameObject>();
        tilesTransform = transform.Find("Tiles");

        for (int x = 0; x < world.nx; x++){
            for (int y = world.ny - 1; y >= 0; y--){
                Tile tile = world.GetTileAt(x, y);
                    
                GameObject tile_go = new GameObject();
                tile_go.name = "Tile_" + x + "_" + y;
                tile_go.transform.position = new Vector3(tile.x, tile.y, 0);
                tile_go.transform.SetParent(tilesTransform);
                tileGameObjectMap.Add(tile, tile_go);
                tile.go = tile_go;
                
                SpriteRenderer tile_sr = tile_go.AddComponent<SpriteRenderer>();
                tile_sr.sortingOrder = 0;
                
                // remember that when you destroy tiles you'll need to unregister the callback
                // tile.RegisterCbTileTypeChanged((tile) => {OnTileTypeChanged(tile, tile_go);});
                tile.RegisterCbTileTypeChanged(OnTileTypeChanged);

                // world generating
                if (y < 4){
                    tile.type = Db.tileTypeByName["soil"];
                }
                if (y < 2){
                    tile.type = Db.tileTypeByName["stone"];
                }
            }
        }
        // world.GetTileAt(5, 4).type = Db.tileTypeByName["tree"];
        // world.GetTileAt(4, 4).type = Db.tileTypeByName["tree"];
        // world.GetTileAt(12, 4).type = Db.tileTypeByName["tree"];
        Plant plant1 = new Plant(Db.plantTypeByName["tree"], 12, 4);
        plant1.Mature();
        new Plant(Db.plantTypeByName["tree"], 5, 4);
        new Plant(Db.plantTypeByName["tree"], 8, 4);
        new Plant(Db.plantTypeByName["wheat"], 15, 4);
        new Plant(Db.plantTypeByName["wheat"], 16, 4);

        world.graph.Initialize();
    } 

    // updaes the gameobjects sprite when the tile data is changed
    void OnTileTypeChanged(Tile tile) {
        if (!tileGameObjectMap.ContainsKey(tile)){
            Debug.LogError("tile data is not in tile game object map!");
        }
        GameObject tile_go = tileGameObjectMap[tile];
        Sprite sprite;
        if (tile.type.name == "soil"){
            if (tile.y < world.ny - 1 && world.GetTileAt(tile.x, tile.y + 1).type != Db.tileTypes[0]){
                sprite = Resources.Load<Sprite>("Sprites/Tiles/dirt");
            } else {
                sprite = Resources.Load<Sprite>("Sprites/Tiles/grass");
            }
        }
        else { sprite = Resources.Load<Sprite>("Sprites/Tiles/" + tile.type.name); }
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Tiles/default");
        }
        tile_go.GetComponent<SpriteRenderer>().sprite = sprite;

        world.graph.UpdateNeighbors(tile.x, tile.y); // i think this is redundant. should laready be called
        // in structcontroller whenever a tile type changes?
    }

}
