# Apocaraiders

New raiders for Apocalypter, built on the game's own enemies, and better gunfights for everyone.

## Gungirl

A woman among the Flexa raiders. She shows up in the same camps and places as Flexa, carries the same guns, fights the
same way, and leaves her own corpse when she dies. A share of the Flexas the game spawns are Gungirls instead
(50 % by default). She and her corpse stay Gungirls after a save and load.

Only new spawns are affected: the Flexas already in your world stay as they are.

## Tracers

Every gun-wielding NPC (raiders, Coyotes and other humans) and your own guns now fire real bullets you can see: a bright
tracer line flies from the muzzle, takes time to arrive, and hits whatever is really in its way. Crossbows fire slower,
visible bolts. Tracers look the same by day and by night.

- Damage falls off with distance: half damage at half the weapon's range, gone at full range (pistols 60 m, SMGs 70 m,
  rifles and machine guns 120 m, sniper rifles 250 m, shotguns 35 m, crossbows 90 m by default).
- Shotguns fire a spread of pellets that together do the gun's damage.
- A bullet that hits a vehicle part damages it (10 damage = 1 % of the part's condition), and a bolted-on metal plate
  can be knocked off (20 % per hit by default).
- A hit on its target does exactly what the game's own hit does (blood/sparks, damage, armor, hit sounds).

## Install

Needs BepInEx 5. Copy the `Apocaraiders` folder into `BepInEx\plugins\`, so you have:

```
BepInEx\plugins\Apocaraiders\Apocaraiders.dll
BepInEx\plugins\Apocaraiders\Models\Flexa_female.glb
BepInEx\plugins\Apocaraiders\Models\flexa_female.png
BepInEx\plugins\Apocaraiders\Sounds\Gungirl\*.wav / *.ogg
```

## Settings

`BepInEx\config\com.denis.apocalypter.apocaraiders.cfg`, or in game in the Apocasetter Mods menu.

- **[General] Enabled**: new Gungirls appear among the raiders.
- **[Gungirl] Chance**: % of Flexa spawns that are Gungirls (0 to 100).
- **[Gungirl] Model / Texture**: her body model and texture in the mod folder. You can make your own: the model has to be
  rigged to Flexa's skeleton (see below).
- **[Gungirl] Voice**: the folder with her voice clips (see below).
- **[Gungirl] VoiceMatchLoudness / VoiceVolume**: her clips are brought to Flexa's loudness, then scaled by VoiceVolume (1 = as loud as Flexa).
- **[Gungirl] VoiceIntervalMin / VoiceIntervalMax**: the pause between her shouts in a fight, in seconds (default 2 to 8; Flexa uses 0.1 to 4).
- **[Gungirl] HideParts**: Flexa's attachments she doesn't wear (default: the beard).
- **[Tracers]**: on/off (NPCs and, separately, your own guns), bullet and bolt speed, colour, width, length and glow,
  the falloff range per weapon type, shotgun pellets and spread, vehicle damage on/off, the metal-plate chance, and the
  most bullets in flight at once.
- **[Debug] SpawnKey** (F9): spawns a Gungirl in front of you.

## Making your own body

The model is Flexa's body exported from the game, in glTF format, with Flexa's skeleton. Open it in Blender
(File > Import > glTF 2.0), reshape the body or repaint the texture, and keep the armature as it is: same bones,
same names, not moved. Export as glTF (.gltf or .glb) with Skinning on and point `[Gungirl] Model` at it.

## Her voice

`Sounds\Gungirl\` holds one sound file per sound she makes, named after the game's clip it replaces:

| File | When |
|---|---|
| `enemy_human_single_1.wav` ... `enemy_human_single_8.wav` | shouts while attacking (one picked at random) |
| `human_hurt.wav`, `human_hurt_2.wav` | getting hit |
| `death_1.wav`, `death_3.wav`, `death_6.wav` | dying |

Out of the box they are Flexa's own clips (WAV). Replace any of them with a recording of your own and restart the game:
either overwrite the .wav (8/16/24-bit PCM or 32-bit float), or put an .ogg (Ogg Vorbis) with the same name next to it,
which wins over the .wav. Mono or stereo, any sample rate, any length. Files with other names are ignored.
Your recordings don't need to be as loud as the game's: each one is matched to the loudness of the clip it replaces,
and `[Gungirl] VoiceVolume` makes her louder or quieter overall. A missing file means she uses Flexa's
sound for that clip. Only Gungirls use these files: Flexa, the other raiders and the player keep their voices.
