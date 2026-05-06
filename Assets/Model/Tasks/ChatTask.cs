using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class ChatTask : Task {
    public override bool IsWork => false;
    public Animal partner;
    public Tile myTile; // tile this animal should stand on during chat
    public ChatTask(Animal animal, Animal partner) : base(animal) {
        this.partner = partner;
    }
    public override bool Initialize() {
        Tile partnerTile = partner.TileHere();
        if (partnerTile == null) return false;

        // Detect whether we're the initiator or the recruited partner.
        // The recruited partner's Initialize() is called second, after the initiator
        // already assigned us a ChatTask pointing back at them.
        bool isInitiator = !(partner.task is ChatTask ct && ct.partner == animal);

        if (isInitiator) {
            // Find a pair of horizontally adjacent tiles so the animals stand side by side.
            // Partner stays on (or near) their current tile; initiator takes the neighbor.
            Tile initiatorTile = FindAdjacentChatTile(partnerTile);
            if (initiatorTile != null) {
                myTile = initiatorTile;
                // Recruit partner — give them a reciprocal ChatTask
                if (partner.task == null && partner.state == Animal.AnimalState.Idle) {
                    var partnerChat = new ChatTask(partner, animal);
                    partnerChat.myTile = partnerTile;
                    partner.task = partnerChat;
                    partner.task.Start();
                }
                objectives.AddLast(new GoObjective(this, initiatorTile));
            } else {
                // Fallback: no horizontal neighbor available, walk to partner's tile
                if (!animal.nav.WithinRadius(animal.nav.PathTo(partnerTile), MediumFindRadius)) return false;
                if (partner.task == null && partner.state == Animal.AnimalState.Idle) {
                    partner.task = new ChatTask(partner, animal);
                    partner.task.Start();
                }
                objectives.AddLast(new GoObjective(this, partnerTile));
            }
        } else if (myTile != null && myTile != animal.TileHere()) {
            // Partner was assigned a tile by the initiator — walk there if not already on it
            objectives.AddLast(new GoObjective(this, myTile));
        }

        objectives.AddLast(new ChatObjective(this, partner, 10));
        return true;
    }

    // Try horizontal neighbors of partnerTile (left and right). Return the one the
    // initiator can path to, preferring the shorter path. Returns null if neither works.
    private Tile FindAdjacentChatTile(Tile partnerTile) {
        World w = animal.world;
        Tile left  = w.GetTileAt(partnerTile.x - 1, partnerTile.y);
        Tile right = w.GetTileAt(partnerTile.x + 1, partnerTile.y);

        Path pathLeft  = (left  != null && left.node.standable)  ? animal.nav.PathTo(left)  : null;
        Path pathRight = (right != null && right.node.standable) ? animal.nav.PathTo(right) : null;

        // Reject paths that blow past the medium radius — a chat shouldn't require a cross-map hike.
        if (!animal.nav.WithinRadius(pathLeft,  Task.MediumFindRadius)) pathLeft  = null;
        if (!animal.nav.WithinRadius(pathRight, Task.MediumFindRadius)) pathRight = null;

        if (pathLeft != null && pathRight != null)
            return pathLeft.cost <= pathRight.cost ? left : right;
        if (pathLeft != null) return left;
        if (pathRight != null) return right;
        return null;
    }
    public override void Complete() {
        // Social satisfaction is granted gradually per-tick in HandleChatting — no lump grant here.
        base.Complete();
    }
    public override void Cleanup() {
        // If partner hasn't entered the chat phase yet, force-fail them.
        // If they're already chatting, HandleChatting will detect our departure.
        Animal p = partner;
        partner = null;
        if (p?.task is ChatTask pt && pt.partner == animal) {
            bool partnerChatting = p.state == Animal.AnimalState.Leisuring
                && p.task.currentObjective is ChatObjective;
            if (!partnerChatting) {
                pt.partner = null;
                p.task.Fail();
            }
        }
        base.Cleanup();
    }
}
