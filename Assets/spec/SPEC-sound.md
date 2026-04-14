# Sound System

`SoundManager` (`Assets/Controller/SoundManager.cs`) — MonoBehaviour singleton. Attach to a scene GameObject; self-creates `sfxSource` (one-shots via `PlayOneShot`) and `ambientSource` (`loop = true`, per-frame volume). Inspector volumes: `sfxVolume`, `ambientVolume`.

Clips are `Resources.Load<AudioClip>`-ed on first use and cached:
- `Resources/Audio/SFX/` — one-shots (e.g. `click.wav`)
- `Resources/Audio/Ambient/` — loops (e.g. `rain.wav`)

**API**: `SoundManager.instance.PlaySFX("clipName")`.

**Rain**: ambient volume driven each frame by `WeatherSystem.rainAmount` with a quadratic curve (`rain² × ambientVolume`) so audio fades in sync with particle visuals.

**Adding**: new SFX = drop file + call `PlaySFX`. New ambient loop = add a second AudioSource following the rain pattern.
