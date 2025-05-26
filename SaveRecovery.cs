using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Xml;
using Assets.Scripts.Objects;
using Assets.Scripts.Serialization;
using Assets.Scripts.Util;
using HarmonyLib;
using StationeersMods.Interface;
using UnityEngine;

namespace SaveRecovery
{
  [StationeersMod("SaveRecovery", "SaveRecovery", "0.1.1")]
  class SaveRecovery : ModBehaviour
  {
    public override void OnLoaded(ContentHandler contentHandler)
    {
      base.OnLoaded(contentHandler);

      var harmony = new Harmony("SaveRecovery");
      harmony.PatchAll();
    }
  }

  [HarmonyPatch(typeof(XmlSaveLoad))]
  static class XmlSaveLoadPatch
  {
    [HarmonyPatch("LoadWorld"), HarmonyPrefix]
    static void LoadWorld()
    {
      var existingTypes = new HashSet<string>();
      foreach (var type in XmlSaveLoad.ExtraTypes)
        existingTypes.Add(type.Name);

      var missingTypes = new HashSet<string>();

      // search the save file for any missing savedata types
      var fname = XmlSaveLoad.Instance.CurrentWorldSave.World.FullName;
      using (var streamReader = new StreamReader(fname, Encoding.GetEncoding("UTF-8")))
      using (var reader = XmlReader.Create(streamReader))
      {
        while (reader.Read())
        {
          if (reader.NodeType != XmlNodeType.Element || reader.Name != "ThingSaveData" || !reader.HasAttributes)
            continue;
          if (!reader.MoveToAttribute("xsi:type"))
            continue;
          if (!existingTypes.Contains(reader.Value))
            missingTypes.Add(reader.Value);
        }
      }

      AddExtraTypes(missingTypes);

      _failedThings.Clear();
    }

    private static int _assemblyIndex = 0;
    private static void AddExtraTypes(HashSet<string> names)
    {
      // generate an assembly with an empty type extending ThingSaveData for each missing type
      var index = _assemblyIndex++;
      var name = $"SaveDataPatch{index}";
      var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.Run);
      var module = assembly.DefineDynamicModule(name);

      var extraTypes = XmlSaveLoad.ExtraTypes.ToList();

      foreach (var typeName in names)
      {
        Debug.Log($"generating missing type: {typeName}");
        var type = module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class, typeof(ThingSaveData));
        extraTypes.Add(type.CreateType());
      }

      // add our generated types to ExtraTypes and clear the world data serializer so it gets remade
      XmlSaveLoad.ExtraTypes = extraTypes.ToArray();
      typeof(Serializers).GetField("_worldData", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
    }

    private static HashSet<long> _failedThings = new();
    [HarmonyPatch("LoadThing"), HarmonyPrefix]
    static void LoadThingPrefix(ThingSaveData thingData)
    {
      // If the parent failed to load, drop this in the world
      if (thingData is DynamicThingSaveData dynamicData && _failedThings.Contains(dynamicData.ParentReferenceId))
        dynamicData.ParentReferenceId = 0;
    }

    [HarmonyPatch("LoadThing"), HarmonyPostfix]
    static void LoadThingPostfix(ThingSaveData thingData, ref Thing __result)
    {
      // If this thing didn't load for whatever reason, save its ID so any children aren't waiting for it
      if (__result == null)
        _failedThings.Add(thingData.ReferenceId);
    }
  }
}