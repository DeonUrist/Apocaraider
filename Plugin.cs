using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Apocaraiders
{
    // New raiders built on the game's own human enemies. Gungirl: a Flexa with a female body, voice and corpse.
    // Tracers: every gun-wielding human fires visible bullets / crossbow bolts with travel time, range falloff and vehicle hits.
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.apocaraiders";
        public const string NAME = "Apocaraiders";
        public const string VERSION = "0.13.5";

        internal static ManualLogSource Log;
        internal static string Dir;

        internal static ConfigEntry<bool> Enabled, VerboseLog, HitLog, GungirlVoiceMatch;
        internal static ConfigEntry<float> GungirlVoiceVolume, GungirlVoiceIntervalMin, GungirlVoiceIntervalMax;
        internal static ConfigEntry<int> GungirlChance;
        internal static ConfigEntry<string> GungirlModel, GungirlTexture, GungirlVoice, GungirlHideParts;
        internal static ConfigEntry<Key> SpawnKey;
        internal static ConfigEntry<bool> TracersEnabled, VehicleDamage, PlayerTracers, NpcAimAtBody, AimEnabled, HitMarker, LeadTargets, ImpactEffects, MetalSparks;
        internal static ConfigEntry<float> LeadAccuracy, LeadError, MaxLeadTime;
        internal static ConfigEntry<int> DamageNumbers, DamageFontSize, HitMarkerSize;
        internal static ConfigEntry<float> AimTimeScale, FacingTolerance, AimBaseDistance, AimDelayPer5m, SpreadPer5m, EngagePercent, EngagePatience, HoldRecheckMin, HoldRecheckMax;
        internal static ConfigEntry<float> BulletSpeed, BoltSpeed, TracerWidth, TracerLength, BoltWidth, BoltLength, TracerGlow;
        internal static ConfigEntry<Color> TracerColor, BoltColor;
        internal static ConfigEntry<float> PistolRange, SmgRange, RifleRange, SniperRange, ShotgunRange, CrossbowRange, ShotgunPelletSpread, FullDamageUntil, NpcShotgunDamage, VehicleDamagePer1, MetalSparksScale, MetalSheetPopChance, NpcHitRadius, PlayerBodyRadius, PlayerHeadRadius, HeadshotMultiplier;
        internal static ConfigEntry<int> ShotgunPellets, MaxTracers;
        internal static ConfigEntry<string> MetalSheetNames;
        internal static ConfigEntry<bool> BrainEnabled, DropCheck, BrainLog, AimPose, ShooterPathing, ScaleWithActors;
        internal static ConfigEntry<float> TurnRate, CrouchChance, SensorInterval, ReactionTime, FeelerLength, MeleeFeelerLength, FeelerAngle, AdvanceChance, AdvanceMin, AdvanceMax, StuckBackupSeconds, StuckMemorySeconds, MaxDistance;
        internal static ConfigEntry<int> FeelerCount, StuckGiveUpCount, PursuitMin, PursuitMax;
        internal static ConfigEntry<bool> SensesEnabled, MuffleSounds, SensesLog, ShowGhosts, BailOutAware;
        internal static ConfigEntry<float> SightCone, SightRange, DarkSightRange, DaylightIntensity, NoticeSeconds, LoseSeconds, SearchSeconds, GhostTimeout, LookInterval, ArriveDistance, MuffleFactor,
            ShotRangePistol, ShotRangeSmg, ShotRangeRifle, ShotRangeSniper, ShotRangeShotgun, ShotRangeCrossbow, TauntRange, EngineMinRange, EngineMaxRange, EngineMinHp, EngineMaxHp, EngineIdleFactor, ThrowRange, BailOutAwareRange, ExplosionRange, BlastRange, PlayerShoutRange, ShoutCooldown, ShoutVolume;
        internal static ConfigEntry<Key> ShoutKey, ShoutModifier;
        internal static ConfigEntry<string> ShoutBlocksButtons;
        internal static ConfigEntry<string> NpcShotRanges, HumanFactions, BlastPrefabs;
        internal static ConfigEntry<bool> NavEnabled, NavLog, ShowNav, NavDump;
        internal static ConfigEntry<float> NavBakeRange, NavCellSize, NavMargin, NavMaxStep, NavBakeBudgetMs, NavFieldSeconds;

        private static GameObject _runner;

        private void Awake()
        {
            Log = Logger;
            Dir = Path.GetDirectoryName(Info.Location);

            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
            Enabled = Config.Bind("General", "Enabled", true,
                "New Gungirls appear among the raiders. Off: no new ones; those already in the world keep their looks.");
            GungirlChance = Config.Bind("Gungirl", "Chance", 50, new ConfigDescription(
                "% of the Flexa raiders the game spawns that are Gungirls instead (same camps, same gear and fighting).",
                new AcceptableValueRange<int>(0, 100)));
            GungirlModel = Config.Bind("Gungirl", "Model", "Models/Flexa_female.glb",
                "Body model (.gltf or .glb), relative to the mod folder. Must be rigged to Flexa's skeleton (mixamorig bones). Read at game start.");
            GungirlTexture = Config.Bind("Gungirl", "Texture", "Models/flexa_female.png",
                "Body texture (PNG/JPG), relative to the mod folder. Read at game start.");
            GungirlVoice = Config.Bind("Gungirl", "Voice", "Sounds/Gungirl",
                "Folder with her voice, relative to the mod folder: WAV or OGG files named like Flexa's clips (death_1, human_hurt, enemy_human_single_1 ...; .ogg wins over .wav of the same name). A clip without a file keeps Flexa's sound. Read at game start.");
            GungirlVoiceMatch = Config.Bind("Gungirl", "VoiceMatchLoudness", true,
                "Bring each of her voice files to the loudness of the Flexa clip it replaces (quiet recordings get louder, a soft limiter stops distortion). Read at game start.");
            GungirlVoiceVolume = Config.Bind("Gungirl", "VoiceVolume", 1f, new ConfigDescription(
                "Her voice volume on top of that: 1 = as loud as Flexa, 0.5 = half, 2 = twice (louder than 1.5 starts to sound squashed). Read at game start.",
                new AcceptableValueRange<float>(0f, 4f)));
            GungirlVoiceIntervalMin = Config.Bind("Gungirl", "VoiceIntervalMin", 2f, new ConfigDescription(
                "In a fight she shouts, waits a random pause between VoiceIntervalMin and VoiceIntervalMax seconds, shouts again ... (Flexa: 0.1 to 4). Applies to Gungirls spawned or loaded after the change.",
                new AcceptableValueRange<float>(0f, 60f)));
            GungirlVoiceIntervalMax = Config.Bind("Gungirl", "VoiceIntervalMax", 8f, new ConfigDescription(
                "Longest pause between her shouts, seconds (Flexa: 4).",
                new AcceptableValueRange<float>(0f, 60f)));
            GungirlHideParts = Config.Bind("Gungirl", "HideParts", "beard, headband, armband",
                "Flexa's attachments to hide on a Gungirl, comma-separated name starts: beard, headband, armband, bag1, pouch1, armor2, machete.");
            TracersEnabled = Config.Bind("Tracers", "Enabled", true,
                "Gun-wielding NPCs (raiders, Coyotes ...) fire visible bullets with travel time instead of instant hits; crossbows fire visible bolts.");
            PlayerTracers = Config.Bind("Tracers", "PlayerGuns", true,
                "Your own guns follow the same rules: visible bullets with travel time, the same range falloff and vehicle-part hits.");
            BulletSpeed = Config.Bind("Tracers", "BulletSpeed", 250f, new ConfigDescription("Bullet speed, m/s.", new AcceptableValueRange<float>(20f, 2000f)));
            BoltSpeed = Config.Bind("Tracers", "BoltSpeed", 60f, new ConfigDescription("Crossbow bolt speed, m/s.", new AcceptableValueRange<float>(10f, 500f)));
            TracerColor = Config.Bind("Tracers", "TracerColor", new Color(1f, 0.78f, 0.35f, 1f), "Bullet tracer colour (RGBA hex). Unlit: same brightness day and night.");
            BoltColor = Config.Bind("Tracers", "BoltColor", new Color(0.85f, 0.72f, 0.5f, 1f), "Crossbow bolt colour (RGBA hex).");
            TracerGlow = Config.Bind("Tracers", "Glow", 1.5f, new ConfigDescription("Brightness multiplier of tracers and bolts.", new AcceptableValueRange<float>(0f, 8f)));
            TracerWidth = Config.Bind("Tracers", "TracerWidth", 0.1f, new ConfigDescription("Tracer line width, m.", new AcceptableValueRange<float>(0.005f, 0.5f)));
            TracerLength = Config.Bind("Tracers", "TracerLength", 4f, new ConfigDescription("Tracer line length, m.", new AcceptableValueRange<float>(0.1f, 30f)));
            BoltWidth = Config.Bind("Tracers", "BoltWidth", 0.04f, new ConfigDescription("Bolt line width, m.", new AcceptableValueRange<float>(0.005f, 0.5f)));
            BoltLength = Config.Bind("Tracers", "BoltLength", 0.8f, new ConfigDescription("Bolt line length, m.", new AcceptableValueRange<float>(0.1f, 5f)));
            PistolRange = Config.Bind("Tracers", "PistolRange", 60f, new ConfigDescription("Pistols/revolvers: damage falls off linearly with distance - half at 50 % of this range, the bullet is gone at 100 %. Metres.", new AcceptableValueRange<float>(5f, 1000f)));
            SmgRange = Config.Bind("Tracers", "SmgRange", 70f, new ConfigDescription("SMGs, falloff range in metres (half damage at half range).", new AcceptableValueRange<float>(5f, 1000f)));
            RifleRange = Config.Bind("Tracers", "RifleRange", 120f, new ConfigDescription("Automatic rifles and machine guns, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            SniperRange = Config.Bind("Tracers", "SniperRange", 250f, new ConfigDescription("Sniper/scoped rifles, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            ShotgunRange = Config.Bind("Tracers", "ShotgunRange", 35f, new ConfigDescription("Shotguns, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            CrossbowRange = Config.Bind("Tracers", "CrossbowRange", 90f, new ConfigDescription("Crossbows, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            ShotgunPellets = Config.Bind("Tracers", "ShotgunPellets", 3, new ConfigDescription("Pellets per vanilla shotgun ray (a vanilla blast is 4 rays; 3 = 12 pellets). The blast's damage is split between them.", new AcceptableValueRange<int>(1, 8)));
            ShotgunPelletSpread = Config.Bind("Tracers", "ShotgunPelletSpread", 1f, new ConfigDescription("Extra spread of each pellet, degrees.", new AcceptableValueRange<float>(0f, 15f)));
            FullDamageUntil = Config.Bind("Tracers", "FullDamageUntil", 50f, new ConfigDescription(
                "Every gun (NPC and yours) does full damage until this % of its range, then the damage falls off to 0 at the range. 0 = falloff from the muzzle (half damage at 50 %).",
                new AcceptableValueRange<float>(0f, 99f)));
            NpcShotgunDamage = Config.Bind("Tracers", "NpcShotgunDamage", 1.7f, new ConfigDescription(
                "Damage multiplier for NPC shotgun pellets (the game's shotgunners do 5-8 per ray x 4 rays per blast, a third of a rifle burst).",
                new AcceptableValueRange<float>(0f, 5f)));
            NpcHitRadius = Config.Bind("Tracers", "NpcHitRadius", 0.05f, new ConfigDescription(
                "Thickness of NPC bullets, m, added to the target's hitbox (your body/head shapes, other targets' colliders). 0 = hairline. Your own bullets are always a hairline.",
                new AcceptableValueRange<float>(0f, 0.5f)));
            NpcAimAtBody = Config.Bind("Tracers", "NpcAimAtBody", true,
                "NPCs aim at the centre of your body. Off = the game's own aim point, your head, which with the game's aim jitter sends many shots over your head.");
            PlayerBodyRadius = Config.Bind("Tracers", "PlayerBodyRadius", 0.22f, new ConfigDescription(
                "Your body as NPC bullets see it: a capsule from your feet to your neck with this radius, m (the game's own collider is only 0.17-0.20).",
                new AcceptableValueRange<float>(0.05f, 0.6f)));
            PlayerHeadRadius = Config.Bind("Tracers", "PlayerHeadRadius", 0.14f, new ConfigDescription(
                "Your head as NPC bullets see it: a sphere of this radius at the top of your body, m.",
                new AcceptableValueRange<float>(0.05f, 0.4f)));
            HeadshotMultiplier = Config.Bind("Tracers", "HeadshotMultiplier", 1.2f, new ConfigDescription(
                "Damage multiplier for an NPC bullet that hits your head (the game itself has no headshots on the player: 1 = as before).",
                new AcceptableValueRange<float>(0f, 5f)));
            ImpactEffects = Config.Bind("Tracers", "ImpactEffects", true,
                "NPC bullets that hit the world show the same impact (sparks, sound) your own hits do; the game showed nothing for their misses.");
            MetalSparks = Config.Bind("Tracers", "MetalSparks", true,
                "Extra sparks, smoke and a bullet mark on anything that is part of a car - attached parts, the body and frame, loose parts - except wheels (any bullet), using the game's MetalImpact effect.");
            MetalSparksScale = Config.Bind("Tracers", "MetalSparksScale", 0.25f, new ConfigDescription("Size of the metal sparks effect (1 = the prefab's own, demo-scene size).", new AcceptableValueRange<float>(0.05f, 4f)));
            VehicleDamage = Config.Bind("Tracers", "VehicleDamage", true, "A bullet that hits a vehicle part damages it by the VehicleDamagePer1 rule. Off = the game's own rule (your bullet's full damage comes straight off the part's condition: an akms round -23 %; NPC bullets never damage parts).");
            VehicleDamagePer1 = Config.Bind("Tracers", "VehicleDamagePer1", 20f, new ConfigDescription(
                "Bullet damage that takes 1 % off a vehicle part's condition (20 = a -23 rifle round costs 1.15 %, a shotgun pellet about 0.4 %).", new AcceptableValueRange<float>(1f, 1000f)));
            MetalSheetPopChance = Config.Bind("Tracers", "MetalSheetPopChance", 20f, new ConfigDescription("% chance that a bullet hitting a bolted-on metal plate knocks it off.", new AcceptableValueRange<float>(0f, 100f)));
            MetalSheetNames = Config.Bind("Tracers", "MetalSheetNames", "metal_plate", "Which attached parts count as metal sheets (comma-separated name starts).");
            MaxTracers = Config.Bind("Tracers", "MaxTracers", 300, new ConfigDescription("Most bullets in flight at once; shots above this hit instantly (vanilla style) instead.", new AcceptableValueRange<int>(16, 2000)));
            AimEnabled = Config.Bind("NpcAim", "Enabled", true,
                "NPC gunmen pace their fire by distance: no shots beyond the gun's reach (the [Tracers] ranges), slower aiming and a wider spread far away.");
            AimTimeScale = Config.Bind("NpcAim", "AimTimeScale", 50f, new ConfigDescription(
                "How long NPCs take to aim between bursts, as % of the game's own pause (3-5 s, plus the distance delay): 50 = half the time, 100 = as the game, 300 = three times slower.",
                new AcceptableValueRange<float>(1f, 300f)));
            FacingTolerance = Config.Bind("NpcAim", "FacingTolerance", 8f, new ConfigDescription(
                "An NPC fires only once its body faces you within this many degrees (it turns at [Brain] TurnRate); until then it keeps turning and checks again every 0.1-0.2 s.",
                new AcceptableValueRange<float>(0f, 90f)));
            AimBaseDistance = Config.Bind("NpcAim", "AimBaseDistance", 5f, new ConfigDescription(
                "Up to this distance, m, NPCs aim and spread as the game does; every 5 m beyond it adds AimDelayPer5m and SpreadPer5m.", new AcceptableValueRange<float>(0f, 200f)));
            AimDelayPer5m = Config.Bind("NpcAim", "AimDelayPer5m", 0.25f, new ConfigDescription(
                "Seconds added to the pause between an NPC's bursts for every 5 m the target is beyond AimBaseDistance.", new AcceptableValueRange<float>(0f, 5f)));
            SpreadPer5m = Config.Bind("NpcAim", "SpreadPer5m", 10f, new ConfigDescription(
                "% added to the NPC's aim spread for every 5 m the target is beyond AimBaseDistance.", new AcceptableValueRange<float>(0f, 100f)));
            EngagePercent = Config.Bind("NpcAim", "EngagePercent", 85f, new ConfigDescription(
                "NPCs open fire once the target is within this % of the gun's reach; farther away they keep closing in. Never beyond the reach itself.",
                new AcceptableValueRange<float>(1f, 100f)));
            EngagePatience = Config.Bind("NpcAim", "EngagePatience", 5f, new ConfigDescription(
                "Seconds an NPC within reach but beyond EngagePercent keeps closing in before it fires anyway (stuck, hiding...).", new AcceptableValueRange<float>(0f, 60f)));
            HoldRecheckMin = Config.Bind("NpcAim", "HoldRecheckMin", 1f, new ConfigDescription(
                "While holding fire (target too far), the NPC looks again after a random pause between HoldRecheckMin and HoldRecheckMax seconds: its reaction time once you come into reach.",
                new AcceptableValueRange<float>(0.1f, 30f)));
            LeadTargets = Config.Bind("NpcAim", "LeadTargets", true,
                "NPCs aim where a moving target will be when the bullet arrives (the game's hitscan never had to). Changing direction still beats them.");
            LeadAccuracy = Config.Bind("NpcAim", "LeadAccuracy", 0.75f, new ConfigDescription(
                "How much of the ideal lead NPCs apply: 1 = perfect prediction, 0 = none. Each NPC rolls a personal skill between half of this and all of it.",
                new AcceptableValueRange<float>(0f, 1f)));
            LeadError = Config.Bind("NpcAim", "LeadError", 30f, new ConfigDescription("Random error on the NPC's estimate of your speed, +/- %, per shot.", new AcceptableValueRange<float>(0f, 100f)));
            MaxLeadTime = Config.Bind("NpcAim", "MaxLeadTime", 1.5f, new ConfigDescription("Longest flight time NPCs lead for, s (bolts at long range).", new AcceptableValueRange<float>(0f, 5f)));
            HoldRecheckMax = Config.Bind("NpcAim", "HoldRecheckMax", 4f, new ConfigDescription("See HoldRecheckMin.", new AcceptableValueRange<float>(0.1f, 30f)));
            BrainEnabled = Config.Bind("Brain", "Enabled", true,
                "Humans and ground animals that have a target move with a brain: they run at you around obstacles (feelers) instead of the game's random swerving, gunmen stop where they can shoot and hold there, a stuck NPC backs out instead of spinning and hopping. Flyers and crews seated in cars (Apocapatrol) are untouched. Off = the game's own movement.");
            ReactionTime = Config.Bind("Brain", "ReactionTime", 100f, new ConfigDescription(
                "How quickly NPCs think and react, as % of the default: every wait of the brain (looks, backing out of a stuck, resting, keeping a way around an obstacle, rechecks while holding) is scaled by this. 50 = twice as quick, 500 = five times slower.",
                new AcceptableValueRange<float>(1f, 500f)));
            ScaleWithActors = Config.Bind("Brain", "ScaleWithActors", false,
                "NPCs think less often when many are engaged at once (0.1 s up to 5 of them, 0.2 s up to 10, 0.3 s up to 20, 0.5 s beyond), to spare the CPU. Off = every engaged NPC thinks every 0.1 s.");
            TurnRate = Config.Bind("Brain", "TurnRate", 220f, new ConfigDescription(
                "How fast an NPC turns its body, degrees per second - also to face you for a shot (the game snapped instantly).", new AcceptableValueRange<float>(30f, 720f)));
            AimPose = Config.Bind("Brain", "AimPose", true,
                "A gunman holding a shooting position keeps the gun up and aimed at you between bursts (the game lowered it to the idle pose and raised it only for the shot).");
            CrouchChance = Config.Bind("Brain", "CrouchChance", 50f, new ConfigDescription(
                "% chance that a gunman kneels when he takes a shooting position (humans only; he stands up when he moves again). He is harder to hit kneeling: his hitbox shrinks with him.",
                new AcceptableValueRange<float>(0f, 100f)));
            FeelerLength = Config.Bind("Brain", "FeelerLength", 3.5f, new ConfigDescription("How far ahead a moving NPC looks for obstacles, m.", new AcceptableValueRange<float>(1f, 10f)));
            SensorInterval = Config.Bind("Brain", "SensorInterval", 0.1f, new ConfigDescription(
                "How often an NPC's eyes look, s: the game's sensors pulse on a slow fixed interval, so an NPC noticed you a second or two after you came into view. 0 = the game's own interval.",
                new AcceptableValueRange<float>(0f, 5f)));
            MeleeFeelerLength = Config.Bind("Brain", "MeleeFeelerLength", 2.5f, new ConfigDescription("How far ahead a melee NPC looks, m (they turn quicker than a gunman needs).", new AcceptableValueRange<float>(1f, 10f)));
            ShooterPathing = Config.Bind("Brain", "ShooterPathing", false,
                "Gunmen on the move use the melee pathing too: body-wide feelers that see low rocks, posts and fence bars, a committed way around an obstacle, wall following. Off = the simpler rays of 0.7.0 (cheaper; they stop to shoot anyway).");
            FeelerAngle = Config.Bind("Brain", "FeelerAngle", 60f, new ConfigDescription("Half-angle of the feeler fan around the direction to the target, degrees.", new AcceptableValueRange<float>(15f, 120f)));
            FeelerCount = Config.Bind("Brain", "FeelerCount", 7, new ConfigDescription("Feeler rays per look (odd; fewer = cheaper, coarser).", new AcceptableValueRange<int>(3, 15)));
            DropCheck = Config.Bind("Brain", "DropCheck", true, "A moving NPC also checks for ground 1.5 m along its chosen direction and picks another when there is a drop (one extra ray).");
            AdvanceChance = Config.Bind("Brain", "AdvanceChance", 10f, new ConfigDescription(
                "A gunman holding a shooting position rolls this % at every hold recheck ([NpcAim] HoldRecheckMin..Max s) to run toward you for AdvanceMin..AdvanceMax s instead.", new AcceptableValueRange<float>(0f, 100f)));
            AdvanceMin = Config.Bind("Brain", "AdvanceMin", 2f, new ConfigDescription("Shortest advance, s.", new AcceptableValueRange<float>(0.5f, 20f)));
            AdvanceMax = Config.Bind("Brain", "AdvanceMax", 4f, new ConfigDescription("Longest advance, s.", new AcceptableValueRange<float>(0.5f, 20f)));
            StuckBackupSeconds = Config.Bind("Brain", "StuckBackupSeconds", 0.4f, new ConfigDescription("A stuck NPC backs up this long, s, before trying another way.", new AcceptableValueRange<float>(0.1f, 5f)));
            StuckMemorySeconds = Config.Bind("Brain", "StuckMemorySeconds", 3f, new ConfigDescription("How long the heading it got stuck on is avoided, s.", new AcceptableValueRange<float>(0f, 60f)));
            StuckGiveUpCount = Config.Bind("Brain", "StuckGiveUpCount", 3, new ConfigDescription("Stucks within 10 s after which the NPC stands still for a second (facing you) before trying again.", new AcceptableValueRange<int>(1, 20)));
            MaxDistance = Config.Bind("Brain", "MaxDistance", 150f, new ConfigDescription("NPCs farther than this from their target move the game's way (no cost).", new AcceptableValueRange<float>(20f, 1000f)));
            SensesEnabled = Config.Bind("Senses", "Enabled", true,
                "The mod's own detection replaces the game's: NPCs see with a cone from the head, worse in the dark, hear gunshots, taunts, thrown items and your engine, remember where you were and search there, and the alerts are shared and saved with the game. Off = the game's own sensors (a 240 deg look from the chest, no hearing).");
            SightCone = Config.Bind("Senses", "SightCone", 100f, new ConfigDescription("Width of an NPC's field of view, degrees (the game: 240).", new AcceptableValueRange<float>(10f, 360f)));
            SightRange = Config.Bind("Senses", "SightRange", 100f, new ConfigDescription("How far an NPC sees in daylight, m; also with your flashlight on, from any angle.", new AcceptableValueRange<float>(5f, 500f)));
            DarkSightRange = Config.Bind("Senses", "DarkSightRange", 5f, new ConfigDescription("How far an NPC sees in full darkness, m. Dusk and moonlight lie in between.", new AcceptableValueRange<float>(0.5f, 500f)));
            DaylightIntensity = Config.Bind("Senses", "DaylightIntensity", 0f, new ConfigDescription("Main-light intensity that counts as full daylight. 0 = Enviro's own sun setting. Raise it if nights feel too bright to NPCs, lower it if days feel dark (VerboseLog prints the light reading every minute).", new AcceptableValueRange<float>(0f, 20f)));
            NoticeSeconds = Config.Bind("Senses", "NoticeSeconds", 0.2f, new ConfigDescription("How long a target has to be in view before the NPC reacts, s.", new AcceptableValueRange<float>(0f, 5f)));
            LoseSeconds = Config.Bind("Senses", "LoseSeconds", 0.3f, new ConfigDescription("How long a target can be out of view before the NPC counts it as lost and goes to where it last saw it, s.", new AcceptableValueRange<float>(0f, 10f)));
            SearchSeconds = Config.Bind("Senses", "SearchSeconds", 30f, new ConfigDescription("How long an NPC looks around at the place it went to check before it loses interest, s.", new AcceptableValueRange<float>(0f, 120f)));
            PursuitMin = Config.Bind("Senses", "PursuitMin", 1, new ConfigDescription("An NPC that loses you from sight goes to where it last saw you and, finding nothing, follows to where you really are this many times at least before it starts searching.", new AcceptableValueRange<int>(0, 10)));
            PursuitMax = Config.Bind("Senses", "PursuitMax", 3, new ConfigDescription("... and this many times at most (rolled per chase).", new AcceptableValueRange<int>(0, 10)));
            GhostTimeout = Config.Bind("Senses", "GhostTimeout", 60f, new ConfigDescription("An NPC walking to a spot it goes to check (a ghost) keeps going until it gets there; only if this many seconds pass with no news about that spot (the ghost not renewed or moved) does it give up the walk and search from where it is, s.", new AcceptableValueRange<float>(5f, 600f)));
            ArriveDistance = Config.Bind("Senses", "ArriveDistance", 1.5f, new ConfigDescription("How close to the remembered spot counts as being there, m.", new AcceptableValueRange<float>(0.5f, 10f)));
            LookInterval = Config.Bind("Senses", "LookInterval", 0.15f, new ConfigDescription("How often each NPC looks, s (a couple of rays per look).", new AcceptableValueRange<float>(0.05f, 2f)));
            MuffleSounds = Config.Bind("Senses", "MuffleSounds", false, "Walls muffle sounds: an NPC with no line to the sound hears it only within MuffleFactor % of the range (one extra ray per NPC in range).");
            MuffleFactor = Config.Bind("Senses", "MuffleFactor", 50f, new ConfigDescription("Hearing range through walls, % of the open-air range (with MuffleSounds).", new AcceptableValueRange<float>(0f, 100f)));
            ShotRangePistol = Config.Bind("Senses", "ShotRangePistol", 80f, new ConfigDescription("A pistol or revolver shot is heard this far, m.", new AcceptableValueRange<float>(0f, 1000f)));
            ShotRangeSmg = Config.Bind("Senses", "ShotRangeSmg", 120f, new ConfigDescription("An SMG shot is heard this far, m.", new AcceptableValueRange<float>(0f, 1000f)));
            ShotRangeRifle = Config.Bind("Senses", "ShotRangeRifle", 150f, new ConfigDescription("A rifle or machine-gun shot is heard this far, m.", new AcceptableValueRange<float>(0f, 1000f)));
            ShotRangeSniper = Config.Bind("Senses", "ShotRangeSniper", 150f, new ConfigDescription("A sniper rifle shot is heard this far, m.", new AcceptableValueRange<float>(0f, 1000f)));
            ShotRangeShotgun = Config.Bind("Senses", "ShotRangeShotgun", 150f, new ConfigDescription("A shotgun blast is heard this far, m.", new AcceptableValueRange<float>(0f, 1000f)));
            ShotRangeCrossbow = Config.Bind("Senses", "ShotRangeCrossbow", 15f, new ConfigDescription("A crossbow shot is heard this far, m.", new AcceptableValueRange<float>(0f, 1000f)));
            NpcShotRanges = Config.Bind("Senses", "NpcShotRanges", "Flexa=150, Gungirl=150, Lugnut=150, Scrud=150, Boltjaw=120, Sprokka=80, Pistoleer=80, Gunnar=150, Lugger=150",
                "How far each NPC type's gunfire is heard, m, as Type=metres pairs; a type not listed uses the range of its weapon class above.");
            TauntRange = Config.Bind("Senses", "TauntRange", 15f, new ConfigDescription("A human's shout passes on what it knows to same-faction humans within this range - what it sees (as sight) or the spot it is going to check (at that spot's own rank) - and tells its enemies where it stands, m. 0 = off.", new AcceptableValueRange<float>(0f, 200f)));
            ShoutKey = Config.Bind("Senses", "ShoutKey", Key.Q,
                "Your shout (with ShoutModifier held): you yell like a raider and every NPC hostile to you within PlayerShoutRange comes to check (a SHOUT, below gunshots). None = off.");
            ShoutModifier = Config.Bind("Senses", "ShoutModifier", Key.LeftAlt,
                "Hold this and press ShoutKey to shout (either Alt works when this is an Alt key). While it is held, the game's own buttons on the same key (ShoutBlocksButtons) do nothing, so Alt+Q shouts without kicking. None = ShoutKey alone.");
            ShoutBlocksButtons = Config.Bind("Senses", "ShoutBlocksButtons", "Kick,ShiftDown",
                "The game's input buttons (Input Manager names) ignored while ShoutModifier is held.");
            PlayerShoutRange = Config.Bind("Senses", "PlayerShoutRange", 25f, new ConfigDescription("How far your shout carries, m.", new AcceptableValueRange<float>(0f, 300f)));
            ShoutCooldown = Config.Bind("Senses", "ShoutCooldown", 1.5f, new ConfigDescription("Shortest time between two of your shouts, s.", new AcceptableValueRange<float>(0f, 30f)));
            ShoutVolume = Config.Bind("Senses", "ShoutVolume", 1f, new ConfigDescription("Volume of your shout.", new AcceptableValueRange<float>(0f, 1f)));
            HumanFactions = Config.Bind("Senses", "HumanFactions", "Scrapyard,Coyotes", "Which factions (object tags) count as humans for taunts.");
            EngineMinRange = Config.Bind("Senses", "EngineMinRange", 50f, new ConfigDescription("Your running engine is heard this far with the weakest engine (EngineMinHp), m.", new AcceptableValueRange<float>(0f, 1000f)));
            EngineMaxRange = Config.Bind("Senses", "EngineMaxRange", 150f, new ConfigDescription("... and this far with the strongest (EngineMaxHp), m.", new AcceptableValueRange<float>(0f, 1000f)));
            EngineMinHp = Config.Bind("Senses", "EngineMinHp", 40f, new ConfigDescription("Horsepower that counts as the weakest engine.", new AcceptableValueRange<float>(1f, 2000f)));
            EngineMaxHp = Config.Bind("Senses", "EngineMaxHp", 300f, new ConfigDescription("Horsepower that counts as the strongest engine.", new AcceptableValueRange<float>(1f, 2000f)));
            EngineIdleFactor = Config.Bind("Senses", "EngineIdleFactor", 50f, new ConfigDescription("Engine range while idling (no throttle, standing), % of the driving range. A switched-off engine is silent.", new AcceptableValueRange<float>(0f, 100f)));
            BailOutAware = Config.Bind("Senses", "BailOutAware", true,
                "With Apocapatrol: a raider bailing out of a car keeps what its crew knew - it fights at once if it sees you, otherwise it heads for where you were and searches.");
            BailOutAwareRange = Config.Bind("Senses", "BailOutAwareRange", 150f, new ConfigDescription("With Apocapatrol: a crew bailing out knows where you are if you are within this range of its car, m.", new AcceptableValueRange<float>(0f, 1000f)));
            ExplosionRange = Config.Bind("Senses", "ExplosionRange", 150f, new ConfigDescription("With Apocapatrol: an exploding raider car is heard this far (like a gunshot), m. 0 = silent.", new AcceptableValueRange<float>(0f, 1000f)));
            BlastRange = Config.Bind("Senses", "BlastRange", 150f, new ConfigDescription("Grenades, blast lances, Blast Rats and Blast Zombies exploding are heard this far (like a gunshot), m. 0 = silent.", new AcceptableValueRange<float>(0f, 1000f)));
            BlastPrefabs = Config.Bind("Senses", "BlastPrefabs", "Explosion_Grenade,Explosion_Can,Explosion_BlastRat,Explosion_BlastZombie", "Explosion prefabs heard within BlastRange when the game spawns them (Explosion_Grenade = grenade + blast lance, Explosion_Can = stronger blast lance, Explosion_BlastRat / Explosion_BlastZombie = the exploding creatures).");
            ThrowRange = Config.Bind("Senses", "ThrowRange", 10f, new ConfigDescription("An item you throw draws NPCs within this range of where it lands, m. 0 = off.", new AcceptableValueRange<float>(0f, 200f)));
            NavEnabled = Config.Bind("Nav", "Enabled", true,
                "NPCs know the camps, buildings and caves: each one near you is mapped once (spread over frames) and NPCs inside it take the real way out through its exits and around its walls, spikes and props, instead of feeling their way. Off = feelers only.");
            NavBakeRange = Config.Bind("Nav", "BakeRange", 200f, new ConfigDescription("A structure is mapped when you come within this distance of it, m.", new AcceptableValueRange<float>(30f, 1000f)));
            NavCellSize = Config.Bind("Nav", "CellSize", 0.5f, new ConfigDescription("Map resolution, m (smaller = narrower gaps found, slower mapping). Very large structures get coarser cells automatically.", new AcceptableValueRange<float>(0.25f, 2f)));
            NavMargin = Config.Bind("Nav", "Margin", 4f, new ConfigDescription("Open ground mapped around a structure's outline, m.", new AcceptableValueRange<float>(1f, 20f)));
            NavMaxStep = Config.Bind("Nav", "MaxStep", 0.45f, new ConfigDescription("Largest height step between neighbouring map cells an NPC can walk, m.", new AcceptableValueRange<float>(0.1f, 2f)));
            NavBakeBudgetMs = Config.Bind("Nav", "BakeBudgetMs", 1f, new ConfigDescription("CPU time per frame spent mapping a structure, ms.", new AcceptableValueRange<float>(0.2f, 10f)));
            NavFieldSeconds = Config.Bind("Nav", "FieldSeconds", 1f, new ConfigDescription("How long a computed route to one goal is reused by every NPC heading there, s.", new AcceptableValueRange<float>(0.2f, 10f)));
            SpawnKey = Config.Bind("Debug", "SpawnKey", Key.F9,
                "Spawns a Gungirl 6 m in front of you (a real raider: she fights and is saved). None = off.");
            DamageNumbers = Config.Bind("Hud", "DamageNumbers", 2, new ConfigDescription(
                "Damage your bullets do, shown as: 0 = nothing, 1 = a red list in the top right corner, 2 = numbers floating up from the hit point. A '!' marks a headshot.",
                new AcceptableValueRange<int>(0, 2)));
            DamageFontSize = Config.Bind("Hud", "DamageFontSize", 14, new ConfigDescription("Font size of the damage numbers, px.", new AcceptableValueRange<int>(8, 40)));
            HitMarker = Config.Bind("Hud", "HitMarker", true, "A red diagonal cross flashes at the screen centre when your bullet hits a creature (yellow for a headshot).");
            HitMarkerSize = Config.Bind("Hud", "HitMarkerSize", 22, new ConfigDescription("Hit marker size, px.", new AcceptableValueRange<int>(6, 100)));
            HitLog = Config.Bind("Debug", "HitLog", false, "Log every bullet hit on a creature: who, what, distance, damage, and its Health before and after.");
            VerboseLog = Config.Bind("Debug", "VerboseLog", false, "Log every Gungirl that is dressed (spawn, corpse, after a load).");
            NavLog = Config.Bind("Debug", "NavLog", false, "Log the structures found and mapped (size, cells, time taken).");
            NavDump = Config.Bind("Debug", "NavDump", false, "Save each structure map as a picture when it is baked, and again (with the NPC, its reachable area, the goal and the exit marked) when an NPC finds no map route: BepInEx/config/Apocaraiders/NavDump/*.bmp.");
            ShowNav = Config.Bind("Debug", "ShowNav", false, "Draw each nearby structure's mapping state and, in cyan, the next map waypoint of every NPC routing through one.");
            SensesLog = Config.Bind("Debug", "SensesLog", false, "Log every detection event: who sees, hears, loses, searches, gives up; every ghost made.");
            ShowGhosts = Config.Bind("Debug", "ShowGhosts", false, "Draw the ghosts in the world (a diamond and a label: number, source, what it is about, holders, age) and each alert NPC's state above its head. Colours: red sight (own, or passed on by a friend's shout), magenta hit, yellow gunshot, green thrown item, orange shout (an enemy NPC's or yours), blue engine. Ranks: sight > gunshot (and hits, explosions, thrown items) > shout > engine.");
            BrainLog = Config.Bind("Debug", "BrainLog", false, "Log every NPC movement decision (chase, hold, advance, stuck, rest) with the reason and distance.");

            try
            {
                new Harmony(GUID).Patch(AccessTools.Method(typeof(CreateObject), "OnEnter"),
                    postfix: new HarmonyMethod(typeof(Gungirl), nameof(Gungirl.AfterCreateObject)));
            }
            catch (Exception e) { Log.LogError("Harmony patch failed, Gungirls won't spawn: " + e); }
            try
            {
                var h = new Harmony(GUID + ".tracers");
                h.Patch(AccessTools.Method(typeof(Micosmo.SensorToolkit.PlayMaker.SensorGetDetectionRayHit), "OnEnter"),
                    prefix: new HarmonyMethod(typeof(Tracers), nameof(Tracers.BeforeRayHit)));
                h.Patch(AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.Raycast), "OnEnter"),
                    prefix: new HarmonyMethod(typeof(Tracers), nameof(Tracers.BeforeRaycast)));
                h.Patch(AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.SendEvent), "OnEnter"),
                    prefix: new HarmonyMethod(typeof(Aim), nameof(Aim.BeforeSendEvent)));
                h.Patch(AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.RandomWait), "OnEnter"),
                    prefix: new HarmonyMethod(typeof(Aim), nameof(Aim.BeforeRandomWait)));
            }
            catch (Exception e) { Log.LogError("Harmony patch failed, no tracers: " + e); }
            try
            {
                var h = new Harmony(GUID + ".brain");
                h.Patch(AccessTools.Method(typeof(SetVelocity), "DoSetVelocity"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeSetVelocity)));
                h.Patch(AccessTools.Method(typeof(Rotate), "DoRotate"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeRotate)));
                h.Patch(AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.Raycast), "DoRaycast"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeRaycast)));
                h.Patch(AccessTools.Method(typeof(LookAt), "DoLookAt"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeLookAt)));
                h.Patch(AccessTools.Method(typeof(SmoothLookAt), "DoSmoothLookAt"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeSmoothLookAt)));
                h.Patch(AccessTools.Method(typeof(SendEvent), "OnEnter"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeSendEvent)));
                h.Patch(AccessTools.Method(typeof(AddForce), "DoAddForce"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeAddForce)));
                h.Patch(AccessTools.Method(typeof(Micosmo.SensorToolkit.LOSSensor), "OnEnable"), postfix: new HarmonyMethod(typeof(Brain), nameof(Brain.AfterLosEnable)));
                h.Patch(AccessTools.Method(typeof(Micosmo.SensorToolkit.RangeSensor), "OnEnable"), postfix: new HarmonyMethod(typeof(Brain), nameof(Brain.AfterRangeEnable)));
            }
            catch (Exception e) { Log.LogError("Harmony patch failed, no NPC brain: " + e); }
            try
            {
                var h = new Harmony(GUID + ".senses");
                h.Patch(AccessTools.Method(typeof(Micosmo.SensorToolkit.PlayMaker.SensorGetDetections), "DoAction"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeGetDetections)));
                h.Patch(AccessTools.Method(typeof(Micosmo.SensorToolkit.PlayMaker.SensorGetLineOfSightResult), "OnEnter3D"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeLosResult)));
                h.Patch(AccessTools.Method(typeof(Micosmo.SensorToolkit.PlayMaker.SensorGetLineOfSightResult), "OnUpdate3D"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeLosResult)));
                h.Patch(AccessTools.Method(typeof(AudioPlay), "OnEnter"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeAudioPlay)));
                h.Patch(AccessTools.Method(typeof(CreateObject), "OnEnter"), postfix: new HarmonyMethod(typeof(Senses), nameof(Senses.AfterCreateObject)));
                h.Patch(AccessTools.Method(typeof(GetButton), "DoGetButton"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeGetButton)));
                h.Patch(AccessTools.Method(typeof(GetButtonDown), "OnUpdate"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeGetButtonDown)));
                h.Patch(AccessTools.Method(typeof(GetButtonUp), "OnUpdate"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeGetButtonUp)));
            }
            catch (Exception e) { Log.LogError("Harmony patch failed, no senses: " + e); }

            SceneManager.sceneLoaded += (s, m) => { EnsureRunner(); Gungirl.OnSceneLoaded(); Tracers.OnSceneLoaded(); Brain.OnSceneLoaded(); Senses.OnSceneLoaded(); Nav.OnSceneLoaded(); };
            EnsureRunner();
            Log.LogInfo(NAME + " " + VERSION + " loaded");
        }

        private static void EnsureRunner()
        {
            if (_runner != null) return;
            _runner = new GameObject("Apocaraiders.Runner") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_runner);
            _runner.AddComponent<Runner>();
        }

        internal static void Verbose(string s) { if (VerboseLog != null && VerboseLog.Value && Log != null) Log.LogInfo(s); }
        internal static void Warn(string s) { if (Log != null) Log.LogWarning(s); }
    }

    internal class Runner : MonoBehaviour
    {
        private void Update() { Voice.EnsureLoading(this); Gungirl.Tick(); Tracers.Tick(); try { Senses.Tick(this); } catch (Exception e) { Plugin.Log.LogError("Senses: " + e); } try { Nav.Tick(); } catch (Exception e) { Plugin.Log.LogError("Nav: " + e); } Brain.Tick(); }
        private void LateUpdate() { try { Brain.LateTick(); } catch (Exception e) { Plugin.Log.LogError("Brain: " + e); } }
        private void OnGUI() { try { Hud.OnGUI(); } catch (Exception e) { Plugin.Log.LogError("Hud: " + e); } }
    }
}
