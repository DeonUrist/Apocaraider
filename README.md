# Apocaraiders

New raiders for Apocalypter, built on the game's own enemies.

## Gungirl

A woman among the Flexa raiders. She shows up in the same camps and places as Flexa, carries the same guns, fights the
same way, and leaves her own corpse when she dies. A share of the Flexas the game spawns are Gungirls instead
(50 % by default). She and her corpse stay Gungirls after a save and load.

Only new spawns are affected: the Flexas already in your world stay as they are.

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
- **[Gungirl] HideParts**: Flexa's attachments she doesn't wear (default: the beard).
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
which wins over the .wav. Mono or stereo, any sample rate, any length. Files with other names are ignored. A missing file means she uses Flexa's
sound for that clip. Only Gungirls use these files: Flexa, the other raiders and the player keep their voices.
