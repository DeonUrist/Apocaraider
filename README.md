# Apocaraider

Smarter raiders and real gunfights for Apocalypter: real bullets, headshots and car-part damage; raiders that see, hear, remember
and search; pathing that knows camps, caves and buildings; and Gungirl, a female raider. The short player description is in NEXUS.md.

## Gungirl

A woman among the Flexa raiders. She shows up in the same camps and places as Flexa, carries the same guns, fights the
same way, and leaves her own corpse when she dies. A share of the Flexas the game spawns are Gungirls instead
(50 % by default). She and her corpse are named Gungirl in the game, stay Gungirls after a save and load, and her corpse
is labelled Gungirl when you look at it.

Only new spawns are affected: the Flexas already in your world stay as they are.

## Tracers

Every gun-wielding NPC (raiders, Coyotes and other humans) and your own guns now fire real bullets you can see: a bright
tracer line flies from the muzzle, takes time to arrive, and hits whatever is really in its way. Crossbows fire slower,
visible bolts. Tracers look the same by day and by night.

- Damage falls off with distance: full damage up to half the weapon's range, then down to nothing at full range
  (pistols 60 m, SMGs 70 m, rifles and machine guns 120 m, sniper rifles 250 m, shotguns 35 m, crossbows 90 m by default).
- Shotguns fire a spread of pellets that together do the gun's damage. NPC shotgun pellets do 1.7x the game's damage by default (`NpcShotgunDamage`), so a shotgunner up close is a real threat.
- NPC bullets see you as a realistic body (feet to neck, shoulder width) plus a head: the game's own player collider is a slim
  0.4 m capsule that made many visible hits miss. A headshot does 1.2x damage by default (the game had no headshots on you).
  One bullet hits one thing, then it's gone.
- A bullet that hits a vehicle part damages it (20 damage = 1 % of the part's condition by default, `VehicleDamagePer1`; wheels
  take `WheelDamageMultiplier` (5) times as much - from your melee weapons too - so shooting or slashing the wheels stops a car, and a wheel shot to 0 jumps off it (`WheelPopOff`); the game's own rule,
  which took a rifle round's full 23 off a part's condition, is replaced), and a bolted-on metal plate
  can be knocked off (20 % per hit by default).
- A hit on its target does exactly what the game's own hit does (blood/sparks, damage, armor, hit sounds), and a bullet
  that misses you and hits the world shows the same sparks and sound your own hits make (the game showed nothing for NPC
  misses). Any part of a car except its wheels throws extra sparks.
- Headshots on raiders work as in the game (their head capsule doubles the damage); the mod's bullets see the same
  bodies the game's own shots do.

## Hit feedback

Your hits show as damage numbers (`[Hud] DamageNumbers`: 0 off, 1 a list in the top right corner, 2 floating up from
the hit point; white = damage, red with a `!` = headshot, light blue = % of condition taken off a vehicle part) and a diagonal
hit marker flashes at the crosshair (`[Hud] HitMarker`, white, red for a headshot). Both can be turned off.

## NPC aim

Gunmen pace their fire by distance. They don't shoot at all beyond their gun's reach (the Tracers ranges) and keep
closing in until you are within 85 % of it (a few seconds of patience, then they fire anyway). Up to 5 m they aim and
spread as the game made them; every 5 m beyond that adds 0.25 s to the pause between their bursts and 10 % to their
spread. They also lead a moving target: they aim where you will be when the bullet gets there, as well as each of them
can guess (some raiders are better shots than others, and all of them misjudge your speed a little), so running in a
straight line is no longer safe at range, while a change of direction still throws them off. All of it is configurable.

## NPC movement

Humans and ground animals that have spotted you no longer run around like headless chickens. The game never turned
their bodies toward you: a chasing NPC ran straight ahead at full speed while random turns, a pair of 1.2 m bumper rays
and a "stuck" reflex (a spin and a hop) threw it about, and it reached you when the random walk happened to bring it
close. Now every NPC with a target has a brain: it turns at a limited rate (220 deg/s, and fires only once it actually faces you),
feels its way around rocks, cars and camp walls with a fan of rays, and backs out of a dead end instead of spinning.
Gunmen stop as soon as they have a line of sight on you within their gun's engage distance, stand - or kneel, half of the time - with the gun up and shoot from there
(now and then one decides to run at you for a few seconds), and move again when you break the line of sight or get out
of range; a gunman that gets stuck but can see you shoots from where it is. Melee NPCs run at you around things.
NPCs also notice you at once: the game's sensors looked only every so often, so a raider reacted a second or two after you
stepped into view. Melee NPCs path for real: their feelers are body-wide sweeps that see low rocks, posts, tyres and fence bars; when the straight line to you is
blocked they commit to one way around the obstacle and follow its edge instead of dithering into it, and if they get nowhere for a few
seconds they try the other side, then stop to rethink. Gunmen can get the same with `[Brain] ShooterPathing` (off by default).
Flyers (bats, wasps, Terror of the Night) and crews seated in Apocapatrol cars are left to the game until they bail out.
Every engaged NPC thinks every 0.1 s (`ScaleWithActors` thins that out when many are engaged), `ReactionTime` scales all its
waits, and NPCs farther than 150 m move the game's way. `[Brain] Enabled = false` restores the game's own movement.

## Senses

NPCs no longer see through the back of their heads. Each one looks with a 100-degree cone from its head, with a ray to your head or
body, 100 m in daylight and down to 5 m in full darkness (your flashlight gives you away at full range from any angle). They hear:
your gunshots (80 m for a pistol, up to 150 m for rifles and shotguns), each other's gunfire, a human's combat shout (it tells its
own faction within 15 m where you are and tells its enemies where it stands), explosions (grenades, blast lances, Blast Rats and
Blast Zombies, 150 m, like a gunshot), an item you throw (10 m around where it lands) and
your running engine (50 m for the weakest engines to 150 m for the strongest, half while idling, nothing when it's off). What an NPC
hears or saw becomes a "ghost", a remembered spot it goes to check; one event makes one ghost shared by everyone it alerted, and an
NPC keeps the more trustworthy knowledge (sight over a gunshot - or a hit, an explosion, a thrown item - over a shout, over an
engine; the newest of equal rank; newer news about the same person always wins, so a shot you fire pulls NPCs away from an older
shout about you). A shout is not a ghost of its own: it passes on what the shouter knows, what it sees (as sight) or the ghost it is going to, with that ghost's own rank; a spot an NPC already got is never sent to it again by a shout, and an enemy's shouts draw it only once per alert. An
NPC walking to its spot keeps going until it gets there; only when GhostTimeout (60 s) passes with no news about that spot does it
search from where it got. If it lost you from sight it first goes to where it last saw you and, finding nothing, follows to where you really
are one to three times (PursuitMin/Max). At the spot it looks around for 30 s and, seeing nothing, loses interest and idles where it stands. Being
shot tells it where that came from. The same rules run between NPC factions. A faction at peace with you (the Coyotes towns) ignores everything about you - your
shots, shouts, engine, thrown items, and raiders shooting or shouting at you - and only reacts to its own fights; the moment the
game turns it against you (you hit one of them) its NPCs see and hunt you like raiders. Ghosts and alert states are saved with the game and
restored after a load. You can shout too (Alt+Q, `[Senses] ShoutModifier` + `ShoutKey`; Alt keeps Q from kicking): you yell like a raider and NPCs hostile to you within
25 m come to check. With Apocapatrol installed, a raider bailing out of a car keeps its crew's knowledge (it fights if it sees you,
otherwise heads for where you were) and an exploding car is heard like a gunshot. `[Senses] Enabled = false` gives the game's own sensors back; `[Debug] ShowGhosts` draws every ghost and
alert NPC's state in the world.

## Structure maps

NPCs know their camps, buildings and caves. When you come within 200 m of one, it is mapped once in the background: a 0.5 m grid of
where a body fits (spikes at a cave mouth, braziers, crates and walls are obstacles; the clean opening is not). An NPC inside a
mapped structure takes the real way to you or to the spot it is checking: out through the exit that is shortest overall, around the
walls, instead of feeling its way and running into a dead end; when the map can't reach the spot it at least leads the NPC out into the
open (a cave mouth, a building's door) and the feelers take over. The surfaces the maps find to be floor (a cave's rock floor, a camp
deck) count as ground for the feelers too, as long as they are no steeper than about 37 degrees and no higher than a kerb (0.25 m) where the
body meets them; a lower step limit and a knee-low wall check keep routes off rock lips, and an NPC stuck on something low hops over it. Outside structures nothing changes. `[Nav] Enabled = false` turns it
off; `[Debug] ShowNav` shows the mapping state and each NPC's next map waypoint; the hidden `[Debug] NavDump` (code default off) saves the maps as pictures.

## Install

Needs BepInEx 5. Copy the `Apocaraider` folder into `BepInEx\plugins\`, so you have:

```
BepInEx\plugins\Apocaraider\Apocaraider.dll
BepInEx\plugins\Apocaraider\Models\Flexa_female.glb
BepInEx\plugins\Apocaraider\Models\flexa_female.png
BepInEx\plugins\Apocaraider\Sounds\Gungirl\*.wav / *.ogg
```

## Settings

`BepInEx\config\com.denis.apocalypter.apocaraider.cfg`, or in game in the Apocasetter Mods menu.

- **[General]** EnableHudEffects (damage numbers and hit marker), EnableGunplay (real bullets, falloff, headshots, NPC aim pacing,
  vehicle damage), EnableNpcDetection (sight, hearing, ghosts, search), EnableNpcPathfinding (steering, structure maps, shooting
  positions), EnableFemaleNpc (Gungirls among the raiders).
- **[Hud]** FloatingDamage (0 off, 1 list top right, 2 floating numbers), HitMarker.
- **[Gunplay]** Tracers (draw bullet trails), PlayerGunTracers (also for your own shots), BulletSpeed, BoltSpeed, HeadshotMultiplier
  (1.5: your bullets in an NPC's head, NPC bullets in yours and in each other's), VehicleDamage, WheelPopOff, NpcAim,
  AdjustHumanBossHP (40 %: Duke Ironjaw and Buzzgut).
- **[Detection]** PlayerShoutKey + PlayerShoutKeyModifier (Alt+Q), SightCone, SightRange, DarkSightRange.
- **[Pathfinding]** ScaleWithActors.
- **[Debug]** VerboseLog (every log of the mod), ShowNavigation (ghosts, NPC states
  and map waypoints drawn in the world).

Everything else (Gungirl looks and voice, tracer colours, weapon ranges, aim pacing, movement, hearing ranges, maps) is fixed in
the code (`Plugin.cs`, the `H(...)` lines): each can be put back in the config file by changing `H(` to `Config.Bind(`.

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
