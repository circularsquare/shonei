using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class WorldController : MonoBehaviour
{
    public static WorldController instance {get; protected set;}
    public World world {get; protected set;}

    Dictionary<Tile, GameObject> tileGameObjectMap;

    void Start() {
        if (instance != null) {
            Debug.LogError("there should only be one world controller");}
        instance = this;


        world = this.gameObject.AddComponent<World>(); // ad dworld

        tileGameObjectMap = new Dictionary<Tile, GameObject>();

        for (int x = 0; x < world.nx; x++){
            for (int y = world.ny - 1; y >= 0; y--){
                Tile tile_data = world.GetTileAt(x, y);
                    
                GameObject tile_go = new GameObject();
                tile_go.name = "Tile_" + x + "_" + y;
                tile_go.transform.position = new Vector3(tile_data.x, tile_data.y, 0);
                tile_go.transform.SetParent(this.transform, true);
                tileGameObjectMap.Add(tile_data, tile_go);
                tile_data.go = tile_go;
                
                SpriteRenderer tile_sr = tile_go.AddComponent<SpriteRenderer>();
                
                // remember that when you destroy tiles you'll need to unregister the callback
                // tile_data.RegisterCbTileTypeChanged((tile) => {OnTileTypeChanged(tile, tile_go);});
                tile_data.RegisterCbTileTypeChanged(OnTileTypeChanged);

                // world generating
                if (y < 2){
                    tile_data.type = Db.tileTypeByName["soil"];
                }
            }
        }
        world.GetTileAt(5, 2).type = Db.tileTypeByName["tree"];
        world.CalculateTileStandability();
    } 

    // updaes the gameobjects sprite when the tile data is changed
    void OnTileTypeChanged(Tile tile_data) {
        if (!tileGameObjectMap.ContainsKey(tile_data)){
            Debug.LogError("tile data is not in tile game object map!");
        }
        GameObject tile_go = tileGameObjectMap[tile_data];
        if (tile_data.type.name == "soil"){
            if (tile_data.y < world.ny - 1 && world.GetTileAt(tile_data.x, tile_data.y + 1).type != Db.tileTypes[0]){
                tile_go.GetComponent<SpriteRenderer>().sprite = Resources.Load<Sprite>("Sprites/Tiles/dirt");
            } else {
                tile_go.GetComponent<SpriteRenderer>().sprite = Resources.Load<Sprite>("Sprites/Tiles/grass");
            }
        }
        else if (tile_data.type.name == "empty" || tile_data.type.name == "structure"){
            tile_go.GetComponent<SpriteRenderer>().sprite = null;
        }
        else if (tile_data.type.name == "tree" || tile_data.type.name == "stone"){
            tile_go.GetComponent<SpriteRenderer>().sprite = Resources.Load<Sprite>("Sprites/Tiles/" + tile_data.type.ToString());
        }
        else{
            Debug.LogError("ontiletypechanged - unrecognized tile type");
        }
        world.CalculateTileStandability(tile_data.x, tile_data.y);
    }

}
