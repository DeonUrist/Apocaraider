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
        public const string VERSION = "0.3.1";

        internal static ManualLogSource Log;
        internal static string Dir;

        internal static ConfigEntry<bool> Enabled, VerboseLog, GungirlVoiceMatch;
        internal static ConfigEntry<float> GungirlVoiceVolume, GungirlVoiceIntervalMin, GungirlVoiceIntervalMax;
        internal static ConfigEntry<int> GungirlChance;
        internal static ConfigEntry<string> GungirlModel, GungirlTexture, GungirlVoice, GungirlHideParts;
        internal static ConfigEntry<Key> SpawnKey;
        internal static ConfigEntry<bool> TracersEnabled, VehicleDamage, PlayerTracers;
        internal static ConfigEntry<float> BulletSpeed, BoltSpeed, TracerWidth, TracerLength, BoltWidth, BoltLength, TracerGlow;
        internal static ConfigEntry<Color> TracerColor, BoltColor;
        internal static ConfigEntry<float> PistolRange, SmgRange, RifleRange, SniperRange, ShotgunRange, CrossbowRange, ShotgunPelletSpread, MetalSheetPopChance, NpcHitRadius;
        internal static ConfigEntry<int> ShotgunPellets, MaxTracers;
        internal static ConfigEntry<string> MetalSheetNames;

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
            GungirlHideParts = Config.Bind("Gungirl", "HideParts", "beard",
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
            TracerWidth = Config.Bind("Tracers", "TracerWidth", 0.03f, new ConfigDescription("Tracer line width, m.", new AcceptableValueRange<float>(0.005f, 0.5f)));
            TracerLength = Config.Bind("Tracers", "TracerLength", 4f, new ConfigDescription("Tracer line length, m.", new AcceptableValueRange<float>(0.1f, 30f)));
            BoltWidth = Config.Bind("Tracers", "BoltWidth", 0.03f, new ConfigDescription("Bolt line width, m.", new AcceptableValueRange<float>(0.005f, 0.5f)));
            BoltLength = Config.Bind("Tracers", "BoltLength", 0.8f, new ConfigDescription("Bolt line length, m.", new AcceptableValueRange<float>(0.1f, 5f)));
            PistolRange = Config.Bind("Tracers", "PistolRange", 60f, new ConfigDescription("Pistols/revolvers: damage falls off linearly with distance - half at 50 % of this range, the bullet is gone at 100 %. Metres.", new AcceptableValueRange<float>(5f, 1000f)));
            SmgRange = Config.Bind("Tracers", "SmgRange", 70f, new ConfigDescription("SMGs, falloff range in metres (half damage at half range).", new AcceptableValueRange<float>(5f, 1000f)));
            RifleRange = Config.Bind("Tracers", "RifleRange", 120f, new ConfigDescription("Automatic rifles and machine guns, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            SniperRange = Config.Bind("Tracers", "SniperRange", 250f, new ConfigDescription("Sniper/scoped rifles, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            ShotgunRange = Config.Bind("Tracers", "ShotgunRange", 50f, new ConfigDescription("Shotguns, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            CrossbowRange = Config.Bind("Tracers", "CrossbowRange", 90f, new ConfigDescription("Crossbows, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            ShotgunPellets = Config.Bind("Tracers", "ShotgunPellets", 2, new ConfigDescription("Pellets per vanilla shotgun ray (a vanilla blast is 4 rays; 2 = 8 pellets). The blast's damage is split between them.", new AcceptableValueRange<int>(1, 8)));
            ShotgunPelletSpread = Config.Bind("Tracers", "ShotgunPelletSpread", 1f, new ConfigDescription("Extra spread of each pellet, degrees.", new AcceptableValueRange<float>(0f, 15f)));
            NpcHitRadius = Config.Bind("Tracers", "NpcHitRadius", 0.12f, new ConfigDescription(
                "Thickness of NPC bullets, m: a bullet passing this close to you (or another target) counts as a hit. The player's body collider is only 0.4 m wide. 0 = hairline. Your own bullets are always a hairline.",
                new AcceptableValueRange<float>(0f, 0.5f)));
            VehicleDamage = Config.Bind("Tracers", "VehicleDamage", true, "A bullet that hits a vehicle part damages it: 10 damage = 1 % of the part's condition.");
            MetalSheetPopChance = Config.Bind("Tracers", "MetalSheetPopChance", 20f, new ConfigDescription("% chance that a bullet hitting a bolted-on metal plate knocks it off.", new AcceptableValueRange<float>(0f, 100f)));
            MetalSheetNames = Config.Bind("Tracers", "MetalSheetNames", "metal_plate", "Which attached parts count as metal sheets (comma-separated name starts).");
            MaxTracers = Config.Bind("Tracers", "MaxTracers", 300, new ConfigDescription("Most bullets in flight at once; shots above this hit instantly (vanilla style) instead.", new AcceptableValueRange<int>(16, 2000)));
            // 0.3.0 shipped ShotgunRange 35 / ShotgunPelletSpread 3 (too few hits); a cfg still at those values gets the new defaults
            if (Mathf.Approximately(ShotgunRange.Value, 35f)) ShotgunRange.Value = 50f;
            if (Mathf.Approximately(ShotgunPelletSpread.Value, 3f)) ShotgunPelletSpread.Value = 1f;
            SpawnKey = Config.Bind("Debug", "SpawnKey", Key.F9,
                "Spawns a Gungirl 6 m in front of you (a real raider: she fights and is saved). None = off.");
            VerboseLog = Config.Bind("Debug", "VerboseLog", false, "Log every Gungirl that is dressed (spawn, corpse, after a load).");

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
            }
            catch (Exception e) { Log.LogError("Harmony patch failed, no tracers: " + e); }

            SceneManager.sceneLoaded += (s, m) => { EnsureRunner(); Gungirl.OnSceneLoaded(); Tracers.OnSceneLoaded(); };
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
        private void Update() { Voice.EnsureLoading(this); Gungirl.Tick(); Tracers.Tick(); }
    }
}
