using System;
using UnityEditor;
using UnityEngine;
namespace YAMO.UnityTools.Editor {
[InitializeOnLoad]
public static class YamoSelectionTargetGUI {
 static YamoSelectionTargetGUI(){Selection.selectionChanged += RepaintTools;}
 static void RepaintTools(){foreach(var w in Resources.FindObjectsOfTypeAll<EditorWindow>()) if(w && w.GetType().Namespace != null && w.GetType().Namespace.StartsWith("YAMO.UnityTools")) w.Repaint();}
 public static UnityEngine.Object Resolve(Type type, bool allowSceneObjects){
  return ResolveObject(Selection.activeObject,type,allowSceneObjects);
 }
 public static UnityEngine.Object ResolveObject(UnityEngine.Object selected,Type type,bool allowSceneObjects){
  var selectedGo=selected as GameObject; if(!selectedGo && selected is Component component)selectedGo=component.gameObject;
  if(typeof(Component).IsAssignableFrom(type)) {selected=selectedGo?selectedGo.GetComponent(type):null;}
  else if(type==typeof(GameObject)) selected=selectedGo;
  if(!selected || !type.IsInstanceOfType(selected)) return null;
  if(!allowSceneObjects && !EditorUtility.IsPersistent(selected)) return null;
  return selected;
 }
 public static UnityEngine.Object Draw(string label,UnityEngine.Object previous,Type type,bool allowSceneObjects){
  var selected=Resolve(type,allowSceneObjects);
  if(previous!=selected) GUI.changed=true;
  EditorGUILayout.LabelField(new GUIContent(label+" (현재 선택)"),new GUIContent(selected?selected.name:"선택한 대상 없음",selected?AssetDatabase.GetAssetPath(selected):"Hierarchy 또는 Project에서 작업 대상을 선택하세요."));
  return selected;
 }
 public static UnityEngine.Object Draw(GUIContent label,UnityEngine.Object previous,Type type,bool allowSceneObjects)=>Draw(label.text,previous,type,allowSceneObjects);
}
}
