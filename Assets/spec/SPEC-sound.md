# Sound System

## Overview

`SoundManager` (`Assets/Controller/SoundManager.cs`) is a MonoBehaviour singleton that handles all game audio. Attach it to a GameObject in the scene — it self-creates two AudioSources in Awake.

## Audio Sources

| Source | Purpose | Mode |
|--------|---------|------|
| `sfxSource` | One-shot effects (UI clicks, placement) | `PlayOneShot` |
| `ambientSource` | Looping background audio (rain) | `loop = true`, volume driven per-frame |

Volume knobs `sfxVolume` and `ambientVolume` are exposed in the Inspector.

## Clip Loading

Clips are loaded via `Resources.Load<AudioClip>()` on first use and cached in a dictionary. Place audio files under:

```
Assets/Resources/Audio/
  SFX/         one-shots (e.g. click.wav)
  Ambient/     loops (e.g. rain.wav)
```

## API

- **`SoundManager.instance.PlaySFX(string clipName)`** — plays `Resources/Audio/SFX/{clipName}` as a one-shot.

## Rain Ambient

Rain volume is driven each frame by `WeatherSystem.rainAmount` with a **quadratic curve** (`rain * rain * ambientVolume`) so the audio fades out in sync with the particle visuals rather than lingering.

## Adding New Sounds

- **New SFX**: drop the clip in `Resources/Audio/SFX/`, call `PlaySFX("clipName")` at the trigger site.
- **New ambient loop**: add a new AudioSource + update method following the rain pattern. Consider whether it needs its own volume curve.
