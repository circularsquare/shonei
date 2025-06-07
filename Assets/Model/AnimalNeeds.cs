using UnityEngine;
using System;

public class AnimalNeeds {
    private Animal animal;
    public Eating eating;
    public Eeping eeping;

    public AnimalNeeds(Animal animal) {
        this.animal = animal;
        this.eating = new Eating();
        this.eeping = new Eeping();
    }

    public void Update() {
        eating.Update();
        eeping.Update();
        
        if (eating.Hungry()) {
            if (animal.inv.ContainsItem(Db.itemByName["wheat"])) {
                animal.Consume(Db.itemByName["wheat"], 1);
                eating.Eat(20f);
            } else {
                animal.deliveryTarget = Animal.DeliveryTarget.Self;
                animal.StartFetching(Db.itemByName["wheat"], 5);
            }
        } else if (eeping.Eepy() && animal.state != Animal.AnimalState.Eeping) {
            animal.GoToEep();
        }
    }

    public float GetEfficiency() {
        return eating.Efficiency() * eeping.Efficiency();
    }
}

public class Eating {
    public float maxFood = 100f;
    public float food = 90f;
    public float hungerRate = 0.5f;

    public Eating() { }
    
    public float Fullness() { return food / maxFood; }
    public bool Hungry() { return food / maxFood < 0.5f; }

    public float Efficiency() {
        if (Fullness() > 0.5f) {
            return 1f;
        } else {
            return Fullness() * 2f * 0.8f + 0.2f; // 20% at worst.
        }
    }

    public void Eat(float nFood) {
        food += nFood;
    }

    public void Update(float t = 1f) {
        food -= hungerRate * t;
        if (food < 0f) { food = 0f; }
    }
}

public class Eeping {
    public float maxEep = 100f;
    public float eep = 90f;
    public static float eepRate = 5f;
    public static float outsideEepRate = 2f;

    public Eeping() { }

    public bool Eepy() {
        return eep / maxEep < 0.5f;
    }

    public float Efficiency() {
        if (Eepness() > 0.5f) {
            return 1f;
        } else {
            return Eepness() * 2f * 0.8f + 0.2f; // 20% at worst.
        }
    }

    public float Eepness() { return eep / maxEep; }

    public void Eep(float t, bool atHome) {
        eep += (atHome ? eepRate : outsideEepRate) * t;
    }

    public void Update(float t = 1f) {
        eep -= 0.5f * t;
        if (eep < 0f) { eep = 0f; }
    }
} 