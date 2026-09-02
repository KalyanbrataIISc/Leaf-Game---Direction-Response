#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace LeafGame.Editor
{
    [CustomEditor(typeof(LeafGameController))]
    public sealed class LeafGameControllerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Neurofeedback Source", EditorStyles.boldLabel);
            SerializedProperty sourceProperty=serializedObject.FindProperty("nfSourceType");
            SerializedProperty pathProperty=serializedObject.FindProperty("nfFilePath");
            if(sourceProperty==null||pathProperty==null)
            {
                EditorGUILayout.HelpBox("The NF file path field could not be found.", MessageType.Error);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            if(sourceProperty.enumValueIndex==0)
            {
                using(new EditorGUILayout.HorizontalScope())
                {
                    if(GUILayout.Button("Choose NF File..."))
                    {
                        string directory=Application.persistentDataPath;
                        string current=pathProperty.stringValue;
                        if(!string.IsNullOrWhiteSpace(current)&&Path.IsPathRooted(current))
                        {
                            string parent=Path.GetDirectoryName(current);
                            if(!string.IsNullOrEmpty(parent)&&Directory.Exists(parent))directory=parent;
                        }

                        string selected=EditorUtility.OpenFilePanel("Choose neurofeedback binary file",directory,"txt");
                        if(!string.IsNullOrEmpty(selected))pathProperty.stringValue=selected;
                    }

                    if(GUILayout.Button("Use persistent-data nf.txt"))pathProperty.stringValue="nf.txt";
                }
            }

            EditorGUILayout.HelpBox(
                "These serialized Inspector values are saved in the scene and included in the APK. TCP expects the same repeated 24-byte records as gameNFv9.m: three little-endian 64-bit doubles. File paths remain relative to Application.persistentDataPath unless absolute.",
                MessageType.Info);

            if(serializedObject.ApplyModifiedProperties())EditorUtility.SetDirty(serializedObject.targetObject);
        }
    }
}
#endif
