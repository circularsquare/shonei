using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

using AnimalState = Animal.AnimalState;

public class AnimalStateManager {

    private Animal animal;

    private Dictionary<AnimalState, Action> stateActions;
    private Dictionary<AnimalState, Action> onStateEnter;

    public AnimalStateManager(Animal animal) {
        this.animal = animal;
        InitializeStateActions();
    }

    private void InitializeStateActions() {
        stateActions = new Dictionary<AnimalState, Action> {
            { AnimalState.Idle, HandleIdle },
            { AnimalState.Working, HandleWorking },
            { AnimalState.Eeping, HandleEeping },
            // Walking, Fetching, and Delivering are handled in Update() since they need deltaTime
        };

        onStateEnter = new Dictionary<AnimalState, Action> {
            { AnimalState.Idle, () => animal.FindWork() },
            { AnimalState.Delivering, () => animal.StartDelivering() }, 
            //{ AnimalState.Fetching, () => animal.StartFetching() } // DON'T WANT THIS because want fetching to be recursive
        };
    }


    public void UpdateState() {
        if (stateActions.ContainsKey(animal.state)) {
            stateActions[animal.state].Invoke();
        }
    }

    public void OnStateEnter(AnimalState newState) {
        if (onStateEnter.ContainsKey(newState)) {
            onStateEnter[newState].Invoke();
        }
    }
    private void HandleIdle() {
        animal.objective = Animal.Objective.None;
        
        animal.FindWork();
        if (animal.state == AnimalState.Idle) {
            // Random walking when nothing else to do
            if (UnityEngine.Random.Range(0, 5) == 0) {
                animal.GoTo(animal.x + UnityEngine.Random.Range(-1, 2), animal.y);
            }
        }
    }

    private void HandleWorking() {
        // should recheck if worktile is what you expect? how to do that? 
        if (animal.workTile?.blueprint != null && 
                   animal.workTile.blueprint.state == Blueprint.BlueprintState.Constructing) {
            if (animal.workTile.blueprint.ReceiveConstruction(1f * animal.efficiency)){
                animal.state = AnimalState.Idle;// if finished
            }

        } else if (animal.recipe != null && animal.inv.ContainsItems(animal.recipe.inputs)
            && animal.AtWork()) {
            animal.Produce(animal.recipe);
        } else {
            animal.state = AnimalState.Idle;
        }
    }

    private void HandleEeping() {
        animal.eeping.Eep(1f, animal.AtHome());
        if (animal.eeping.eep >= animal.eeping.maxEep) {
            animal.state = AnimalState.Idle;
        }
        // reproduction! 
        if (animal.AtHome() && animal.homeTile.building.reserved < animal.homeTile.building.capacity 
            && animal.homeTile.building.reserved > 2) {
            if (animal.random.Next(0, 50) == 0) {
                AnimalController.instance.AddAnimal(animal.x, animal.y);
            }
        }
    }

    public void UpdateMovement(float deltaTime) {
        // Handle falling
        if (!animal.TileHere().node.standable) {
            animal.nav.Fall();
        }

        if (IsMovingState(animal.state)) {
            if (animal.target == null) {
                // Error handling for missing target
                Debug.LogError("movement target null! " + animal.state.ToString());
                animal.StartDropping();
                //animal.state = AnimalState.Idle;
            }
            else {
                bool done = animal.nav.Move(deltaTime);
                if (done && animal.SquareDistance(animal.x, animal.target.x, animal.y, animal.target.y) < 0.001f) {
                    // Arrived at destination
                    animal.x = animal.target.x;
                    animal.y = animal.target.y;
                    animal.go.transform.position = new Vector3(animal.x, animal.y, 0);

                    HandleArrival();
                }
                animal.sr.flipX = !animal.isMovingRight;
            }
        }
    }
    private bool IsMovingState(AnimalState state) {
        return state == AnimalState.Walking ||
               state == AnimalState.Fetching ||
               state == AnimalState.Delivering;
    }
    private void HandleArrival() {
        // Tile here = animal.TileHere();
        // Debug.Log($"Arrived: state={animal.state}, " + 
        //       $"here=({here.x},{here.y}), " +
        //       $"workTile=({animal.workTile?.x},{animal.workTile?.y}), " +
        //       $"equals={here == animal.workTile}");
        switch (animal.state) {
            case AnimalState.Walking:
                    // Check if we arrived at workTile or homeTile
                if (animal.objective == Animal.Objective.Construct){
                    animal.state = AnimalState.Working;
                } else if (animal.TileHere() == animal.workTile) {
                    if (animal.TileHere().building is Plant) { // work tile is plant 
                        Plant plant = animal.TileHere().building as Plant;
                        if (plant.harvestable) {
                            animal.Produce(plant.Harvest());
                            animal.workTile = null;
                        }
                        animal.state = AnimalState.Idle;
                    }
                    else { // worktile is not plant 
                        animal.state = AnimalState.Working;
                    }
                } else if (animal.TileHere() == animal.homeTile) {
                    Debug.Log("arrived home, going to eep");
                    animal.state = AnimalState.Eeping;
                } else {
                    animal.state = AnimalState.Idle;
                }
                break;
            case AnimalState.Fetching:
                animal.OnArrivalFetch();
                break;
            case AnimalState.Delivering:
                animal.OnArrivalDeliver();
                break;
        }
    }
}