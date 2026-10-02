#!/bin/sh
# Builds Apocaraider.dll against the game's own libraries. Usage: ./build.sh [out.dll]
M=${MANAGED:-/e/SteamLibrary/steamapps/common/Apocalypter/Apocalypter_Data/Managed}; B=${BEPCORE:-/e/SteamLibrary/steamapps/common/Apocalypter/BepInEx/core}
mcs -nostdlib -noconfig -target:library -langversion:latest -optimize+ -out:${1:-Apocaraider.dll} \
  -r:$M/mscorlib.dll -r:$M/System.dll -r:$M/System.Core.dll -r:$M/netstandard.dll \
  -r:$B/BepInEx.dll -r:$B/0Harmony.dll \
  -r:$M/UnityEngine.dll -r:$M/UnityEngine.CoreModule.dll -r:$M/UnityEngine.PhysicsModule.dll \
  -r:$M/UnityEngine.ImageConversionModule.dll -r:$M/UnityEngine.AudioModule.dll -r:$M/UnityEngine.UnityWebRequestModule.dll -r:$M/UnityEngine.UnityWebRequestAudioModule.dll -r:$M/UnityEngine.IMGUIModule.dll -r:$M/UnityEngine.TextRenderingModule.dll -r:$M/UnityEngine.ParticleSystemModule.dll -r:$M/UnityEngine.AnimationModule.dll -r:$M/Unity.InputSystem.dll -r:$M/PlayMaker.dll -r:$M/Micosmo.SensorToolkit.dll -r:$M/Assembly-CSharp.dll -r:$M/Assembly-CSharp-firstpass.dll \
  Plugin.cs Gungirl.cs Voice.cs Wav.cs Level.cs Tracers.cs Aim.cs Brain.cs Senses.cs Nav.cs Bosses.cs Storm.cs Idle.cs Passthrough.cs Hud.cs Gltf.cs Json.cs Bindposes.cs
