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
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.apocaraiders";
        public const string NAME = "Apocaraiders";
        public const string VERSION = "0.2.0";

        internal static ManualLogSource Log;
        internal static string Dir;

        internal static ConfigEntry<bool> Enabled, VerboseLog;
        internal static ConfigEntry<int> GungirlChance;
        internal static ConfigEntry<string> GungirlModel, GungirlTexture, GungirlVoice, GungirlHideParts;
        internal static ConfigEntry<Key> SpawnKey;

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
                "Folder with her voice, relative to the mod folder: WAV files named like Flexa's clips (death_1.wav, human_hurt.wav, enemy_human_single_1.wav ...). A clip without a file keeps Flexa's sound. Read at game start.");
            GungirlHideParts = Config.Bind("Gungirl", "HideParts", "beard",
                "Flexa's attachments to hide on a Gungirl, comma-separated name starts: beard, headband, armband, bag1, pouch1, armor2, machete.");
            SpawnKey = Config.Bind("Debug", "SpawnKey", Key.F9,
                "Spawns a Gungirl 6 m in front of you (a real raider: she fights and is saved). None = off.");
            VerboseLog = Config.Bind("Debug", "VerboseLog", false, "Log every Gungirl that is dressed (spawn, corpse, after a load).");

            try
            {
                new Harmony(GUID).Patch(AccessTools.Method(typeof(CreateObject), "OnEnter"),
                    postfix: new HarmonyMethod(typeof(Gungirl), nameof(Gungirl.AfterCreateObject)));
            }
            catch (Exception e) { Log.LogError("Harmony patch failed, Gungirls won't spawn: " + e); }

            SceneManager.sceneLoaded += (s, m) => { EnsureRunner(); Gungirl.OnSceneLoaded(); };
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
        private void Update() { Gungirl.Tick(); }
    }
}
