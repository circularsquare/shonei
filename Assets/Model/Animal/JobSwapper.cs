// Automatic job swapping: when an idle animal has skills that better match
// another idle animal's job (and vice versa), they swap jobs.
//
// Scoring: for each (animal, job) pair, compute sum(skillWeight[skill] * skillLevel[skill]).
// A swap is beneficial when the combined score improves for both animals.
//
// Only idle animals are considered as swap partners — SetJob interrupts the current
// task via Refresh(), so swapping with a busy mouse would throw away in-progress work.

using UnityEngine;
using System.Collections.Generic;

public static class JobSwapper {
    // How well an animal's skill levels match a job's skill weight profile.
    // Returns sum of (weight * level) across all weighted skills. 0 if job has no weights.
    public static float WeightedScore(SkillSet skills, Job job) {
        if (job.resolvedSkillWeights == null) return 0f;
        float score = 0f;
        foreach (var kv in job.resolvedSkillWeights)
            score += kv.Value * skills.GetLevel(kv.Key);
        return score;
    }

    // Finds the best beneficial job swap for an idle animal and executes it.
    // Returns true if a swap was performed.
    public static bool TrySwap(Animal idle) {
        AnimalController ac = AnimalController.instance;
        if (ac == null) return false;
        if (idle.job == null) return false;

        float bestGain = 0f;
        Animal bestPartner = null;

        for (int i = 0; i < ac.na; i++) {
            Animal other = ac.animals[i];
            if (other == idle) continue;
            if (other.job == null) continue;
            if (other.job.id == idle.job.id) continue; // same job, no benefit
            // Only swap with other idle animals — avoid interrupting productive work.
            if (other.state != Animal.AnimalState.Idle) continue;
            if (other.task != null) continue; // defensive: idle should imply no task

            float currentScore = WeightedScore(idle.skills, idle.job)
                               + WeightedScore(other.skills, other.job);
            float swappedScore = WeightedScore(idle.skills, other.job)
                               + WeightedScore(other.skills, idle.job);
            float gain = swappedScore - currentScore;

            if (gain > bestGain) {
                bestGain = gain;
                bestPartner = other;
            }
        }

        if (bestPartner != null) {
            Job idleJob = idle.job;
            Job partnerJob = bestPartner.job;
            idle.SetJob(partnerJob);
            bestPartner.SetJob(idleJob);
            Debug.Log($"Job swap: {idle.aName} ({idleJob.name}\u2192{partnerJob.name}) <-> " +
                      $"{bestPartner.aName} ({partnerJob.name}\u2192{idleJob.name}), gain={bestGain:F2}");
            return true;
        }
        return false;
    }
}
