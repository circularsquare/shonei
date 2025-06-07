using UnityEngine;
using System;
using System.Collections.Generic;

public class AnimalStateMachine {
    private Animal animal;
    private Dictionary<Animal.AnimalState, Action> stateActions;
    private Dictionary<Animal.AnimalState, Action> onStateEnter;

    public AnimalStateMachine(Animal animal) {
        this.animal = animal;
        InitializeStateActions();
    }

    private void InitializeStateActions() {
        stateActions = new Dictionary<Animal.AnimalState, Action> {
            { Animal.AnimalState.Idle, HandleIdle },
            { Animal.AnimalState.Working, HandleWorking },
            { Animal.AnimalState.Eeping, HandleEeping },
            // Walking, Fetching, and Delivering are handled in Update() since they need deltaTime
        };

        onStateEnter = new Dictionary<Animal.AnimalState, Action> {
            { Animal.AnimalState.Idle, () => animal.FindWork() },
            { Animal.AnimalState.Delivering, () => animal.StartDelivering() },
        };
    }

    public void UpdateState() {
        if (stateActions.ContainsKey(animal.state)) {
            stateActions[animal.state].Invoke();
        }
    }

    public void OnStateEnter(Animal.AnimalState newState) {
        if (onStateEnter.ContainsKey(newState)) {
            onStateEnter[newState].Invoke();
        }
    }

    private void HandleIdle() {
        animal.objective = Animal.Objective.None;
        
        animal.FindWork();
        if (animal.state == Animal.AnimalState.Idle) {
            // Random walking when nothing else to do
            if (UnityEngine.Random.Range(0, 5) == 0) {
                animal.GoTo(animal.x + UnityEngine.Random.Range(-1, 2), animal.y);
            }
        }
    }

    private void HandleWorking() {
        if (animal.workTile?.blueprint != null && 
            animal.workTile.blueprint.state == Blueprint.BlueprintState.Constructing) {
            if (animal.workTile.blueprint.ReceiveConstruction(1f * animal.efficiency)) {
                animal.state = Animal.AnimalState.Idle;
            }
        } else if (animal.recipe != null && animal.inv.ContainsItems(animal.recipe.inputs)
            && animal.AtWork()) {
            animal.Produce(animal.recipe);
        } else {
            animal.state = Animal.AnimalState.Idle;
        }
    }

    private void HandleEeping() {
        animal.eeping.Eep(1f, animal.AtHome());
        if (animal.eeping.eep >= animal.eeping.maxEep) {
            animal.state = Animal.AnimalState.Idle;
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
                Debug.LogError("movement target null! " + animal.state.ToString());
                animal.StartDropping();
            } else {
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

    private bool IsMovingState(Animal.AnimalState state) {
        return state == Animal.AnimalState.Walking ||
               state == Animal.AnimalState.Fetching ||
               state == Animal.AnimalState.Delivering;
    }

    private void HandleArrival() {
        switch (animal.state) {
            case Animal.AnimalState.Walking:
                if (animal.objective == Animal.Objective.Construct) {
                    animal.state = Animal.AnimalState.Working;
                } else if (animal.TileHere() == animal.workTile) {
                    if (animal.TileHere().building is Plant) {
                        Plant plant = animal.TileHere().building as Plant;
                        if (plant.harvestable) {
                            animal.Produce(plant.Harvest());
                            animal.workTile = null;
                        }
                        animal.state = Animal.AnimalState.Idle;
                    } else {
                        animal.state = Animal.AnimalState.Working;
                    }
                } else if (animal.TileHere() == animal.homeTile) {
                    Debug.Log("arrived home, going to eep");
                    animal.state = Animal.AnimalState.Eeping;
                } else {
                    animal.state = Animal.AnimalState.Idle;
                }
                break;
            case Animal.AnimalState.Fetching:
                animal.OnArrivalFetch();
                break;
            case Animal.AnimalState.Delivering:
                animal.OnArrivalDeliver();
                break;
        }
    }
} 